"""Verify runtime probe command boundaries without contacting Azure or Microsoft Graph."""

import argparse
import importlib.util
import json
from pathlib import Path
import shlex
import sys
import unittest
from unittest.mock import Mock, patch

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location(
    "directory_probe",
    Path(__file__).parent.parent / "scripts" / "Invoke-DocIntelDirectoryProbe.py",
)
PROBE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROBE)


class DirectoryProbeTests(unittest.TestCase):
    """Keep process output private and execute only a supported runtime diagnostic."""

    def setUp(self) -> None:
        self.record = {
            "schemaVersion": 1, "status": "Connected", "failureCode": "None",
            "directoryEnabled": False, "runtimeIdentityVerified": True,
            "ownerEligible": True, "searchVerified": True,
        }

    def test_only_one_complete_known_record_is_accepted(self) -> None:
        text = "private provider data\n" + PROBE.PREFIX + json.dumps(self.record)
        self.assertEqual(PROBE.parse_result(text), self.record)
        for invalid in (
            {**self.record, "ownerEligible": False},
            {**self.record, "rawToken": "must-not-escape"},
            {**self.record, "schemaVersion": True},
            {**self.record, "failureCode": "untrusted"},
        ):
            with self.assertRaises(ValueError):
                PROBE.parse_result(PROBE.PREFIX + json.dumps(invalid))
        with self.assertRaises(ValueError):
            PROBE.parse_result(text + "\n" + PROBE.PREFIX + json.dumps(self.record))

    def test_command_is_scoped_capability_gated_and_never_uses_deployer_tokens(self) -> None:
        guid = "11111111-1111-1111-1111-111111111111"
        args = argparse.Namespace(
            subscription=guid, resource_group="rg", app="app",
            image="registry/docintel@sha256:" + "a" * 64, tenant=guid, client=guid, owner=guid,
        )
        app = {"properties": {
            "latestRevisionName": "rev", "latestReadyRevisionName": "rev",
            "configuration": {"activeRevisionsMode": "Single"},
            "template": {"containers": [{"name": "docintel-api", "image": args.image, "env": [
                {"name": name, "value": guid} for name in (
                    "AZURE_CLIENT_ID", "Identity__ManagedIdentityClientId", "Identity__DirectoryTenantId",
                )
            ]}]},
        }}
        process = Mock()
        process.communicate.return_value = (PROBE.PREFIX + json.dumps(self.record), "private stderr")
        with patch.object(PROBE.os, "name", "posix"), patch.object(
            PROBE, "inspect", side_effect=[app, [{"name": "replica"}]]
        ), patch.object(PROBE.subprocess, "Popen", return_value=process) as start:
            self.assertEqual(PROBE.probe(args), self.record)
        command = start.call_args.args[0]
        self.assertEqual(command[:3], ["script", "-q", "-e"])
        azure_command = shlex.split(command[4])
        self.assertIn("--subscription", azure_command)
        self.assertIn("--revision", azure_command)
        diagnostic = azure_command[azure_command.index("--command") + 1]
        self.assertIn("docintel-directory-probe.v1", diagnostic)
        self.assertIn("--diagnose-directory", diagnostic)
        self.assertNotIn("access-token", diagnostic)
        self.assertTrue(start.call_args.kwargs["start_new_session"])
        app["properties"]["configuration"] = {
            "activeRevisionsMode": "Multiple",
            "ingress": {"traffic": [{"weight": 100, "revisionName": "old"}]},
        }
        with patch.object(PROBE.os, "name", "posix"), patch.object(
            PROBE, "inspect", return_value=app
        ), patch.object(PROBE.subprocess, "Popen") as start:
            self.assertEqual(PROBE.probe(args)["status"], "ActionRequired")
            start.assert_not_called()


if __name__ == "__main__":
    unittest.main()
