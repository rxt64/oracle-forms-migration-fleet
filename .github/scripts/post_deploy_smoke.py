#!/usr/bin/env python3
"""Exercise authenticated workbench persistence without authorizing or executing migration work."""

from __future__ import annotations

import io
import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class HttpResponse:
    status: int
    body: bytes
    headers: dict[str, str]

    def json(self) -> Any:
        return json.loads(self.body.decode("utf-8"))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req: Any, fp: Any, code: int, msg: str, headers: Any, newurl: str) -> None:
        return None


OPENER = urllib.request.build_opener(NoRedirect)


def http_request(
    method: str,
    url: str,
    *,
    token: str | None = None,
    body: bytes | None = None,
    headers: dict[str, str] | None = None,
) -> HttpResponse:
    request_headers = {"Accept": "application/json", **(headers or {})}
    if token:
        request_headers["Authorization"] = f"Bearer {token}"

    request = urllib.request.Request(url, data=body, headers=request_headers, method=method)
    try:
        with OPENER.open(request, timeout=60) as response:
            return HttpResponse(response.status, response.read(), dict(response.headers.items()))
    except urllib.error.HTTPError as error:
        return HttpResponse(error.code, error.read(), dict(error.headers.items()))


def json_request(method: str, url: str, token: str, value: Any | None = None) -> HttpResponse:
    body = None if value is None else json.dumps(value, separators=(",", ":")).encode("utf-8")
    headers = {} if body is None else {"Content-Type": "application/json"}
    return http_request(method, url, token=token, body=body, headers=headers)


def managed_identity_token(client_id: str, resource: str) -> str:
    query = urllib.parse.urlencode(
        {"api-version": "2019-08-01", "resource": resource, "client_id": client_id}
    )
    response = http_request(
        "GET",
        f"http://169.254.169.254/metadata/identity/oauth2/token?{query}",
        headers={"Metadata": "true"},
    )
    if response.status != 200:
        raise RuntimeError(f"Managed identity token request failed with HTTP {response.status}.")

    token = response.json().get("access_token")
    if not isinstance(token, str) or not token:
        raise RuntimeError("Managed identity token response contained no access token.")
    return token


def tiny_source_zip() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("forms/VALIDATION.fmb", b"deployment-validation")
    return buffer.getvalue()


def multipart_archive(content: bytes) -> tuple[bytes, str]:
    boundary = f"----ofm-validation-{uuid.uuid4().hex}"
    body = b"".join(
        [
            f"--{boundary}\r\n".encode(),
            b'Content-Disposition: form-data; name="archive"; filename="validation.zip"\r\n',
            b"Content-Type: application/zip\r\n\r\n",
            content,
            f"\r\n--{boundary}--\r\n".encode(),
        ]
    )
    return body, f"multipart/form-data; boundary={boundary}"


def workspace_from_events(body: bytes) -> str:
    workspace_id = ""
    for line in body.decode("utf-8").splitlines():
        if not line.startswith("data:"):
            continue
        frame = json.loads(line[5:].strip())
        workspace = frame.get("workspace")
        if isinstance(workspace, dict) and isinstance(workspace.get("workspaceId"), str):
            workspace_id = workspace["workspaceId"]
    if not workspace_id:
        raise RuntimeError("Source upload completed without a workspace identifier.")
    return workspace_id


def require_status(response: HttpResponse, expected: int | set[int], action: str) -> None:
    accepted = {expected} if isinstance(expected, int) else expected
    if response.status not in accepted:
        raise RuntimeError(f"{action} returned HTTP {response.status}; expected {sorted(accepted)}.")


def run() -> None:
    app_url = os.environ["APP_URL"].rstrip("/")
    app_resource = os.environ["APP_RESOURCE"]
    primary_client_id = os.environ["PRIMARY_CLIENT_ID"]
    unauthorized_client_id = os.environ["UNAUTHORIZED_CLIENT_ID"]
    deployment_sha = os.environ["DEPLOYMENT_SHA"]
    project_name = os.environ.get("VALIDATION_PROJECT_NAME", "Deployment validation")

    primary_token = managed_identity_token(primary_client_id, app_resource)
    unauthorized_token = managed_identity_token(unauthorized_client_id, app_resource)

    readiness = http_request("GET", f"{app_url}/readiness")
    require_status(readiness, 200, "Readiness")
    if readiness.body.decode("utf-8").strip() != "Healthy":
        raise RuntimeError("Readiness did not report Healthy.")

    require_status(
        http_request("GET", f"{app_url}/api/workbench/bootstrap"),
        {401, 302},
        "Unauthenticated bootstrap",
    )
    require_status(
        http_request("GET", f"{app_url}/api/workbench/bootstrap", token=unauthorized_token),
        {401, 403},
        "Unauthorized identity bootstrap",
    )

    require_status(
        http_request("GET", f"{app_url}/api/workbench/bootstrap", token=primary_token),
        200,
        "Authenticated bootstrap",
    )

    context_response = json_request("GET", f"{app_url}/api/workbench/context", primary_token)
    require_status(context_response, 200, "Authenticated context")
    context = context_response.json()
    if context.get("authentication", {}).get("mode") != "ContainerApps":
        raise RuntimeError("Authenticated context did not report ContainerApps mode.")
    if context.get("persistence", {}).get("configured") is not True:
        raise RuntimeError("Platform persistence is not configured.")

    projects = context.get("projects", [])
    project = next((item for item in projects if item.get("name") == project_name), None)
    if project is None:
        created = json_request(
            "POST",
            f"{app_url}/api/workbench/projects",
            primary_token,
            {"name": project_name},
        )
        require_status(created, 201, "Validation project creation")
        project = created.json()
        if project.get("targetProfile") is None:
            raise RuntimeError("Validation project has no server-owned target profile.")

    project_id = project.get("projectId")
    if not isinstance(project_id, str) or not project_id:
        raise RuntimeError("Validation project has no project identifier.")

    require_status(
        json_request("GET", f"{app_url}/api/workbench/projects/{project_id}/approvals", primary_token),
        200,
        "Authorized project access",
    )

    upload_body, content_type = multipart_archive(tiny_source_zip())
    uploaded = http_request(
        "POST",
        f"{app_url}/api/workbench/source/upload?projectId={urllib.parse.quote(project_id)}",
        token=primary_token,
        body=upload_body,
        headers={"Content-Type": content_type, "Accept": "text/event-stream"},
    )
    require_status(uploaded, 200, "Validation source upload")
    workspace_id = workspace_from_events(uploaded.body)

    engagement_id = f"DEPLOY-{deployment_sha[:12]}-{uuid.uuid4().hex[:8]}"
    run_request = {
        "engagementId": engagement_id,
        "applicationName": "Deployment validation",
        "requestedMode": "PlanOnly",
        "target": {"frontEnd": "React", "backEnd": "JavaSpringBoot", "database": "PostgreSql"},
        "oracleFormsVersion": "12c",
        "oracleDatabaseVersion": "19c",
        "sourceRoot": "forms",
        "outputRoot": "validation-output",
        "evidence": [],
        "planApproval": {"decision": "Pending", "approverId": None, "notes": None},
        "executionApproval": {"decision": "Pending", "approverId": None, "notes": None},
        "productionApproval": {"decision": "Pending", "approverId": None, "notes": None},
        "attestations": [],
    }

    try:
        requested = json_request(
            "POST",
            f"{app_url}/api/workbench/projects/{project_id}/approvals",
            primary_token,
            {
                "workspaceId": workspace_id,
                "targetProfileId": "sandbox",
                "scope": "ValidationOnly",
                "lifetimeMinutes": 5,
                "notes": f"Post-deployment validation for {deployment_sha[:12]}.",
                "request": run_request,
            },
        )
        require_status(requested, 201, "Validation approval request")
        approval = requested.json()
        if approval.get("state") != "Requested" or approval.get("scope") != "ValidationOnly":
            raise RuntimeError("Validation approval was not persisted in the expected non-writing state.")

        approval_id = approval["approvalId"]
        listed = json_request(
            "GET", f"{app_url}/api/workbench/projects/{project_id}/approvals", primary_token
        )
        require_status(listed, 200, "Validation approval retrieval")
        if not any(item.get("approvalId") == approval_id for item in listed.json().get("approvals", [])):
            raise RuntimeError("Persisted validation approval could not be retrieved.")

        revoked = json_request(
            "POST",
            f"{app_url}/api/workbench/approvals/{approval_id}/revoke",
            primary_token,
            {"expectedVersion": approval["version"], "notes": "Post-deployment validation completed."},
        )
        require_status(revoked, 200, "Validation approval revocation")
        if revoked.json().get("state") != "Revoked":
            raise RuntimeError("Validation approval was not revoked.")

        persisted = json_request(
            "GET", f"{app_url}/api/workbench/projects/{project_id}/approvals", primary_token
        )
        require_status(persisted, 200, "Revoked approval retrieval")
        stored = next(
            (item for item in persisted.json().get("approvals", []) if item.get("approvalId") == approval_id),
            None,
        )
        if stored is None or stored.get("state") != "Revoked" or stored.get("isEffective") is not False:
            raise RuntimeError("Revoked validation approval was not durably retrieved as ineffective.")
    finally:
        released = http_request(
            "DELETE",
            f"{app_url}/api/workbench/source/{urllib.parse.quote(workspace_id)}?projectId={urllib.parse.quote(project_id)}",
            token=primary_token,
        )
        require_status(released, 204, "Validation workspace release")

    print("Post-deployment workbench verification passed without executing migration work.")


if __name__ == "__main__":
    run()