#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch


MODULE_PATH = Path(__file__).with_name("post_deploy_smoke.py")
SPEC = importlib.util.spec_from_file_location("post_deploy_smoke", MODULE_PATH)
assert SPEC and SPEC.loader
SMOKE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = SMOKE
SPEC.loader.exec_module(SMOKE)


def response(status: int, value: object | bytes = b""):
    body = value if isinstance(value, bytes) else json.dumps(value).encode("utf-8")
    return SMOKE.HttpResponse(status, body, {})


class FakeWorkbench:
    def __init__(self) -> None:
        self.project_created = False
        self.approval_state = ""
        self.calls: list[tuple[str, str, str | None]] = []

    def request(self, method: str, url: str, *, token=None, body=None, headers=None):
        path = url.split("https://workbench.example", 1)[-1]
        self.calls.append((method, path, token))
        if path == "/readiness":
            return response(200, b"Healthy")
        if path == "/api/workbench/bootstrap":
            return response(200 if token == "primary-token" else 403 if token else 401)
        if path == "/api/workbench/context":
            projects = []
            if self.project_created:
                projects = [{"projectId": "prj-validation", "name": "Deployment validation"}]
            return response(
                200,
                {
                    "authentication": {"mode": "ContainerApps"},
                    "persistence": {"configured": True},
                    "projects": projects,
                },
            )
        if method == "POST" and path == "/api/workbench/projects":
            self.project_created = True
            return response(
                201,
                {
                    "projectId": "prj-validation",
                    "name": "Deployment validation",
                    "targetProfile": {"targetProfileId": "sandbox"},
                },
            )
        if method == "POST" and path.startswith("/api/workbench/source/upload?"):
            event = {"workspace": {"workspaceId": "workspace-validation"}}
            return response(200, f"data: {json.dumps(event)}\n\n".encode())
        if method == "POST" and path.endswith("/approvals"):
            payload = json.loads(body)
            if payload["scope"] != "ValidationOnly":
                return response(400)
            self.approval_state = "Requested"
            return response(
                201,
                {
                    "approvalId": "approval-validation",
                    "state": "Requested",
                    "scope": "ValidationOnly",
                    "version": 1,
                },
            )
        if method == "GET" and path.endswith("/approvals"):
            approvals = []
            if self.approval_state:
                approvals = [
                    {
                        "approvalId": "approval-validation",
                        "state": self.approval_state,
                        "isEffective": False,
                    }
                ]
            return response(200, {"approvals": approvals})
        if method == "POST" and path == "/api/workbench/approvals/approval-validation/revoke":
            self.approval_state = "Revoked"
            return response(200, {"state": "Revoked", "version": 2})
        if method == "DELETE" and path.startswith("/api/workbench/source/workspace-validation?"):
            return response(204)
        raise AssertionError(f"Unexpected request: {method} {path}")


class PostDeploySmokeTests(unittest.TestCase):
    def test_managed_identity_retries_transient_errors_and_returns_token(self) -> None:
        responses = [
            response(500, {"error": "temporarily_unavailable"}),
            response(429, {"error": "throttled"}),
            response(200, {"access_token": "secret-token"}),
        ]
        with (
            patch.object(SMOKE, "http_request", side_effect=responses),
            patch.object(SMOKE.time, "sleep"),
        ):
            self.assertEqual("secret-token", SMOKE.managed_identity_token("client", "resource"))

    def test_managed_identity_failure_reports_bounded_error_without_token(self) -> None:
        failure = response(
            400,
            {
                "error": "invalid_resource",
                "error_description": "The requested resource is not configured.",
                "access_token": "must-not-appear",
            },
        )
        with patch.object(SMOKE, "http_request", return_value=failure):
            with self.assertRaisesRegex(
                RuntimeError,
                r"HTTP 400 \(invalid_resource: The requested resource is not configured\.\)",
            ) as raised:
                SMOKE.managed_identity_token("client", "resource")
        self.assertNotIn("must-not-appear", str(raised.exception))

    def test_smoke_sequence_is_authenticated_persistent_and_non_writing(self) -> None:
        fake = FakeWorkbench()
        environment = {
            "APP_URL": "https://workbench.example",
            "APP_RESOURCE": "api://workbench",
            "PRIMARY_CLIENT_ID": "primary-client",
            "UNAUTHORIZED_CLIENT_ID": "secondary-client",
            "DEPLOYMENT_SHA": "0123456789abcdef0123456789abcdef01234567",
        }

        def token(client_id: str, resource: str) -> str:
            self.assertEqual("api://workbench", resource)
            return "primary-token" if client_id == "primary-client" else "secondary-token"

        with (
            patch.dict(os.environ, environment, clear=True),
            patch.object(SMOKE, "managed_identity_token", side_effect=token),
            patch.object(SMOKE, "http_request", side_effect=fake.request),
        ):
            SMOKE.run()

        self.assertEqual("Revoked", fake.approval_state)
        self.assertFalse(any("/execute" in path for _, path, _ in fake.calls))
        self.assertIn(("GET", "/api/workbench/bootstrap", None), fake.calls)
        self.assertIn(("GET", "/api/workbench/bootstrap", "secondary-token"), fake.calls)
        self.assertIn(("GET", "/api/workbench/bootstrap", "primary-token"), fake.calls)


if __name__ == "__main__":
    unittest.main()