"""Run the bounded, read-only directory diagnostic in the exact deployed identity."""

import argparse
import json
import os
import re
import shlex
import signal
import subprocess

PREFIX = "DOCINTEL_DIRECTORY_DIAGNOSTIC "
FIELDS = {
    "schemaVersion", "status", "failureCode", "directoryEnabled",
    "runtimeIdentityVerified", "ownerEligible", "searchVerified",
}
CODES = {
    "None", "InvalidInput", "TenantMismatch", "RuntimeIdentityMismatch",
    "OwnerNotFound", "OwnerDisabled", "OwnerNotMember", "OwnerNoWorkAddress",
    "DirectoryForbidden", "DirectoryUnauthorized", "DirectoryUnavailable", "TimedOut",
}
STATUSES = {"Connected", "AwaitingAdminApproval", "ActionRequired", "TimedOut"}


def unavailable(code: str = "DirectoryUnavailable") -> dict:
    """Return a fixed failure record, never raw subprocess or provider output."""
    return {
        "schemaVersion": 1, "status": "ActionRequired", "failureCode": code,
        "directoryEnabled": False, "runtimeIdentityVerified": False,
        "ownerEligible": False, "searchVerified": False,
    }


def inspect(arguments: list[str], subscription: str) -> object:
    """Read management-plane metadata with an explicit subscription."""
    result = subprocess.run(
        ["az", *arguments, "--subscription", subscription, "--only-show-errors", "--output", "json"],
        capture_output=True, text=True, timeout=45, check=False,
    )
    if result.returncode:
        raise ValueError("Target inspection failed.")
    return json.loads(result.stdout)


def parse_result(text: str) -> dict:
    """Accept exactly one known diagnostic record and suppress every other line."""
    records = []
    for line in text.splitlines():
        line = re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", line)
        position = line.find(PREFIX)
        if position < 0:
            continue
        record = json.loads(line[position + len(PREFIX):])
        if set(record) != FIELDS or type(record["schemaVersion"]) is not int or record["schemaVersion"] != 1:
            raise ValueError("Unsupported diagnostic record.")
        if record["status"] not in STATUSES or record["failureCode"] not in CODES:
            raise ValueError("Unknown diagnostic result.")
        if any(type(record[key]) is not bool for key in FIELDS - {"schemaVersion", "status", "failureCode"}):
            raise ValueError("Invalid diagnostic flags.")
        if record["status"] == "Connected" and (
            record["failureCode"] != "None" or not all(
                record[key] for key in ("runtimeIdentityVerified", "ownerEligible", "searchVerified")
            )
        ):
            raise ValueError("Incomplete connection proof.")
        records.append(record)
    if len(records) != 1:
        raise ValueError("The diagnostic did not return one verified record.")
    return records[0]


def probe(args: argparse.Namespace) -> dict:
    """Execute only the fixed diagnostic command in the current ready revision."""
    guid = r"[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}"
    if any(not re.fullmatch(guid, value) for value in (args.subscription, args.tenant, args.client, args.owner)):
        return unavailable("InvalidInput")
    if os.name != "posix":
        return unavailable()
    target = ["--resource-group", args.resource_group, "--name", args.app]
    app = inspect(["containerapp", "show", *target], args.subscription)
    properties = app["properties"]
    revision = properties["latestRevisionName"]
    if not revision or revision != properties["latestReadyRevisionName"]:
        return unavailable()
    configuration = properties["configuration"]
    mode = configuration.get("activeRevisionsMode")
    if mode == "Multiple":
        traffic = [
            item for item in configuration["ingress"].get("traffic", [])
            if item.get("weight") == 100
            and (item.get("revisionName") == revision or item.get("latestRevision") is True)
        ]
        if len(traffic) != 1:
            return unavailable()
    elif mode != "Single":
        return unavailable()
    containers = [item for item in properties["template"]["containers"] if item["name"] == "docintel-api"]
    if len(containers) != 1 or containers[0]["image"] != args.image:
        return unavailable("RuntimeIdentityMismatch")
    settings = {item["name"]: item.get("value") for item in containers[0]["env"]}
    if settings.get("AZURE_CLIENT_ID") != args.client or settings.get("Identity__ManagedIdentityClientId") != args.client:
        return unavailable("RuntimeIdentityMismatch")
    if settings.get("Identity__DirectoryTenantId") != args.tenant:
        return unavailable("TenantMismatch")
    replicas = inspect(["containerapp", "replica", "list", *target, "--revision", revision], args.subscription)
    if not replicas:
        return unavailable()
    unsupported = shlex.quote(PREFIX + json.dumps(unavailable(), separators=(",", ":")))
    script = (
        "if [ ! -f /app/docintel-directory-probe.v1 ]; then "
        f"printf '%s\\n' {unsupported}; exit 2; fi; exec dotnet \"$@\""
    )
    diagnostic = shlex.join([
        "sh", "-c", script, "docintel-directory-probe", "/app/DocIntel.Api.dll", "--diagnose-directory", args.owner,
        "--diagnose-directory-tenant", args.tenant, "--diagnose-directory-client", args.client,
    ])
    command = [
        "az", "containerapp", "exec", *target, "--subscription", args.subscription,
        "--revision", revision, "--replica", replicas[0]["name"], "--container", "docintel-api",
        "--command", diagnostic, "--only-show-errors",
    ]
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
        return unavailable("TimedOut")
    return parse_result(stdout)


def main() -> None:
    """Print only the allowlisted result; failures never expose captured process data."""
    parser = argparse.ArgumentParser()
    for name in ("subscription", "resource-group", "app", "image", "tenant", "client", "owner"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    try:
        result = probe(args)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        result = unavailable()
    print(json.dumps(result, separators=(",", ":")))


if __name__ == "__main__":
    main()
