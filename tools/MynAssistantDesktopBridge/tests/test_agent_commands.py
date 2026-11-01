from __future__ import annotations

import importlib.util
import json
import sys
from datetime import datetime, timedelta, timezone
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

AGENT_PATH = Path(__file__).resolve().parents[1] / "agent.py"
sys.path.insert(0, str(AGENT_PATH.parent))
SPEC = importlib.util.spec_from_file_location("mynassistant_agent", AGENT_PATH)
assert SPEC is not None and SPEC.loader is not None
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)


class FakeResponse:
    def __init__(self, status_code: int, payload: dict | None = None, text: str = ""):
        self.status_code = status_code
        self._payload = payload or {}
        self.text = text

    def json(self) -> dict:
        return self._payload


class FakeSession:
    uid = "test-user"

    def headers(self) -> dict:
        return {"Authorization": "Bearer test-token"}


def firestore_doc(fields: dict, update_time: str = "2026-10-22T10:00:00.000000Z") -> dict:
    encoded = {}
    for key, value in fields.items():
        if isinstance(value, bool):
            encoded[key] = {"booleanValue": value}
        elif isinstance(value, int):
            encoded[key] = {"integerValue": str(value)}
        else:
            encoded[key] = {"stringValue": str(value)}
    return {
        "name": "projects/myncloud-24/databases/(default)/documents/commands/cmd-1",
        "updateTime": update_time,
        "fields": encoded,
    }


class FakeScreenshotImage:
    def __init__(self, sizes_by_quality: dict[int, int]):
        self.sizes_by_quality = sizes_by_quality
        self.qualities_used: list[int] = []

    def thumbnail(self, size: tuple[int, int]) -> None:
        self.thumbnail_size = size

    def save(self, output, format: str, quality: int, optimize: bool) -> None:
        self.qualities_used.append(quality)
        output.write(b"x" * self.sizes_by_quality[quality])


class DpapiAndDriveDiscoveryTests(unittest.TestCase):
    def test_crypt_protect_data_bytes_are_saved_without_tuple_unpacking(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            app_data = Path(temporary)
            session_file = app_data / "session.bin"
            with (
                mock.patch.object(agent, "APP_DATA", app_data),
                mock.patch.object(agent, "SESSION_FILE", session_file),
                mock.patch.object(agent.win32crypt, "CryptProtectData", return_value=b"dpapi-ciphertext") as protect,
            ):
                agent.protect_session({"uid": "test-user", "refreshToken": "refresh-token"})

            self.assertEqual(session_file.read_bytes(), b"dpapi-ciphertext")
            protect.assert_called_once()

    def test_crypt_unprotect_data_keeps_pywin32_two_value_return(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            session_file = Path(temporary) / "session.bin"
            session_file.write_bytes(b"dpapi-ciphertext")
            payload = {"uid": "test-user", "refreshToken": "refresh-token"}
            raw = json.dumps(payload).encode("utf-8")
            with (
                mock.patch.object(agent, "SESSION_FILE", session_file),
                mock.patch.object(agent.win32crypt, "CryptUnprotectData", return_value=("MynAssistant", raw)),
            ):
                self.assertEqual(agent.load_protected_session(), payload)

    def test_drive_mask_discovers_every_logical_volume_letter(self) -> None:
        mask = sum(1 << index for index in (2, 3, 4, 5, 7, 25))
        self.assertEqual(
            agent.logical_drive_letters(mask),
            ["C", "D", "E", "F", "H", "Z"],
        )

    def test_allowed_roots_include_mounted_drives_and_keep_user_shortcuts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            drive = root / "volume-c"
            profile = root / "profile"
            drive.mkdir()
            profile.mkdir()
            (profile / "Documents").mkdir()
            with (
                mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}),
                mock.patch.object(agent, "_windows_storage_roots", return_value=[("C:", drive), ("D:", root)]),
            ):
                roots = agent._allowed_file_roots()

            self.assertEqual([label for label, _ in roots], ["C:", "D:", "Belgeler"])
            self.assertEqual(roots[0][1], drive)
            self.assertEqual(roots[2][1], (profile / "Documents").resolve())


class ServerRelayCommandTests(unittest.TestCase):
    def setUp(self) -> None:
        self.session = FakeSession()
        self.document = {
            "id": "cmd-1",
            "updateTime": "2026-10-22T10:00:00.000000Z",
            "fields": {
                "ownerId": "test-user",
                "deviceId": "win-test",
                "command": "volume_up",
                "status": "pending",
            },
        }

    def test_screen_frame_compresses_before_server_upload(self) -> None:
        image = FakeScreenshotImage({45: 600_001, 30: 500_000})
        with mock.patch.object(agent.pyautogui, "screenshot", return_value=image):
            encoded = agent.capture_screen_base64()

        import base64
        self.assertEqual(image.qualities_used, [45, 30])
        self.assertEqual(len(base64.b64decode(encoded)), 500_000)
        self.assertLessEqual(len(encoded), 700_000)

    def test_screen_frame_is_rejected_if_low_quality_still_too_large(self) -> None:
        image = FakeScreenshotImage({45: 800_001, 30: 700_001})
        with mock.patch.object(agent.pyautogui, "screenshot", return_value=image):
            with self.assertRaisesRegex(RuntimeError, "güvenli aktarım"):
                agent.capture_screen_base64()
        self.assertEqual(image.qualities_used, [45, 30])

    def test_claim_refuses_document_without_server_update_time(self) -> None:
        document = dict(self.document)
        document.pop("updateTime")
        with mock.patch.object(agent, "server_request") as request:
            claimed = agent.claim_pending_command(self.session, document, "win-test")
        self.assertFalse(claimed)
        request.assert_not_called()

    def test_claim_is_authorized_by_server_and_stores_claim_token(self) -> None:
        with mock.patch.object(
            agent,
            "server_request",
            return_value={"status": "running", "claimToken": "server-issued-claim"},
        ) as request:
            claimed = agent.claim_pending_command(self.session, self.document, "win-test")

        self.assertTrue(claimed)
        self.assertEqual(self.document["_claimToken"], "server-issued-claim")
        request.assert_called_once_with(
            self.session,
            "POST",
            "/api/v1/pc/agent/devices/win-test/commands/cmd-1/claim",
            {
                "deviceId": "win-test",
                "expectedUpdateTime": self.document["updateTime"],
            },
            timeout=20,
        )

    def test_claim_fails_closed_when_server_rejects_claim(self) -> None:
        with mock.patch.object(
            agent, "server_request", side_effect=RuntimeError("Komut artık beklemede değil.")
        ) as request:
            claimed = agent.claim_pending_command(self.session, self.document, "win-test")
        self.assertFalse(claimed)
        self.assertNotIn("_claimToken", self.document)
        request.assert_called_once()

    def test_claim_without_token_never_executes_result_write(self) -> None:
        with mock.patch.object(agent, "server_request") as request:
            with self.assertRaisesRegex(RuntimeError, "sahiplenilmedi"):
                agent.update_command(self.session, self.document, "completed", "done")
        request.assert_not_called()

    def test_command_result_returns_to_server_with_claim_token(self) -> None:
        document = {
            **self.document,
            "_claimToken": "server-issued-claim",
            "fields": {**self.document["fields"], "command": "list_files"},
        }
        original_result = json.dumps({"entries": [{"name": "x" * 1000} for _ in range(10)]})
        with mock.patch.object(agent, "server_request") as request:
            agent.update_command(self.session, document, "completed", original_result)

        args = request.call_args.args
        self.assertEqual(args[0], self.session)
        self.assertEqual(args[1], "POST")
        self.assertEqual(
            args[2],
            "/api/v1/pc/agent/devices/win-test/commands/cmd-1/result",
        )
        self.assertEqual(args[3]["claimToken"], "server-issued-claim")
        self.assertEqual(args[3]["status"], "completed")
        self.assertEqual(args[3]["result"], original_result)
        self.assertGreater(len(args[3]["result"]), 800)

    def test_command_queue_is_fetched_from_server_for_exact_device(self) -> None:
        with mock.patch.object(
            agent,
            "server_request",
            return_value={"items": [self.document]},
        ) as request:
            result = agent.fetch_owner_commands(self.session, "win-test")

        self.assertEqual(result, [self.document])
        request.assert_called_once_with(
            self.session,
            "GET",
            "/api/v1/pc/agent/devices/win-test/commands",
            timeout=25,
        )


class RemoteKeyboardCommandTests(unittest.TestCase):
    def test_type_text_sends_unicode_without_touching_clipboard_and_can_submit(self) -> None:
        with mock.patch.object(agent, "send_unicode_text") as send_text, mock.patch.object(
            agent.pyautogui, "press"
        ) as press:
            result = agent.execute_command(
                "type_text",
                {"text": "İstanbul – merhaba 👋", "pressEnter": True},
            )

        send_text.assert_called_once_with("İstanbul – merhaba 👋")
        press.assert_called_once_with("enter")
        self.assertIn("Enter gönderildi", result)

    def test_type_text_does_not_press_enter_unless_requested(self) -> None:
        with mock.patch.object(agent, "send_unicode_text") as send_text, mock.patch.object(
            agent.pyautogui, "press"
        ) as press:
            result = agent.execute_command("type_text", {"text": "MynAssistant"})

        send_text.assert_called_once_with("MynAssistant")
        press.assert_not_called()
        self.assertIn("yazıldı", result)

    def test_type_text_rejects_empty_or_oversized_payload(self) -> None:
        with mock.patch.object(agent, "send_unicode_text") as send_text:
            empty_result = agent.execute_command("type_text", {"text": "   "})
            long_result = agent.execute_command(
                "type_text", {"text": "x" * (agent.MAX_REMOTE_TEXT_LENGTH + 1)}
            )

        send_text.assert_not_called()
        self.assertIn("metni gir", empty_result)
        self.assertIn("karakter", long_result)

    def test_backspace_and_enter_are_supported_keyboard_commands(self) -> None:
        with mock.patch.object(agent.pyautogui, "press") as press:
            backspace_result = agent.execute_command("key_backspace", {})
            enter_result = agent.execute_command("key_enter", {})

        self.assertEqual([call.args[0] for call in press.call_args_list], ["backspace", "enter"])
        self.assertIn("Backspace", backspace_result)
        self.assertIn("Enter", enter_result)
        for command in ("type_text", "key_backspace", "key_enter"):
            self.assertTrue(agent.command_is_supported(command))


class RemotePowerAndWindowCommandTests(unittest.TestCase):
    def test_lock_and_close_commands_are_supported_and_dispatched(self) -> None:
        with mock.patch.object(agent, "lock_workstation", return_value="Windows oturumu kilitlendi.") as lock, \
             mock.patch.object(agent, "close_active_app", return_value="notepad.exe uygulamasına güvenli kapatma isteği gönderildi.") as close:
            lock_result = agent.execute_command("lock_pc", {})
            close_result = agent.execute_command("close_app", {})

        lock.assert_called_once_with()
        close.assert_called_once_with()
        self.assertIn("kilitlendi", lock_result)
        self.assertIn("kapatma isteği", close_result)
        self.assertTrue(agent.command_is_supported("lock_pc"))
        self.assertTrue(agent.command_is_supported("close_app"))

    def test_natural_language_lock_and_close_commands_are_routed(self) -> None:
        with mock.patch.object(agent, "lock_workstation", return_value="Windows oturumu kilitlendi.") as lock, \
             mock.patch.object(agent, "close_active_app", return_value="notepad.exe kapatma isteği gönderildi.") as close:
            lock_result = agent.execute_command("text_command", {"text": "bilgisayarı kilitle"})
            close_result = agent.execute_command("text_command", {"text": "uygulamayı kapat"})

        lock.assert_called_once_with()
        close.assert_called_once_with()
        self.assertIn("kilitlendi", lock_result)
        self.assertIn("kapatma isteği", close_result)


class SafeFileListingCommandTests(unittest.TestCase):
    def setUp(self) -> None:
        # These tests cover profile-folder containment. Drive discovery is tested
        # separately so the Windows CI runner's C:/D: disks cannot widen the fixture.
        self.drive_roots_patch = mock.patch.object(agent, "_windows_storage_roots", return_value=[])
        self.drive_roots_patch.start()
        self.addCleanup(self.drive_roots_patch.stop)

    def test_root_listing_contains_only_existing_allowlisted_profile_folders(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            for folder in ("Desktop", "Documents", "Pictures", "Music", "Private"):
                (profile / folder).mkdir()
            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                payload = json.loads(agent.execute_command("list_files", {"path": ""}))

        names = {entry["name"] for entry in payload["entries"]}
        self.assertEqual(names, {"Masaüstü", "Belgeler", "Resimler", "Müzik"})
        self.assertIsNone(payload["parentPath"])
        self.assertTrue(agent.command_is_supported("list_files"))

    def test_profile_root_reparse_points_are_not_exposed(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            base = Path(temp_dir)
            profile = base / "profile"
            profile.mkdir()
            desktop = profile / "Desktop"
            desktop.mkdir()
            (profile / "Documents").mkdir()

            # Simulate Desktop being redirected through a symlink/junction.
            # This does not need elevated symlink privileges on Windows CI.
            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                with mock.patch.object(
                    agent, "_is_reparse_point", side_effect=lambda path: Path(path).name.casefold() == "desktop"
                ):
                    payload = json.loads(agent.execute_command("list_files", {"path": ""}))
                    names = {entry["name"] for entry in payload["entries"]}
                    self.assertNotIn("Masaüstü", names)
                    self.assertIn("Belgeler", names)
                    with self.assertRaisesRegex(ValueError, "erişim izni yok"):
                        agent.execute_command("list_files", {"path": str(desktop)})

    def test_directory_listing_returns_metadata_without_reading_file_contents(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            documents = profile / "Documents"
            documents.mkdir()
            nested = documents / "Raporlar"
            nested.mkdir()
            report = documents / "rapor.txt"
            report.write_text("private contents", encoding="utf-8")
            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                payload = json.loads(
                    agent.execute_command("list_files", {"path": str(documents)})
                )

        self.assertEqual(payload["currentPath"], str(documents.resolve()))
        self.assertEqual(payload["parentPath"], "")
        self.assertEqual(
            {entry["name"] for entry in payload["entries"]},
            {"Raporlar", "rapor.txt"},
        )
        file_entry = next(entry for entry in payload["entries"] if entry["name"] == "rapor.txt")
        self.assertFalse(file_entry["isDirectory"])
        self.assertEqual(file_entry["size"], len("private contents".encode("utf-8")))
        self.assertNotIn("contents", json.dumps(payload).lower())

    def test_path_outside_allowlisted_profile_folders_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir) / "profile"
            profile.mkdir()
            (profile / "Documents").mkdir()
            outside = Path(temp_dir) / "private"
            outside.mkdir()
            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                with self.assertRaisesRegex(ValueError, "erişim izni yok"):
                    agent.execute_command("list_files", {"path": str(outside)})

    def test_empty_directory_returns_valid_empty_listing(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            documents = profile / "Documents"
            documents.mkdir()
            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                payload = json.loads(
                    agent.execute_command("list_files", {"path": str(documents)})
                )

        self.assertEqual(payload["entries"], [])
        self.assertFalse(payload["truncated"])
        self.assertEqual(payload["currentPath"], str(documents.resolve()))

    def test_large_directory_is_bounded_and_marked_truncated(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            documents = profile / "Documents"
            documents.mkdir()
            for index in range(agent.FILE_LIST_MAX_ENTRIES + 40):
                (documents / f"file-{index:03}.txt").write_text("metadata-only test", encoding="utf-8")

            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                payload = json.loads(
                    agent.execute_command("list_files", {"path": str(documents)})
                )

        self.assertEqual(len(payload["entries"]), agent.FILE_LIST_MAX_ENTRIES)
        self.assertTrue(payload["truncated"])
        self.assertTrue(payload["warnings"])
        self.assertTrue(all(isinstance(warning, str) and warning for warning in payload["warnings"]))
        self.assertLessEqual(
            len(json.dumps(payload, ensure_ascii=False).encode("utf-8")),
            agent.FILE_LIST_MAX_RESULT_LENGTH,
        )

    def test_legacy_text_request_is_routed_to_real_file_listing(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            (profile / "Desktop").mkdir()
            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                payload = json.loads(agent.execute_command(
                    "text_command", {"text": "bilgisayar dosyalarını listele"}
                ))

        self.assertEqual([entry["name"] for entry in payload["entries"]], ["Masaüstü"])


    def test_file_list_result_is_not_truncated_at_the_status_message_limit(self) -> None:
        document = {
            "id": "list-files",
            "_claimToken": "server-claim",
            "fields": {"deviceId": "win-test", "command": "list_files"},
        }
        original_result = json.dumps({"entries": [{"name": "x" * 1000} for _ in range(10)]})
        with mock.patch.object(agent, "server_request") as request:
            agent.update_command(FakeSession(), document, "completed", original_result)
        saved_result = request.call_args.args[3]["result"]
        self.assertEqual(saved_result, original_result)
        self.assertGreater(len(saved_result), 800)
        self.assertEqual(len(json.loads(saved_result)["entries"]), 10)



class FirebaseLoginRequiredTests(unittest.TestCase):
    def test_bridge_without_saved_session_requests_app_login_without_oauth_json(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            status_path = root / "status.json"
            with mock.patch.object(agent, "APP_DATA", root), \
                 mock.patch.object(agent, "STATUS_FILE", status_path), \
                 mock.patch.object(agent, "SESSION_FILE", root / "session.bin"), \
                 mock.patch.object(agent, "load_protected_session", return_value=None), \
                 mock.patch.object(agent.requests, "post") as post, \
                 mock.patch("builtins.print"):
                result = agent.run_agent()

            self.assertEqual(result, 2)
            status = json.loads(status_path.read_text(encoding="utf-8"))
            self.assertEqual(status["state"], "auth_required")
            self.assertIn("Hesap bölümünden giriş", status["message"])
            post.assert_not_called()

    def test_expired_refresh_token_requests_account_login_without_browser_oauth(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            status_path = root / "status.json"
            stored = {"uid": "test-user", "refreshToken": "expired-token", "email": "test@example.com"}
            with mock.patch.object(agent, "APP_DATA", root), \
                 mock.patch.object(agent, "STATUS_FILE", status_path), \
                 mock.patch.object(agent, "SESSION_FILE", root / "session.bin"), \
                 mock.patch.object(agent, "load_protected_session", return_value=stored), \
                 mock.patch.object(agent, "FirebaseSession", side_effect=agent.FirebaseSessionExpiredError("expired")), \
                 mock.patch.object(agent, "delete_protected_session") as delete_session, \
                 mock.patch("builtins.print"):
                result = agent.run_agent()

            self.assertEqual(result, 2)
            delete_session.assert_called_once()
            status = json.loads(status_path.read_text(encoding="utf-8"))
            self.assertEqual(status["state"], "auth_required")
            self.assertIn("Hesap bölümünden giriş", status["message"])


if __name__ == "__main__":
    unittest.main(verbosity=2)

class SafeFileDownloadCommandTests(unittest.TestCase):
    def setUp(self) -> None:
        self.drive_roots_patch = mock.patch.object(agent, "_windows_storage_roots", return_value=[])
        self.drive_roots_patch.start()
        self.addCleanup(self.drive_roots_patch.stop)

    def test_download_returns_content_hash_and_size_from_allowlisted_file(self) -> None:
        import base64
        import hashlib

        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            desktop = profile / "Desktop"
            desktop.mkdir()
            content = b"MynAssistant safe file download"
            target = desktop / "example.txt"
            target.write_bytes(content)

            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                payload = json.loads(agent.execute_command("download_file", {"path": str(target)}))

        self.assertEqual(payload["name"], "example.txt")
        self.assertEqual(payload["size"], len(content))
        self.assertEqual(base64.b64decode(payload["contentBase64"]), content)
        self.assertEqual(payload["sha256"], hashlib.sha256(content).hexdigest())
        self.assertTrue(agent.command_is_supported("download_file"))
        self.assertLessEqual(len(json.dumps(payload, separators=(",", ":")).encode("utf-8")),
                             agent.FILE_DOWNLOAD_MAX_RESULT_LENGTH)

    def test_download_rejects_files_outside_allowlisted_profile_folders(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            private = profile / "Private"
            private.mkdir()
            target = private / "secret.txt"
            target.write_text("not allowed", encoding="utf-8")

            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                with self.assertRaisesRegex(ValueError, "erişim izni yok"):
                    agent.execute_command("download_file", {"path": str(target)})

    def test_download_rejects_directory_and_oversized_file(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            profile = Path(temp_dir)
            desktop = profile / "Desktop"
            desktop.mkdir()
            folder = desktop / "Folder"
            folder.mkdir()
            oversized = desktop / "large.bin"
            oversized.write_bytes(b"x" * (agent.FILE_DOWNLOAD_MAX_BYTES + 1))

            with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(profile)}):
                with self.assertRaisesRegex(ValueError, "bir dosya değil"):
                    agent.execute_command("download_file", {"path": str(folder)})
                with self.assertRaisesRegex(ValueError, "(?i)büyük dosyalar"):
                    agent.execute_command("download_file", {"path": str(oversized)})

    def test_download_rejects_empty_and_overlong_paths(self) -> None:
        with self.assertRaisesRegex(ValueError, "geçersiz"):
            agent.execute_command("download_file", {"path": ""})
        with self.assertRaisesRegex(ValueError, "geçersiz"):
            agent.execute_command("download_file", {"path": "x" * (agent.FILE_LIST_MAX_PATH_LENGTH + 1)})


class CommandExpirationAndQueueScopeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.session = FakeSession()
        self.now = datetime(2026, 10, 27, 21, 0, 0, tzinfo=timezone.utc)

    def test_recent_command_is_still_valid(self) -> None:
        fields = {"createdAt": (self.now - timedelta(seconds=30)).isoformat()}
        self.assertIsNone(agent.command_expiration_reason(fields, self.now))

    def test_old_pending_command_expires(self) -> None:
        fields = {"createdAt": (self.now - timedelta(seconds=61)).isoformat()}
        self.assertIn("süresi doldu", agent.command_expiration_reason(fields, self.now).lower())

    def test_expired_offline_queued_command_cannot_run_late(self) -> None:
        fields = {"createdAt": self.now.isoformat(), "expiresAt": (self.now - timedelta(seconds=1)).isoformat()}
        self.assertIn("süresi doldu", agent.command_expiration_reason(fields, self.now).lower())

    def test_missing_timestamp_fails_closed(self) -> None:
        self.assertIn("zamanı", agent.command_expiration_reason({}, self.now).lower())

    def test_unreasonable_future_expiry_fails_closed(self) -> None:
        fields = {"expiresAt": (self.now + timedelta(minutes=10)).isoformat()}
        self.assertIn("sınırı", agent.command_expiration_reason(fields, self.now).lower())

    def test_query_scopes_pending_commands_to_current_device_through_server(self) -> None:
        with mock.patch.object(agent, "server_request", return_value={"items": []}) as request:
            self.assertEqual(agent.fetch_owner_commands(self.session, "win-test"), [])
        request.assert_called_once_with(
            self.session,
            "GET",
            "/api/v1/pc/agent/devices/win-test/commands",
            timeout=25,
        )


class SafeFileUploadAndZipCommandTests(unittest.TestCase):
    def setUp(self) -> None:
        self.drive_roots_patch = mock.patch.object(agent, "_windows_storage_roots", return_value=[])
        self.drive_roots_patch.start()
        self.addCleanup(self.drive_roots_patch.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.profile = Path(self.temp.name) / "profile"
        self.profile.mkdir()
        self.documents = self.profile / "Documents"
        self.documents.mkdir()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def upload_fields(self, name: str = "from-phone.txt", content: bytes = b"phone to PC") -> dict:
        import base64
        import hashlib
        return {
            "destinationPath": str(self.documents),
            "name": name,
            "size": len(content),
            "sha256": hashlib.sha256(content).hexdigest(),
            "contentBase64": base64.b64encode(content).decode("ascii"),
        }

    def test_upload_writes_verified_bytes_inside_allowlisted_folder(self) -> None:
        with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(self.profile)}):
            result = agent.execute_command("upload_file", self.upload_fields())
        self.assertEqual((self.documents / "from-phone.txt").read_bytes(), b"phone to PC")
        self.assertIn("SHA-256", result)
        self.assertTrue(agent.command_is_supported("upload_file"))

    def test_upload_rejects_folder_outside_allowlist(self) -> None:
        outside = self.profile / "Private"
        outside.mkdir()
        fields = self.upload_fields()
        fields["destinationPath"] = str(outside)
        with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(self.profile)}):
            with self.assertRaisesRegex(ValueError, "erişim izni yok"):
                agent.execute_command("upload_file", fields)

    def test_upload_rejects_traversal_oversized_content_and_bad_checksum(self) -> None:
        with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(self.profile)}):
            with self.assertRaises(ValueError):
                agent.execute_command("upload_file", self.upload_fields("../evil.txt"))
            oversized = self.upload_fields(content=b"x" * (agent.FILE_UPLOAD_MAX_BYTES + 1))
            with self.assertRaisesRegex(ValueError, "en fazla"):
                agent.execute_command("upload_file", oversized)
            bad_hash = self.upload_fields()
            bad_hash["sha256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                agent.execute_command("upload_file", bad_hash)

    def test_upload_does_not_overwrite_existing_file(self) -> None:
        target = self.documents / "from-phone.txt"
        target.write_bytes(b"keep")
        with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(self.profile)}):
            with self.assertRaisesRegex(ValueError, "zaten var"):
                agent.execute_command("upload_file", self.upload_fields(content=b"replace"))
        self.assertEqual(target.read_bytes(), b"keep")

    def test_zip_folder_creates_valid_archive_in_allowed_parent(self) -> None:
        folder = self.documents / "Raporlar"
        folder.mkdir()
        (folder / "one.txt").write_text("one", encoding="utf-8")
        (folder / "two.txt").write_text("two", encoding="utf-8")
        with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(self.profile)}):
            result = agent.execute_command("zip_folder", {"path": str(folder)})
        archive_path = self.documents / "Raporlar.zip"
        self.assertTrue(archive_path.is_file())
        self.assertIn("ZIP", result)
        with zipfile.ZipFile(archive_path) as archive:
            self.assertEqual(set(archive.namelist()), {"one.txt", "two.txt"})
        self.assertTrue(agent.command_is_supported("zip_folder"))

    def test_zip_rejects_folder_outside_allowlist_and_existing_archive(self) -> None:
        outside = self.profile / "Private"
        outside.mkdir()
        folder = self.documents / "Raporlar"
        folder.mkdir()
        archive_path = self.documents / "Raporlar.zip"
        archive_path.write_bytes(b"keep")
        with mock.patch.dict(agent.os.environ, {"USERPROFILE": str(self.profile)}):
            with self.assertRaisesRegex(ValueError, "erişim izni yok"):
                agent.execute_command("zip_folder", {"path": str(outside)})
            with self.assertRaisesRegex(ValueError, "zaten var"):
                agent.execute_command("zip_folder", {"path": str(folder)})
        self.assertEqual(archive_path.read_bytes(), b"keep")
