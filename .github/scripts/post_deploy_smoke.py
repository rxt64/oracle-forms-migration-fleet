#!/usr/bin/env python3
"""Exercise authenticated workbench persistence without authorizing or executing migration work."""

from __future__ import annotations

import io
import json
import os
import sys
import time
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
    response = HttpResponse(500, b"", {})
    for attempt in range(5):
        response = http_request(
            "GET",
            f"http://169.254.169.254/metadata/identity/oauth2/token?{query}",
            headers={"Metadata": "true"},
        )
        if response.status == 200:
            break
        if response.status not in {404, 410, 429} and response.status < 500:
            break
        if attempt < 4:
            time.sleep(2**attempt)

    if response.status != 200:
        identifier = "unknown"
        description = ""
        try:
            error = response.json()
            if isinstance(error, dict):
                if isinstance(error.get("error"), str):
                    identifier = error["error"][:64]
                if isinstance(error.get("error_description"), str):
                    description = " ".join(error["error_description"].split())[:240]
        except (UnicodeDecodeError, json.JSONDecodeError):
            pass
        detail = f" ({identifier}: {description})" if description else f" ({identifier})"
        raise RuntimeError(
            f"Managed identity token request failed with HTTP {response.status}{detail}."
        )

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


STACK_FIELDS = ("database", "frontEnd", "backEnd")


def select_target_profile(project: Any) -> Any:
    """Pick the one server-owned profile a project is bound to, from either project shape."""
    if not isinstance(project, dict):
        raise RuntimeError("Validation project was not returned as an object.")

    unavailable = project.get("targetProfileUnavailable")
    if isinstance(unavailable, str) and unavailable.strip():
        raise RuntimeError(f"Server recorded no target profile for the validation project: {unavailable}")

    if "targetProfile" in project:
        return project["targetProfile"]

    profiles = project.get("targetProfiles")
    if not isinstance(profiles, list) or not profiles:
        raise RuntimeError("Validation project has no server-owned target profile.")
    if any(not isinstance(item, dict) for item in profiles):
        raise RuntimeError("A target profile on the validation project was not returned as an object.")

    if len({item.get("targetProfileId") for item in profiles}) != 1:
        raise RuntimeError(
            "The validation project names more than one target profile, so the deployment's own "
            "profile identity is ambiguous."
        )

    # The server resolves an approval against the newest version of a profile identifier.
    return max(profiles, key=lambda item: item["version"] if isinstance(item.get("version"), int) else -1)


def target_profile_identity(profile: Any) -> tuple[str, int, dict[str, str]]:
    """Validate the server-owned target profile contract and return its identity and stack."""
    if not isinstance(profile, dict):
        raise RuntimeError("Validation project has no server-owned target profile.")

    profile_id = profile.get("targetProfileId")
    if not isinstance(profile_id, str) or not profile_id.strip():
        raise RuntimeError("Server-owned target profile carries no profile identifier.")

    version = profile.get("version")
    if not isinstance(version, int) or isinstance(version, bool) or version < 1:
        raise RuntimeError(f"Server-owned target profile '{profile_id}' carries no profile version.")

    stack = profile.get("stack")
    if not isinstance(stack, dict):
        raise RuntimeError(f"Server-owned target profile '{profile_id}' names no generated stack.")

    resolved: dict[str, str] = {}
    for field in STACK_FIELDS:
        value = stack.get(field)
        if not isinstance(value, str) or not value.strip():
            raise RuntimeError(
                f"Server-owned target profile '{profile_id}' does not name its {field}, "
                "so the validation request cannot match the deployment's target."
            )
        resolved[field] = value

    return profile_id, version, resolved


def revoke_failure(app_url: str, token: str, approval_id: str, version: int) -> str:
    """Revoke a created validation approval; return cleanup guidance, or "" once revoked."""
    try:
        response = json_request(
            "POST",
            f"{app_url}/api/workbench/approvals/{urllib.parse.quote(approval_id)}/revoke",
            token,
            {"expectedVersion": version, "notes": "Post-deployment validation cleanup."},
        )
        state = response.json().get("state") if response.status == 200 else None
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        return f"Validation approval {approval_id} could not be revoked during cleanup: {error}."
    if state == "Revoked":
        return ""
    return (
        f"Validation approval {approval_id} was not revoked: cleanup revocation returned "
        f"HTTP {response.status}. This ValidationOnly record cannot authorize side effects, but revoke "
        "it by hand to complete lifecycle cleanup."
    )


def run() -> None:
    app_url = os.environ["APP_URL"].rstrip("/")
    app_resource = os.environ["APP_RESOURCE"]
    primary_client_id = os.environ["PRIMARY_CLIENT_ID"]
    unauthorized_client_id = os.environ["UNAUTHORIZED_CLIENT_ID"]
    deployment_sha = os.environ["DEPLOYMENT_SHA"]
    project_name = os.environ.get(
        "VALIDATION_PROJECT_NAME", f"Deployment validation {deployment_sha[:12]}"
    )

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
    project = next(
        (item for item in projects if isinstance(item, dict) and item.get("name") == project_name), None
    )
    if project is None:
        created = json_request(
            "POST",
            f"{app_url}/api/workbench/projects",
            primary_token,
            {"name": project_name},
        )
        require_status(created, 201, "Validation project creation")
        project = created.json()

    # The target stack and the profile identity are deployment facts the server owns. Reading them back
    # keeps this check honest across a release that changes the generated stack.
    profile_id, profile_version, stack = target_profile_identity(select_target_profile(project))

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
        "target": {
            "frontEnd": stack["frontEnd"],
            "backEnd": stack["backEnd"],
            "database": stack["database"],
        },
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

    approval_id = ""
    approval_version: int | None = None
    approval_revoked = False
    try:
        requested = json_request(
            "POST",
            f"{app_url}/api/workbench/projects/{project_id}/approvals",
            primary_token,
            {
                "workspaceId": workspace_id,
                "targetProfileId": profile_id,
                "scope": "ValidationOnly",
                "lifetimeMinutes": 5,
                "notes": f"Post-deployment validation for {deployment_sha[:12]}.",
                "request": run_request,
            },
        )
        require_status(requested, 201, "Validation approval request")
        approval = requested.json()

        # Capture what the server created before asserting anything about it, so a failed assertion
        # still leaves cleanup able to revoke the approval it just made.
        created_id = approval.get("approvalId")
        if isinstance(created_id, str) and created_id:
            approval_id = created_id
        created_version = approval.get("version")
        if isinstance(created_version, int) and not isinstance(created_version, bool):
            approval_version = created_version

        if approval.get("state") != "Requested" or approval.get("scope") != "ValidationOnly":
            raise RuntimeError("Validation approval was not persisted in the expected non-writing state.")
        if (
            approval.get("targetProfileId") != profile_id
            or approval.get("targetProfileVersion") != profile_version
        ):
            raise RuntimeError("Validation approval was not bound to the project's server-owned target profile.")
        if not approval_id or approval_version is None:
            raise RuntimeError("Validation approval was created without an identifier and version to revoke.")

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
            {"expectedVersion": approval_version, "notes": "Post-deployment validation completed."},
        )
        require_status(revoked, 200, "Validation approval revocation")
        if revoked.json().get("state") != "Revoked":
            raise RuntimeError("Validation approval was not revoked.")
        approval_revoked = True

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
        failing = sys.exc_info()[0] is not None
        if approval_id and approval_version is not None and not approval_revoked:
            reason = revoke_failure(app_url, primary_token, approval_id, approval_version)
            if reason and not failing:
                raise RuntimeError(reason)
            if reason:
                print(reason)
        released = http_request(
            "DELETE",
            f"{app_url}/api/workbench/source/{urllib.parse.quote(workspace_id)}?projectId={urllib.parse.quote(project_id)}",
            token=primary_token,
        )
        require_status(released, 204, "Validation workspace release")

    print("Post-deployment workbench verification passed without executing migration work.")


if __name__ == "__main__":
    run()