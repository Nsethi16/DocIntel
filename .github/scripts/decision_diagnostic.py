"""Probe synthetic native Decision input using the exact deployed runtime identity."""

import base64
import json
import os
import re
import shlex
import signal
import subprocess

PREFIX = "DOCINTEL_DECISION_DIAGNOSTIC "
CODES = {
    "None", "UnclassifiedError", "AccessDenied", "Unauthorized", "Forbidden",
    "PermissionDenied", "not_found", "unknown_model", "DeploymentNotFound",
    "ResourceNotFound", "invalid_request_error", "ModelNotFound", "InvalidRequest",
    "NotFound", "ModelInvocationError", "InternalServerError", "AuthenticationTypeDisabled",
}
FIELDS = {"schemaVersion", "httpStatus", "providerCode", "answerPresent", "probabilityShape"}
REMOTE = r'''
import json, os, re, urllib.parse, urllib.request, urllib.error
codes = CODES_VALUE
result = {"schemaVersion":1,"httpStatus":0,"providerCode":"UnclassifiedError",
          "answerPresent":False,"probabilityShape":"None"}
try:
    endpoint = os.environ["AzureAI__DecisionEndpoint"]
    uri = urllib.parse.urlsplit(endpoint)
    if uri.scheme != "https" or uri.path not in ("", "/") or uri.query or uri.fragment or uri.username:
        raise ValueError("Invalid resource root")
    query = urllib.parse.urlencode({"api-version":"2019-08-01",
        "resource":"https://cognitiveservices.azure.com/","client_id":os.environ["AZURE_CLIENT_ID"]})
    token_request = urllib.request.Request(os.environ["IDENTITY_ENDPOINT"] + "?" + query,
        headers={"X-IDENTITY-HEADER":os.environ["IDENTITY_HEADER"]})
    with urllib.request.urlopen(token_request, timeout=15) as response:
        token = json.loads(response.read(65536))["access_token"]
    body = {"model":os.environ["AzureAI__DecisionDeploymentName"],"state":"Provider connectivity check.",
        "questions":{"document_type":{"type":"choice","instructions":"Select ready for this connectivity check.",
        "criteria":{"ready":"The connectivity-check input is present.","undefined":"The input is absent."}}}}
    request = urllib.request.Request(endpoint.rstrip("/") + "/providers/microsoft/v1/systemone",
        data=json.dumps(body).encode(),headers={"Content-Type":"application/json","Authorization":"Bearer " + token})
    try:
        with urllib.request.urlopen(request, timeout=45) as response:
            result["httpStatus"] = response.status
            data = json.loads(response.read(262144))
            answer = data.get("answers",{}).get("document_type",{})
            result["answerPresent"] = answer.get("choice") in ("ready","undefined")
            probabilities = answer.get("probabilities")
            result["probabilityShape"] = ("Object" if isinstance(probabilities,dict)
                else "Array" if isinstance(probabilities,list) else "None")
            result["providerCode"] = "None"
    except urllib.error.HTTPError as error:
        result["httpStatus"] = error.code
        try:
            code = json.loads(error.read(65536)).get("error",{}).get("code","")
            result["providerCode"] = code if code in codes else "UnclassifiedError"
        except (ValueError,TypeError,AttributeError):
            pass
except (KeyError,ValueError,TypeError,OSError,urllib.error.URLError):
    pass
print("DOCINTEL_DECISION_DIAGNOSTIC " + json.dumps(result,separators=(",",":")))
'''.replace("CODES_VALUE", repr(sorted(CODES)))


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
    code = "import base64;exec(base64.b64decode(" + repr(base64.b64encode(REMOTE.encode()).decode()) + "))"
    command = [
        "az", "containerapp", "exec", *target, "--revision", revision, "--replica", replicas[0]["name"],
        "--container", selected[0]["name"], "--command", shlex.join(["python3", "-c", code]),
        "--only-show-errors",
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
        raise TimeoutError("The native diagnostic exceeded its deadline.") from None
    return parse_result(stdout)


if __name__ == "__main__":
    print(json.dumps(probe(), separators=(",", ":")))
