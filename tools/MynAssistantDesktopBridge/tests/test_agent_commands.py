import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


AGENT_PATH = Path(__file__).resolve().parents[1] / "agent.py"
SPEC = importlib.util.spec_from_file_location("mynassistant_agent_under_test", AGENT_PATH)
agent = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(agent)


class FakeSession:
    uid = "firebase-user-123"
    email = "phone-owner@example.test"

    def headers(self):
        return {"Authorization": "Bearer test-token"}


class FakeResponse:
    def __init__(self, status_code, body=None, text=""):
        self.status_code = status_code
        self._body = body or {}
        self.text = text

    def json(self):
        return self._body


class DeviceCommandQueryTests(unittest.TestCase):
    @patch.object(agent.requests, "post")
    def test_query_is_scoped_to_user_device_and_pending_status(self, post):
        post.return_value = FakeResponse(200, [{"document": {"name": "test-command"}}])

        documents = agent.fetch_device_commands(FakeSession(), "win-device-456")

        self.assertEqual(documents, [{"name": "test-command"}])
        query = post.call_args.kwargs["json"]["structuredQuery"]
        filters = query["where"]["compositeFilter"]["filters"]
        values = {
            item["fieldFilter"]["field"]["fieldPath"]:
                item["fieldFilter"]["value"]["stringValue"]
            for item in filters
        }
        self.assertEqual(values["ownerId"], "firebase-user-123")
        self.assertEqual(values["deviceId"], "win-device-456")
        self.assertEqual(values["status"], "pending")
        self.assertEqual(query["orderBy"][0]["field"]["fieldPath"], "createdAt")
        self.assertEqual(query["orderBy"][0]["direction"], "ASCENDING")


class AndroidPhoneDiscoveryTests(unittest.TestCase):
    @patch.object(agent.requests, "post")
    def test_query_returns_only_android_devices_owned_by_current_user(self, post):
        post.return_value = FakeResponse(200, [
            {"document": {
                "name": "projects/myncloud-24/databases/(default)/documents/devices/android-phone-1",
                "fields": {
                    "ownerId": {"stringValue": "firebase-user-123"},
                    "name": {"stringValue": "Pixel Test"},
                    "platform": {"stringValue": "Android"},
                    "status": {"stringValue": "online"},
                    "lastSeenAt": {"timestampValue": agent.utc_now()},
                },
            }},
            {"document": {
                "name": "projects/myncloud-24/databases/(default)/documents/devices/win-device-1",
                "fields": {
                    "ownerId": {"stringValue": "firebase-user-123"},
                    "name": {"stringValue": "My PC"},
                    "platform": {"stringValue": "Windows"},
                },
            }},
            {"document": {
                "name": "projects/myncloud-24/databases/(default)/documents/devices/android-other-user",
                "fields": {
                    "ownerId": {"stringValue": "somebody-else"},
                    "name": {"stringValue": "Other phone"},
                    "platform": {"stringValue": "Android"},
                },
            }},
        ])

        phones = agent.fetch_android_phone_devices(FakeSession())

        self.assertEqual(len(phones), 1)
        self.assertEqual(phones[0]["id"], "android-phone-1")
        self.assertEqual(phones[0]["name"], "Pixel Test")
        self.assertEqual(phones[0]["status"], "online")
        query = post.call_args.kwargs["json"]["structuredQuery"]
        owner_filter = query["where"]["fieldFilter"]
        self.assertEqual(owner_filter["field"]["fieldPath"], "ownerId")
        self.assertEqual(owner_filter["value"]["stringValue"], "firebase-user-123")
        self.assertEqual(query["limit"], 100)


    @patch.object(agent.requests, "post")
    def test_stale_android_device_is_reported_offline(self, post):
        post.return_value = FakeResponse(200, [
            {"document": {
                "name": "projects/myncloud-24/databases/(default)/documents/devices/android-stale-phone",
                "fields": {
                    "ownerId": {"stringValue": "firebase-user-123"},
                    "name": {"stringValue": "Stale phone"},
                    "platform": {"stringValue": "Android"},
                    "status": {"stringValue": "online"},
                    "lastSeenAt": {"timestampValue": "2000-01-01T00:00:00Z"},
                },
            }},
        ])

        phones = agent.fetch_android_phone_devices(FakeSession())

        self.assertEqual(len(phones), 1)
        self.assertEqual(phones[0]["status"], "offline")

    @patch.object(agent, "fetch_android_phone_devices")
    def test_snapshot_includes_account_identity_and_writes_atomically(self, fetch_devices):
        fetch_devices.return_value = [
            {"id": "android-phone-1", "name": "Pixel Test", "platform": "Android", "status": "online", "lastSeenAt": ""}
        ]

        with tempfile.TemporaryDirectory() as temp_dir:
            app_data = Path(temp_dir)
            with patch.object(agent, "APP_DATA", app_data):
                agent.write_android_phone_snapshot(FakeSession())
                snapshot_path = app_data / "android_devices.json"
                snapshot = json.loads(snapshot_path.read_text(encoding="utf-8"))

            self.assertEqual(snapshot["ownerId"], FakeSession.uid)
            self.assertEqual(snapshot["email"], FakeSession.email)
            self.assertEqual(snapshot["devices"], fetch_devices.return_value)
            self.assertFalse((app_data / "android_devices.tmp").exists())


class CommandClaimTests(unittest.TestCase):
    def setUp(self):
        self.session = FakeSession()
        self.document = {
            "name": "projects/myncloud-24/databases/(default)/documents/commands/test-command",
            "updateTime": "2026-10-17T12:58:30.000000Z",
        }

    @patch.object(agent.requests, "patch")
    def test_claim_uses_update_time_precondition(self, patch_request):
        patch_request.return_value = FakeResponse(200, {})
        claimed = agent.claim_command(self.session, self.document)

        self.assertTrue(claimed)
        params = patch_request.call_args.kwargs["params"]
        self.assertIn(("currentDocument.updateTime", self.document["updateTime"]), params)
        self.assertIn(("updateMask.fieldPaths", "status"), params)
        fields = patch_request.call_args.kwargs["json"]["fields"]
        self.assertEqual(fields["status"]["stringValue"], "running")
        self.assertIn("startedAt", fields)

    @patch.object(agent.requests, "patch")
    def test_stale_claim_is_a_non_fatal_conflict(self, patch_request):
        patch_request.return_value = FakeResponse(
            400,
            {"error": {"status": "FAILED_PRECONDITION", "message": "The precondition failed."}},
            "precondition failed",
        )
        self.assertFalse(agent.claim_command(self.session, self.document))

    @patch.object(agent.requests, "patch")
    def test_unexpected_http_error_is_reported(self, patch_request):
        patch_request.return_value = FakeResponse(
            500,
            {"error": {"status": "INTERNAL", "message": "server error"}},
            "server error",
        )
        with self.assertRaisesRegex(RuntimeError, "HTTP 500"):
            agent.claim_command(self.session, self.document)


class FirebaseResponseHandlingTests(unittest.TestCase):
    def test_transient_server_failure_is_retryable(self):
        response = FakeResponse(503, {"error": {"status": "UNAVAILABLE"}}, "service unavailable")
        with self.assertRaisesRegex(agent.requests.HTTPError, "HTTP 503"):
            agent.raise_for_firebase_response("Firestore", response)

    def test_permanent_client_failure_is_not_marked_retryable(self):
        response = FakeResponse(403, {"error": {"status": "PERMISSION_DENIED"}}, "permission denied")
        with self.assertRaisesRegex(RuntimeError, "HTTP 403"):
            agent.raise_for_firebase_response("Firestore", response)


class FileListingCommandTests(unittest.TestCase):
    def test_phone_file_list_command_returns_server_relayable_listing(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            home = Path(temp_dir)
            desktop = home / "Desktop"
            documents = home / "Documents"
            desktop.mkdir()
            documents.mkdir()
            (desktop / "sample.txt").write_text("not uploaded", encoding="utf-8")
            (documents / "project").mkdir()

            with patch.object(agent.Path, "home", return_value=home):
                result = agent.execute_text_command("bilgisayar dosyalarını listele")

        self.assertIn("Masaüstü", result)
        self.assertIn("sample.txt", result)
        self.assertIn("project", result)
        self.assertNotIn("not uploaded", result)

    def test_file_listing_is_bounded_and_non_recursive(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            home = Path(temp_dir)
            desktop = home / "Desktop"
            desktop.mkdir()
            for index in range(25):
                (desktop / f"item-{index:02d}.txt").write_text("x", encoding="utf-8")
            (desktop / "nested").mkdir()
            (desktop / "nested" / "secret.txt").write_text("x", encoding="utf-8")

            result = agent.list_user_files(home, max_entries_per_folder=5)

        self.assertIn("ve 21 öğe daha", result)
        self.assertNotIn("secret.txt", result)


class StartupRetryTests(unittest.TestCase):
    def test_transient_network_failure_retries_and_recovers(self):
        attempts = []

        def operation():
            attempts.append(1)
            if len(attempts) == 1:
                raise agent.requests.ConnectionError("offline")

        with patch.object(agent.time, "sleep") as sleep:
            agent.retry_transient_operation(operation, "device registration")

        self.assertEqual(len(attempts), 2)
        sleep.assert_called_once_with(2.0)

    def test_permanent_error_is_not_retried(self):
        attempts = []

        def operation():
            attempts.append(1)
            raise RuntimeError("HTTP 403: permission denied")

        with patch.object(agent.time, "sleep") as sleep:
            with self.assertRaisesRegex(RuntimeError, "HTTP 403"):
                agent.retry_transient_operation(operation, "device registration")

        self.assertEqual(len(attempts), 1)
        sleep.assert_not_called()


class OAuthScopeTests(unittest.TestCase):
    def test_google_sign_in_uses_canonical_scope_uris(self):
        self.assertEqual(
            agent.SCOPES,
            [
                "openid",
                "https://www.googleapis.com/auth/userinfo.email",
                "https://www.googleapis.com/auth/userinfo.profile",
            ],
        )


class OAuthConfigPathTests(unittest.TestCase):
    def _make_config_tree(self, root: Path):
        install_dir = root / "install"
        app_data = root / "local" / "MynAI" / "DesktopAgent"
        google_dir = root / "local" / "MynAI" / "Google"
        install_dir.mkdir(parents=True)
        app_data.mkdir(parents=True)
        google_dir.mkdir(parents=True)
        sidecar = install_dir / "client_secret.json"
        user_config = google_dir / "client_secret.json"
        return install_dir, app_data, sidecar, user_config

    def test_side_by_side_config_is_preferred(self):
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            install_dir, app_data, sidecar, user_config = self._make_config_tree(Path(temp_dir))
            sidecar.write_text("{}", encoding="utf-8")
            user_config.write_text("{}", encoding="utf-8")

            with patch.object(agent, "running_directory", return_value=install_dir):
                with patch.object(agent, "APP_DATA", app_data):
                    self.assertEqual(agent.find_oauth_credentials_path(), sidecar)

    def test_per_user_google_config_is_used_as_fallback(self):
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            install_dir, app_data, sidecar, user_config = self._make_config_tree(Path(temp_dir))
            user_config.write_text("{}", encoding="utf-8")

            with patch.object(agent, "running_directory", return_value=install_dir):
                with patch.object(agent, "APP_DATA", app_data):
                    self.assertEqual(agent.find_oauth_credentials_path(), user_config)


class FakeScreenImage:
    def __init__(self, byte_sizes, attempts=None):
        self.byte_sizes = byte_sizes
        self.attempts = attempts if attempts is not None else []
        self.dimensions = None

    def copy(self):
        return FakeScreenImage(self.byte_sizes, self.attempts)

    def thumbnail(self, dimensions):
        self.dimensions = dimensions
        self.attempts.append(dimensions)

    def save(self, output, format, quality, optimize):
        output.write(b"x" * self.byte_sizes[self.dimensions])


class ScreenFrameSizeTests(unittest.TestCase):
    @patch.object(agent.pyautogui, "screenshot")
    def test_frame_budget_measures_base64_size_and_reduces_resolution(self, screenshot):
        attempts = []
        screenshot.return_value = FakeScreenImage(
            {
                (1280, 720): 600_000,
                (960, 540): 470_000,
                (800, 450): 300_000,
                (640, 360): 200_000,
            },
            attempts,
        )

        frame = agent.capture_screen_base64()

        self.assertLessEqual(len(frame), agent.MAX_SCREEN_FRAME_BASE64_CHARS)
        self.assertEqual(len(frame) % 4, 0)
        self.assertEqual(attempts, [(1280, 720), (960, 540)])

    @patch.object(agent.pyautogui, "screenshot")
    def test_frame_that_exceeds_base64_budget_is_rejected(self, screenshot):
        attempts = []
        screenshot.return_value = FakeScreenImage(
            {
                (1280, 720): 600_000,
                (960, 540): 600_000,
                (800, 450): 600_000,
                (640, 360): 600_000,
            },
            attempts,
        )

        with self.assertRaisesRegex(RuntimeError, "Base64"):
            agent.capture_screen_base64()

        self.assertEqual(
            attempts,
            [(1280, 720), (960, 540), (800, 450), (640, 360)],
        )


if __name__ == "__main__":
    unittest.main()
