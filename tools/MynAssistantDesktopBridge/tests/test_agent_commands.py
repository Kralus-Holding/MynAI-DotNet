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


if __name__ == "__main__":
    unittest.main()
