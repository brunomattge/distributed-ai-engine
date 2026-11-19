import json
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
OPENAPI_PATH = REPOSITORY_ROOT / "docs" / "api" / "openapi.json"


class OpenApiContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = json.loads(OPENAPI_PATH.read_text(encoding="utf-8"))

    def test_document_describes_the_loopback_preview_and_routes(self):
        self.assertEqual("3.1.0", self.document["openapi"])
        self.assertEqual("http://127.0.0.1:5100", self.document["servers"][0]["url"])
        self.assertEqual({"get"}, set(self.document["paths"]["/v1/models"]))
        self.assertEqual({"post"}, set(self.document["paths"]["/v1/chat/completions"]))
        self.assertEqual({"get"}, set(self.document["paths"]["/api/inference/jobs/{jobId}"]))

    def test_completion_contract_names_supported_mode_limits_and_errors(self):
        operation = self.document["paths"]["/v1/chat/completions"]["post"]
        schema = self.document["components"]["schemas"]["ChatCompletionRequest"]
        self.assertEqual(["single_model_distributed"], schema["properties"]["workload_mode"]["enum"])
        self.assertEqual(2048, schema["properties"]["max_completion_tokens"]["maximum"])
        self.assertEqual(2048, schema["properties"]["max_tokens"]["maximum"])
        self.assertEqual(
            {"required": ["max_completion_tokens", "max_tokens"]},
            schema["not"],
        )
        self.assertIn("400", operation["responses"])
        self.assertIn("404", operation["responses"])
        self.assertIn("422", operation["responses"])
        self.assertIn("501", operation["responses"])
        self.assertIn("502", operation["responses"])
        self.assertIn("503", operation["responses"])

    def test_job_contract_lists_all_runtime_states_and_node_headers(self):
        schemas = self.document["components"]["schemas"]
        self.assertEqual(
            ["accepted", "running", "completed", "failed", "cancelled", "timed_out"],
            schemas["InferenceJob"]["properties"]["state"]["enum"],
        )
        headers = self.document["components"]["headers"]
        self.assertIn("SelectedNodes", headers)
        self.assertIn("hashed node IDs", headers["SelectedNodes"]["description"])

    def test_all_local_references_resolve(self):
        references = []

        def collect(value):
            if isinstance(value, dict):
                if "$ref" in value:
                    references.append(value["$ref"])
                for child in value.values():
                    collect(child)
            elif isinstance(value, list):
                for child in value:
                    collect(child)

        collect(self.document)
        self.assertTrue(references)
        for reference in references:
            self.assertTrue(reference.startswith("#/"), reference)
            target = self.document
            for component in reference[2:].split("/"):
                component = component.replace("~1", "/").replace("~0", "~")
                target = target[component]
            self.assertIsNotNone(target, reference)


if __name__ == "__main__":
    unittest.main()
