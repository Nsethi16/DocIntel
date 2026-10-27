"""Validate the native diagnostic's closed output schema and synthetic request."""

import importlib.util
import json
from pathlib import Path
import unittest

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

    def test_remote_code_is_fixed_synthetic_input_and_never_logs_token(self) -> None:
        compile(MODULE.REMOTE, "<native-diagnostic>", "exec")
        self.assertIn('"state":"Provider connectivity check."', MODULE.REMOTE)
        self.assertIn("/providers/microsoft/v1/systemone", MODULE.REMOTE)
        self.assertNotIn("print(token", MODULE.REMOTE)
        self.assertNotIn("print(data", MODULE.REMOTE)
        self.assertNotIn("print(error", MODULE.REMOTE)
        self.assertNotIn("subprocess", MODULE.REMOTE)


if __name__ == "__main__":
    unittest.main()
