from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
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


class AtomicCommandClaimTests(unittest.TestCase):
    def setUp(self) -> None:
        self.session = FakeSession()
        self.document = {
            "name": "projects/myncloud-24/databases/(default)/documents/commands/cmd-1",
            "updateTime": "2026-10-22T10:00:00.000000Z",
            "fields": {
                "ownerId": {"stringValue": "test-user"},
                "deviceId": {"stringValue": "win-test"},
                "command": {"stringValue": "volume_up"},
                "status": {"stringValue": "pending"},
            },
        }

    def test_screen_frame_compresses_before_base64_firestore_expansion(self) -> None:
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
            with self.assertRaisesRegex(RuntimeError, "Firestore belge boyutu"):
                agent.capture_screen_base64()
        self.assertEqual(image.qualities_used, [45, 30])

    def test_patch_includes_firestore_update_time_precondition(self) -> None:
        with mock.patch.object(agent.requests, "patch", return_value=FakeResponse(200, {})) as patch:
            agent.firestore_patch(
                self.session,
                "commands/cmd-1",
                {"status": agent.encode_string("running")},
                update_mask=["status"],
                current_document_update_time=self.document["updateTime"],
            )

        params = patch.call_args.kwargs["params"]
        self.assertIn(("currentDocument.updateTime", self.document["updateTime"]), params)
        self.assertIn(("updateMask.fieldPaths", "status"), params)

    def test_claim_refuses_document_without_update_time(self) -> None:
        document = dict(self.document)
        document.pop("updateTime")
        with mock.patch.object(agent.requests, "patch") as patch, mock.patch.object(agent.requests, "get") as get:
            claimed = agent.claim_pending_command(self.session, document, "win-test")
        self.assertFalse(claimed)
        patch.assert_not_called()
        get.assert_not_called()

    def test_claim_wins_using_compare_and_swap(self) -> None:
        with mock.patch.object(agent.requests, "patch", return_value=FakeResponse(200, {})) as patch:
            claimed = agent.claim_pending_command(self.session, self.document, "win-test")

        self.assertTrue(claimed)
        params = patch.call_args.kwargs["params"]
        self.assertIn(("currentDocument.updateTime", self.document["updateTime"]), params)
        fields = patch.call_args.kwargs["json"]["fields"]
        self.assertEqual(fields["status"]["stringValue"], "running")
        self.assertEqual(fields["claimedByDeviceId"]["stringValue"], "win-test")
        self.assertTrue(fields["claimToken"]["stringValue"])

    def test_cancelled_document_wins_race_and_agent_does_not_claim(self) -> None:
        with mock.patch.object(
            agent.requests, "patch", return_value=FakeResponse(
                400, {"error": {"status": "FAILED_PRECONDITION"}}, '{"error":{"status":"FAILED_PRECONDITION"}}'
            )
        ) as patch, mock.patch.object(
            agent.requests, "get", return_value=FakeResponse(
                200, firestore_doc({"status": "cancelled", "result": "Cancelled"})
            )
        ) as get:
            claimed = agent.claim_pending_command(self.session, self.document, "win-test")

        self.assertFalse(claimed)
        patch.assert_called_once()
        get.assert_called_once()
        self.assertIn(("currentDocument.updateTime", self.document["updateTime"]), patch.call_args.kwargs["params"])

    def test_timeout_after_success_is_verified_by_unique_claim_token(self) -> None:
        captured = {}

        def patch_then_timeout(*args, **kwargs):
            captured["body"] = kwargs["json"]["fields"]
            raise agent.requests.Timeout("response was lost after server accepted write")

        def get_claim(*args, **kwargs):
            fields = captured["body"]
            return FakeResponse(
                200,
                firestore_doc({
                    "status": fields["status"]["stringValue"],
                    "claimToken": fields["claimToken"]["stringValue"],
                    "claimedByDeviceId": fields["claimedByDeviceId"]["stringValue"],
                }),
            )

        with mock.patch.object(agent.requests, "patch", side_effect=patch_then_timeout), mock.patch.object(
            agent.requests, "get", side_effect=get_claim
        ):
            claimed = agent.claim_pending_command(self.session, self.document, "win-test")

        self.assertTrue(claimed)



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


class SafeFileListingCommandTests(unittest.TestCase):
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
        self.assertTrue(any("bazı öğeler" in warning.lower() for warning in payload["warnings"]))

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
            "name": "projects/myncloud-24/databases/(default)/documents/commands/list-files",
            "fields": {"command": {"stringValue": "list_files"}},
        }
        original_result = json.dumps({"entries": [{"name": "x" * 1000} for _ in range(10)]})
        with mock.patch.object(agent, "firestore_patch") as patch:
            agent.update_command(FakeSession(), document, "completed", original_result)
        saved_result = patch.call_args.args[2]["result"]["stringValue"]
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
