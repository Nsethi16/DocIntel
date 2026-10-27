"""Probe synthetic native Decision input using the exact deployed runtime identity."""

import json
import os
import re
import shlex
import signal
import subprocess
import sys

PREFIX = "DOCINTEL_DECISION_DIAGNOSTIC "
CODES = {
    "None", "UnclassifiedError", "AccessDenied", "Unauthorized", "Forbidden",
    "PermissionDenied", "not_found", "unknown_model", "DeploymentNotFound",
    "ResourceNotFound", "invalid_request_error", "ModelNotFound", "InvalidRequest",
    "NotFound", "ModelInvocationError", "InternalServerError", "AuthenticationTypeDisabled",
    "DiagnosticUnsupported",
    "BadRequest", "InvalidRequestContent", "MissingApiVersionParameter", "InvalidApiVersionParameter",
    "UnsupportedTransferEncoding", "ContentLengthRequired",
}
FIELDS = {"schemaVersion", "httpStatus", "providerCode", "answerPresent", "probabilityShape"}


def diagnostic_command() -> str:
    """Use a plain argv command, not shell quoting interpreted by the exec proxy."""
    return "dotnet /app/DocIntel.Api.dll --diagnose-decision"


def parse_result(text: str) -> dict:
    """Return one strictly projected status record; never copy arbitrary remote output."""
    records = []
    for line in text.splitlines():
        line = re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", line)
        position = line.find(PREFIX)
        if position < 0:
            continue
        record = json.loads(line[position + len(PREFIX):])
        if set(record) != FIELDS or type(record["schemaVersion"]) is not int or record["schemaVersion"] != 1:
            raise ValueError("Invalid native diagnostic schema.")
        if type(record["httpStatus"]) is not int or record["httpStatus"] not in {0, *range(100, 600)}:
            raise ValueError("Invalid HTTP status.")
        if record["providerCode"] not in CODES or type(record["answerPresent"]) is not bool:
            raise ValueError("Invalid native diagnostic status.")
        if record["probabilityShape"] not in {"None", "Object", "Array"}:
            raise ValueError("Invalid response shape.")
        records.append(record)
    if len(records) != 1:
        raise ValueError("Exactly one native diagnostic record is required.")
    return records[0]


def inspect(arguments: list[str]) -> object:
    """Inspect only the current federated subscription and exact application target."""
    result = subprocess.run(
        ["az", *arguments, "--only-show-errors", "--output", "json"],
        capture_output=True, text=True, timeout=45, check=False,
    )
    if result.returncode:
        raise ValueError("Native diagnostic target inspection failed.")
    return json.loads(result.stdout)


def probe() -> dict:
    """Execute one fixed synthetic request, never a document or a mutable application command."""
    subscription = inspect(["account", "show"])["id"]
    target = ["--subscription", subscription, "--resource-group", os.environ["AZURE_RESOURCE_GROUP"],
              "--name", os.environ["AZURE_CONTAINER_APP_NAME"]]
    app = inspect(["containerapp", "show", *target])
    containers = app["properties"]["template"]["containers"]
    name = os.environ.get("DOCINTEL_CONTAINER_NAME", "")
    selected = [item for item in containers if not name or item["name"] == name]
    if len(selected) != 1:
        raise ValueError("Select exactly one application container.")
    revision = app["properties"]["latestRevisionName"]
    replicas = inspect(["containerapp", "replica", "list", *target, "--revision", revision])
    if not replicas:
        raise ValueError("The exact deployed revision has no active replica.")
    command = [
        "az", "containerapp", "exec", *target, "--revision", revision, "--replica", replicas[0]["name"],
        "--container", selected[0]["name"], "--only-show-errors",
    ]
    marker = execute([*command, "--command", "cat /app/docintel-decision-probe.v1"])
    if "Native Decision diagnostic schema 1; isolated synthetic inference only." not in marker:
        raise ValueError("The exact revision did not return its published native diagnostic capability marker.")
    return parse_result(execute([*command, "--command", diagnostic_command()]))


def execute(command: list[str]) -> str:
    """Capture a bounded exec session without disclosing remote or CLI error text."""
    process = subprocess.Popen(
        ["script", "-q", "-e", "-c", shlex.join(command), "/dev/null"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, start_new_session=True,
        env={**os.environ, "TERM": "dumb"},
    )
    try:
        stdout, _ = process.communicate(timeout=90)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.communicate(timeout=5)
        raise TimeoutError("The native diagnostic exceeded its deadline.") from None
    if process.returncode and PREFIX not in stdout and (
        "Native Decision diagnostic schema 1; isolated synthetic inference only." not in stdout
    ):
        output = stdout.lower()
        if "authorizationfailed" in output or "does not have authorization" in output:
            raise PermissionError("The deployment identity lacks container exec diagnostic authority.")
        raise ValueError(f"The bounded container exec command exited with code {process.returncode}.")
    return stdout


if __name__ == "__main__":
    result = probe()
    print(json.dumps(result, separators=(",", ":")))
    sys.exit(0 if result["httpStatus"] == 200 and result["answerPresent"] else 1)
