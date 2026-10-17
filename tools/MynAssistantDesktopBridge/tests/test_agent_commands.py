import importlib.util
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
