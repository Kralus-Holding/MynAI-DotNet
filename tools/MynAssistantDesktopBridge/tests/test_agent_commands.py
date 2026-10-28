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



class CommandTargetRoutingTests(unittest.TestCase):
    def test_accepts_only_pending_command_for_same_owner_and_device(self) -> None:
        fields = {
            "ownerId": "test-user",
            "deviceId": "win-test",
            "status": "pending",
        }
        self.assertTrue(agent.command_targets_device(fields, "test-user", "win-test"))

    def test_rejects_command_for_another_windows_device(self) -> None:
        fields = {
            "ownerId": "test-user",
            "deviceId": "win-other",
            "status": "pending",
        }
        self.assertFalse(agent.command_targets_device(fields, "test-user", "win-test"))

    def test_rejects_command_owned_by_another_user_even_if_device_id_matches(self) -> None:
        fields = {
            "ownerId": "other-user",
            "deviceId": "win-test",
            "status": "pending",
        }
        self.assertFalse(agent.command_targets_device(fields, "test-user", "win-test"))

    def test_rejects_cancelled_running_completed_or_missing_status(self) -> None:
        for status in ("cancelled", "running", "completed", "failed", ""):
            with self.subTest(status=status):
                fields = {"ownerId": "test-user", "deviceId": "win-test", "status": status}
                self.assertFalse(agent.command_targets_device(fields, "test-user", "win-test"))

    def test_rejects_missing_owner_or_target_fields(self) -> None:
        self.assertFalse(agent.command_targets_device({"deviceId": "win-test", "status": "pending"}, "test-user", "win-test"))
        self.assertFalse(agent.command_targets_device({"ownerId": "test-user", "status": "pending"}, "test-user", "win-test"))


class DeviceRegistrationContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.session = FakeSession()

    def test_initial_registration_uses_shared_android_device_schema(self) -> None:
        with mock.patch.object(agent.platform, "node", return_value="CI-Windows"), mock.patch.object(
            agent.requests, "patch", return_value=FakeResponse(200, {})
        ) as patch:
            agent.register_or_heartbeat(self.session, "win-test-device", initial=True)

        self.assertIn("/devices/win-test-device", patch.call_args.args[0])
        body = patch.call_args.kwargs["json"]["fields"]
        self.assertEqual(body["ownerId"]["stringValue"], "test-user")
        self.assertEqual(body["platform"]["stringValue"], "Windows")
        self.assertEqual(body["type"]["stringValue"], "Windows")
        self.assertEqual(body["connectionMode"]["stringValue"], "firebase-agent")
        self.assertEqual(body["status"]["stringValue"], "online")
        self.assertEqual(body["name"]["stringValue"], "CI-Windows")
        self.assertEqual(body["screenRequested"]["booleanValue"], False)
        self.assertTrue(body["lastSeenAt"]["timestampValue"])

    def test_heartbeat_only_updates_presence_and_preserves_registration_identity(self) -> None:
        with mock.patch.object(agent, "firestore_patch") as patch:
            agent.register_or_heartbeat(self.session, "win-test-device", initial=False)

        patch.assert_called_once()
        self.assertEqual(patch.call_args.args[1], "devices/win-test-device")
        fields = patch.call_args.args[2]
        self.assertEqual(set(fields), {"status", "lastSeenAt"})
        self.assertEqual(fields["status"]["stringValue"], "online")
        self.assertEqual(patch.call_args.kwargs["update_mask"], ["status", "lastSeenAt"])


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


class RemoteControlPermissionTests(unittest.TestCase):
    def test_reenable_timestamp_is_loaded_from_settings(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            settings_dir = Path(directory) / "MynAI"
            settings_dir.mkdir()
            (settings_dir / "settings.json").write_text(
                json.dumps({
                    "RemoteControlEnabled": True,
                    "RemoteControlEnabledAt": "2026-10-27T12:00:00+00:00",
                }),
                encoding="utf-8",
            )
            with mock.patch.dict(agent.os.environ, {"LOCALAPPDATA": directory}):
                cutoff = agent.remote_control_reenable_cutoff()

        self.assertEqual(cutoff, datetime(2026, 10, 27, 12, 0, tzinfo=timezone.utc))

    def test_commands_queued_before_reenable_are_rejected(self) -> None:
        cutoff = datetime(2026, 10, 27, 12, 0, tzinfo=timezone.utc)
        self.assertTrue(agent.command_predates_remote_control_reenable(
            {"createdAt": "2026-10-27T11:59:59Z"}, cutoff
        ))
        self.assertFalse(agent.command_predates_remote_control_reenable(
            {"createdAt": "2026-10-27T12:00:01Z"}, cutoff
        ))

    def test_missing_command_timestamp_fails_closed_after_reenable(self) -> None:
        cutoff = datetime(2026, 10, 27, 12, 0, tzinfo=timezone.utc)
        self.assertTrue(agent.command_predates_remote_control_reenable({}, cutoff))

    def test_missing_reenable_cutoff_preserves_legacy_behavior(self) -> None:
        old_command = {"createdAt": "2020-01-01T00:00:00Z"}
        self.assertFalse(agent.command_predates_remote_control_reenable(old_command, None))

    def test_missing_settings_file_preserves_legacy_default(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.dict(agent.os.environ, {"LOCALAPPDATA": directory}):
                self.assertTrue(agent.remote_control_enabled())

    def test_explicitly_disabled_remote_control_is_respected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            settings_dir = Path(directory) / "MynAI"
            settings_dir.mkdir()
            (settings_dir / "settings.json").write_text(
                json.dumps({"RemoteControlEnabled": False}),
                encoding="utf-8",
            )
            with mock.patch.dict(agent.os.environ, {"LOCALAPPDATA": directory}):
                self.assertFalse(agent.remote_control_enabled())

    def test_legacy_settings_without_permission_key_remain_enabled(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            settings_dir = Path(directory) / "MynAI"
            settings_dir.mkdir()
            (settings_dir / "settings.json").write_text(
                json.dumps({"WindowsNotifications": True}),
                encoding="utf-8",
            )
            with mock.patch.dict(agent.os.environ, {"LOCALAPPDATA": directory}):
                self.assertTrue(agent.remote_control_enabled())

    def test_malformed_settings_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            settings_dir = Path(directory) / "MynAI"
            settings_dir.mkdir()
            (settings_dir / "settings.json").write_text("{bad json", encoding="utf-8")
            with mock.patch.dict(agent.os.environ, {"LOCALAPPDATA": directory}):
                self.assertFalse(agent.remote_control_enabled())

    def test_queue_read_failure_does_not_raise_or_stop_permission_guard(self) -> None:
        with mock.patch.object(
            agent, "fetch_owner_commands", side_effect=RuntimeError("temporary network error")
        ):
            agent.fail_pending_commands_when_remote_control_disabled(
                FakeSession(), "win-test"
            )

    def test_disabled_remote_control_fails_queued_commands(self) -> None:
        session = FakeSession()
        document = firestore_doc({
            "ownerId": "test-user",
            "deviceId": "win-test",
            "command": "volume_up",
            "status": "pending",
        })
        with mock.patch.object(agent, "fetch_owner_commands", return_value=[document]), \
             mock.patch.object(agent, "update_command") as update:
            agent.fail_pending_commands_when_remote_control_disabled(session, "win-test")

        update.assert_called_once()
        args, kwargs = update.call_args
        self.assertEqual(args[0], session)
        self.assertEqual(args[1], document)
        self.assertEqual(args[2], "failed")
        self.assertIn("permission", args[3].lower())
        self.assertEqual(kwargs["expected_update_time"], document.get("updateTime"))


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
