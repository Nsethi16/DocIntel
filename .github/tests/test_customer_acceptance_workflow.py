"""Exercise the actual operator workflow guards and closed metadata projection without cloud calls."""

import ast
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import textwrap
import unittest
from unittest import mock


WORKFLOW = (Path(__file__).parents[1] / "workflows" / "diagnose-ask-ai.yml").read_text(encoding="utf-8")
CASES = (
    "documents", "submit-documents", "ask-termination", "ask-analysis", "ask-followup",
    "ask-no-evidence", "ask-injection", "ask-long-tail",
)
BASIS = "3c929e3bd68f421baeaf801601cbe77e"


def block(name: str, shell: str) -> str:
    """Read the real inline command rather than duplicating its implementation."""
    match = re.search(
        rf"      - name: {re.escape(name)}\r?\n        shell: {shell}\r?\n"
        r"(?:        env:\r?\n(?:          [^\r\n]*\r?\n)+)?"
        r"        run: \|\r?\n(?P<script>(?:(?: {10}[^\r\n]*)?\r?\n| {10}[^\r\n]+$)+)",
        WORKFLOW,
    )
    if match is None:
        raise AssertionError(f"Missing bounded {shell} workflow block.")
    return textwrap.dedent(match["script"])


GUARD = block("Validate explicit diagnostic input and Azure identity consistency", "pwsh")
RUNTIME = block("Run the bounded operator diagnostic in the active app identity", "python")


def environment(**changes: str) -> dict[str, str]:
    """Supply only test identifiers and clear any caller identity-secret environment variables."""
    return {
        **os.environ,
        "DIAGNOSTIC_MODE": "CustomerAcceptance", "DIAGNOSTIC_TENANT": "contoso-legal",
        "DIAGNOSTIC_RUN": BASIS, "DIAGNOSTIC_CUSTOMER_CASE": "documents",
        "DIAGNOSTIC_CONFIRMATION": "TEST", "TRANSLATION_VARIANT": "Configured",
        "PERSONAL_CLIENT_ID": "", "PERSONAL_TENANT_ID": "", "PERSONAL_SUBSCRIPTION_ID": "",
        "AZURE_RESOURCE_GROUP": "test-group", "AZURE_CONTAINER_APP_NAME": "test-app",
        "DOCINTEL_CONTAINER_NAME": "", **changes,
    }


def record(**changes: object) -> dict[str, object]:
    """Build a metadata-only response; no real source text, actor or answer is present."""
    return {
        "phase": "CustomerAcceptance", "customerCase": "documents", "scenario": "Summary",
        "status": "Completed", "failureCode": "None", "assertions": [], **changes,
    }


class CustomerAcceptanceWorkflowTests(unittest.TestCase):
    """Protect explicit authorization, command isolation, timeout and output-redaction boundaries."""

    def test_workflow_remains_manual_main_only_oidc_environment_locked_and_bounded(self) -> None:
        self.assertIn("workflow_dispatch:", WORKFLOW)
        self.assertIn("if: github.ref == 'refs/heads/main'", WORKFLOW)
        self.assertIn("environment: docintel-dev", WORKFLOW)
        self.assertIn("id-token: write", WORKFLOW)
        self.assertIn("cancel-in-progress: false", WORKFLOW)
        self.assertIn("timeout-minutes: 10", WORKFLOW)
        self.assertIn("timeout=420", RUNTIME)
        self.assertNotIn("${{ inputs.", RUNTIME)
        self.assertNotIn("containerapp update", RUNTIME)
        self.assertNotIn("Authorization", RUNTIME)
        self.assertNotIn("--account-key", RUNTIME)

    def test_all_cases_require_exact_test_confirmation_and_basis_id(self) -> None:
        for case in CASES:
            with self.subTest(case=case):
                result = subprocess.run(
                    ["pwsh", "-NoProfile", "-NonInteractive", "-Command", GUARD],
                    env=environment(DIAGNOSTIC_CUSTOMER_CASE=case),
                    capture_output=True, text=True, timeout=30, check=False,
                )
                self.assertEqual(0, result.returncode, result.stderr)
        for invalid in (
            {"DIAGNOSTIC_CONFIRMATION": ""},
            {"DIAGNOSTIC_CONFIRMATION": "test"},
            {"DIAGNOSTIC_TENANT": "other-workspace"},
            {"DIAGNOSTIC_TENANT": "CONTOSO-LEGAL"},
            {"DIAGNOSTIC_RUN": "recent"},
            {"DIAGNOSTIC_RUN": "a" * 64},
            {"DIAGNOSTIC_RUN": ""},
            {"DIAGNOSTIC_CUSTOMER_CASE": "upload-arbitrary-path"},
            {"DIAGNOSTIC_CUSTOMER_CASE": "documents; echo private"},
            {"TRANSLATION_VARIANT": "WithoutGlossary"},
        ):
            with self.subTest(invalid=invalid):
                result = subprocess.run(
                    ["pwsh", "-NoProfile", "-NonInteractive", "-Command", GUARD],
                    env=environment(**invalid), capture_output=True, text=True, timeout=30, check=False,
                )
                self.assertNotEqual(0, result.returncode)

    def execute_runtime(self, case: str, records: list[dict[str, object]],
                        returncode: int = 0) -> tuple[str, list[str]]:
        """Mock the process boundary so no Azure CLI or remote command can actually execute."""
        commands = []

        def run(arguments: list[str], **_: object) -> subprocess.CompletedProcess:
            if arguments[:3] == ["az", "containerapp", "show"]:
                value = {"properties": {"latestRevisionName": "revision",
                                       "template": {"containers": [{"name": "api"}]}}}
                return subprocess.CompletedProcess(arguments, 0, json.dumps(value), "")
            if arguments[:3] == ["az", "containerapp", "replica"]:
                return subprocess.CompletedProcess(arguments, 0, '[{"name":"replica"}]', "")
            self.assertEqual("script", arguments[0])
            command = arguments[arguments.index("-c") + 1]
            commands.append(command)
            output = "private raw process text must not be published\n" + "\n".join(
                "DOCINTEL_DIAGNOSTIC " + json.dumps(item) for item in records
            )
            return subprocess.CompletedProcess(arguments, returncode, output, "private stderr")

        output = io.StringIO()
        with mock.patch.dict(os.environ, environment(DIAGNOSTIC_CUSTOMER_CASE=case), clear=True):
            with mock.patch("subprocess.run", side_effect=run), redirect_stdout(output):
                exec(compile(RUNTIME, "<bounded-operator-workflow>", "exec"), {})
        return output.getvalue(), commands

    def test_each_dispatch_selects_one_exact_cli_case_and_never_a_public_endpoint(self) -> None:
        for case in CASES:
            with self.subTest(case=case):
                output, commands = self.execute_runtime(case, [record(customerCase=case)])
                self.assertNotIn("private", output)
                self.assertEqual(1, len(commands))
                remote = shlex.split(commands[0])
                command = shlex.split(remote[remote.index("--command") + 1])
                self.assertEqual([
                    "dotnet", "/app/DocIntel.Api.dll", "--diagnose-customer-acceptance", BASIS,
                    "--diagnose-customer-case", case, "--diagnose-customer-live", "true",
                    "--diagnose-tenant", "contoso-legal",
                ], command)

    def test_customer_output_rejects_unknown_nested_data_and_untrusted_codes(self) -> None:
        invalid = (
            {"answer": "private answer"},
            {"failureCode": "private provider message"},
            {"jobId": "private identity"},
            {"runId": BASIS},
            {"pages": True},
            {"assertions": [{"id": "private", "status": "Passed"}]},
            {"assertions": [{"id": "RunCompleted", "status": "Passed", "answer": "private"}]},
            {"sources": [{"jobId": BASIS, "sourceSha256": "a" * 64, "fileName": "private"}]},
        )
        for change in invalid:
            with self.subTest(change=change):
                with self.assertRaises(RuntimeError):
                    self.execute_runtime("documents", [record(**change)])

    def test_partial_refused_failed_or_missing_results_never_become_success(self) -> None:
        for records in (
            [], [record(scenario="A")], [record(status="Failed")], [record(status="Refused")],
            [record(status="Pending")], [record(phase="Stored")],
        ):
            with self.subTest(records=records):
                with self.assertRaises((RuntimeError, SystemExit)):
                    self.execute_runtime("documents", records)
        with self.assertRaises(SystemExit):
            self.execute_runtime("documents", [record()], returncode=1)

    def test_schema_projection_is_a_standalone_closed_function(self) -> None:
        tree = ast.parse(RUNTIME)
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                        and node.name == "customer_record")
        namespace = {"os": os, "re": re}
        exec(compile(ast.Module(body=[function], type_ignores=[]), "<metadata-schema>", "exec"), namespace)
        with mock.patch.dict(os.environ, {"DIAGNOSTIC_CUSTOMER_CASE": "documents"}):
            value = record(runId="a" * 64, sources=[{"jobId": BASIS, "sourceSha256": "b" * 64}])
            self.assertEqual(value, namespace["customer_record"](value))


if __name__ == "__main__":
    unittest.main()
