"""Validate the native diagnostic's closed output schema and synthetic request."""

import importlib.util
import json
from pathlib import Path
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location(
    "decision_diagnostic", Path(__file__).parents[1] / "scripts" / "decision_diagnostic.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class DecisionDiagnosticTests(unittest.TestCase):
    """Prove remote output cannot disclose tokens, provider messages or documents."""

    def test_only_projected_record_is_returned(self) -> None:
        record = {"schemaVersion": 1, "httpStatus": 403, "providerCode": "AccessDenied",
                  "answerPresent": False, "probabilityShape": "None"}
        self.assertEqual(MODULE.parse_result("sensitive ignored line\n" + MODULE.PREFIX + json.dumps(record)), record)
        for key, value in (("token", "private"), ("message", "private"), ("httpStatus", True),
                           ("providerCode", "private"), ("probabilityShape", "private")):
            with self.subTest(key=key):
                with self.assertRaises(ValueError):
                    MODULE.parse_result(MODULE.PREFIX + json.dumps({**record, key: value}))
        with self.assertRaises(ValueError):
            MODULE.parse_result((MODULE.PREFIX + json.dumps(record) + "\n") * 2)

    def test_only_capability_gated_isolated_dotnet_mode_can_execute(self) -> None:
        command = MODULE.diagnostic_command()
        self.assertEqual("dotnet /app/DocIntel.Api.dll --diagnose-decision", command)
        self.assertNotIn("python3", command)
        self.assertNotIn("IDENTITY_HEADER", command)
        self.assertNotIn("Authorization", command)

    def test_published_marker_is_required_before_dotnet_execution(self) -> None:
        metadata = [{"id": "subscription"}, {"properties": {
            "latestRevisionName": "revision",
            "template": {"containers": [{"name": "api"}]},
        }}, [{"name": "replica"}]]
        record = {"schemaVersion": 1, "httpStatus": 400, "providerCode": "UnclassifiedError",
                  "answerPresent": False, "probabilityShape": "None"}
        marker = "Native Decision diagnostic schema 1; isolated synthetic inference only."
        with mock.patch.dict(MODULE.os.environ, {
            "AZURE_RESOURCE_GROUP": "group", "AZURE_CONTAINER_APP_NAME": "app", "DOCINTEL_CONTAINER_NAME": "",
        }):
            with mock.patch.object(MODULE, "inspect", side_effect=metadata), mock.patch.object(
                MODULE, "execute", side_effect=[marker, MODULE.PREFIX + json.dumps(record)]
            ) as execute:
                self.assertEqual(MODULE.probe(), record)
                self.assertEqual(execute.call_args_list[0].args[0][-1], "cat /app/docintel-decision-probe.v1")
                self.assertEqual(execute.call_args_list[1].args[0][-1], MODULE.diagnostic_command())
            with mock.patch.object(MODULE, "inspect", side_effect=metadata), mock.patch.object(
                MODULE, "execute", return_value="no marker"
            ) as execute:
                with self.assertRaises(ValueError):
                    MODULE.probe()
                self.assertEqual(execute.call_count, 1)


if __name__ == "__main__":
    unittest.main()
