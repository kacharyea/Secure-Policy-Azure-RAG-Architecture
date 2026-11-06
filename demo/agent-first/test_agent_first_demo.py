import contextlib
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

import agent_first_demo as demo


class AgentFirstTests(unittest.TestCase):
    def setUp(self):
        self.config = {
            "projectEndpoint": "https://example.services.ai.azure.com/api/projects/demo",
            "searchEndpoint": "https://example.search.windows.net",
            "connectionName": "demo-search",
            "indexName": "policy-chunks",
            "queryType": "simple",
            "model": "gpt-5-mini",
        }
        self.output = io.StringIO()
        self.redirect = contextlib.redirect_stdout(self.output)
        self.redirect.__enter__()
        self.addCleanup(self.redirect.__exit__, None, None, None)

    def definition(self):
        return demo.build_definition(self.config, "test-connection")

    def response(self, output, answer="insufficient evidence", status="completed"):
        response = Mock(output_text=answer)
        response.model_dump.return_value = {"status": status, "output": output}
        return response

    def test_existing_index_simple_top_five_without_filter(self):
        index = self.definition().as_dict()["tools"][0]["azure_ai_search"]["indexes"][0]
        self.assertEqual(index, {
            "project_connection_id": "test-connection", "index_name": "policy-chunks",
            "query_type": "simple", "top_k": 5,
        })

    def test_config_rejects_vector_and_credentials_in_urls(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.local.json"
            for overrides in ({"queryType": "vector"}, {"projectEndpoint": "https://user:secret@example.com"}):
                path.write_text(json.dumps(self.config | overrides), encoding="utf-8")
                with self.assertRaises(demo.DemoError):
                    demo.load_config(path)

    def test_agent_reuses_owned_matching_definition(self):
        definition = self.definition()
        latest = SimpleNamespace(
            name=demo.AGENT_NAME, version="1", definition=definition,
            metadata={"demo": "policy-agent-first"},
        )
        project = Mock()
        project.agents.get.return_value.versions.latest = latest
        agent, fingerprint = demo.ensure_agent(project, definition)
        self.assertIs(agent, latest)
        self.assertEqual(fingerprint, demo.definition_hash(definition))
        project.agents.create_version.assert_not_called()
        latest.metadata = {}
        demo.ensure_agent(project, definition)
        project.agents.create_version.assert_called_once()

    def test_cleanup_deletes_only_recorded_owned_version(self):
        definition = self.definition()
        project = Mock()
        project.agents.get_version.return_value = SimpleNamespace(
            definition=definition, metadata={"demo": "policy-agent-first"},
        )
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "agent-state.local.json"
            path.write_text(json.dumps({
                "agentName": demo.AGENT_NAME, "version": "2",
                "projectEndpoint": self.config["projectEndpoint"],
                "definitionSha256": demo.definition_hash(definition),
            }), encoding="utf-8")
            demo.cleanup(project, self.config, path)
            project.agents.delete_version.assert_called_once_with(
                agent_name=demo.AGENT_NAME, agent_version="2",
            )
            self.assertFalse(path.exists())
            self.assertEqual([call[0] for call in project.mock_calls], [
                "agents.get_version", "agents.delete_version",
            ])

    def test_cleanup_refuses_unowned_version(self):
        project = Mock()
        project.agents.get_version.return_value = SimpleNamespace(metadata={})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "agent-state.local.json"
            path.write_text(json.dumps({
                "agentName": demo.AGENT_NAME, "version": "2",
                "projectEndpoint": self.config["projectEndpoint"],
            }), encoding="utf-8")
            with self.assertRaises(demo.DemoError):
                demo.cleanup(project, self.config, path)
            project.agents.delete_version.assert_not_called()
            self.assertTrue(path.exists())

    def test_intention_to_search_is_not_a_tool_call(self):
        with self.assertRaisesRegex(demo.DemoError, "No Azure AI Search call"):
            demo.describe_response(self.response([], "I will search now."))

    def test_failed_search_is_not_grounded_success(self):
        with self.assertRaisesRegex(demo.DemoError, "did not complete"):
            demo.describe_response(self.response([{"type": "azure_ai_search_call", "status": "failed"}]))

    def test_completed_search_requires_citation_or_insufficient_evidence(self):
        steps = [{"type": "azure_ai_search_call", "status": "completed"}]
        demo.describe_response(self.response(steps))
        demo.describe_response(self.response(steps, '"Retain evidence for 12 months." Source ID: access-review-2'))
        self.assertIn("Azure AI Search tool called: YES", self.output.getvalue())
        self.assertIn("not server-validated", self.output.getvalue())
        with self.assertRaisesRegex(demo.DemoError, "without citations"):
            demo.describe_response(self.response(steps, "Retain evidence for 12 months."))

    def test_responses_pattern_keeps_tool_selection_automatic(self):
        client = Mock()
        client.conversations.create.return_value.id = "test-conversation"
        client.responses.create.return_value = self.response([
            {"type": "azure_ai_search_call", "status": "completed"},
        ])
        demo.run_question(client, SimpleNamespace(name=demo.AGENT_NAME), demo.Q1)
        client.responses.create.assert_called_once_with(
            conversation="test-conversation",
            extra_body={"agent_reference": {"type": "agent_reference", "name": demo.AGENT_NAME}},
            input=demo.Q1,
        )

    def test_identifiers_and_tokens_are_redacted(self):
        self.assertEqual(demo.safe_text("00000000-0000-0000-0000-000000000001 eyJabc.abc.def"),
                         "[redacted-id] [redacted-token]")


if __name__ == "__main__":
    unittest.main()
