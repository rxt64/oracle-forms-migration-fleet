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


ASPNET_STACK = {"database": "PostgreSql", "frontEnd": "React", "backEnd": "AspNetCore"}
JAVA_STACK = {"database": "PostgreSql", "frontEnd": "React", "backEnd": "JavaSpringBoot"}


def profile(stack: dict[str, str], *, profile_id: str = "sandbox", version: int = 1):
    return {
        "targetProfileId": profile_id,
        "version": version,
        "environmentName": "sandbox",
        "stack": dict(stack),
        "canonicalHash": "hash",
    }


class FakeWorkbench:
    """Stands in for the workbench, including its refusal of a stack the profile does not name."""

    def __init__(self, profiles: list[dict[str, object]] | None = None, *, existing: bool = False) -> None:
        self.profiles = profiles if profiles is not None else [profile(ASPNET_STACK)]
        self.project_created = existing
        self.project_name = "Deployment validation 0123456789ab" if existing else ""
        self.approval_state = ""
        self.approval_payload: dict[str, object] | None = None
        self.revocations: list[dict[str, object]] = []
        self.calls: list[tuple[str, str, str | None]] = []

    def _effective(self) -> dict[str, object] | None:
        candidates = [item for item in self.profiles if isinstance(item, dict)]
        if not candidates:
            return None
        return max(candidates, key=lambda item: item["version"] if isinstance(item.get("version"), int) else -1)

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
                projects = [
                    {
                        "projectId": "prj-validation",
                        "name": self.project_name,
                        "targetProfiles": self.profiles,
                    }
                ]
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
            self.project_name = json.loads(body)["name"]
            return response(
                201,
                {
                    "projectId": "prj-validation",
                    "name": self.project_name,
                    "targetProfile": self._effective(),
                },
            )
        if method == "POST" and path.startswith("/api/workbench/source/upload?"):
            event = {"workspace": {"workspaceId": "workspace-validation"}}
            return response(200, f"data: {json.dumps(event)}\n\n".encode())
        if method == "POST" and path.endswith("/approvals"):
            payload = json.loads(body)
            self.approval_payload = payload
            if payload["scope"] != "ValidationOnly":
                return response(400)
            bound = self._effective()
            assert bound is not None
            if payload.get("targetProfileId") != bound["targetProfileId"]:
                return response(404, {"error": "That project has no target profile with that identifier."})
            stack = bound["stack"]
            requested = payload["request"]["target"]
            if (
                requested["database"] != stack["database"]
                or requested["frontEnd"] != stack["frontEnd"]
                or requested["backEnd"] != stack["backEnd"]
            ):
                return response(409, {"error": "This project's target profile is immutable."})
            self.approval_state = "Requested"
            return response(
                201,
                {
                    "approvalId": "approval-validation",
                    "state": "Requested",
                    "scope": "ValidationOnly",
                    "targetProfileId": bound["targetProfileId"],
                    "targetProfileVersion": bound["version"],
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
            self.revocations.append(json.loads(body))
            self.approval_state = "Revoked"
            return response(200, {"state": "Revoked", "version": 2})
        if method == "DELETE" and path.startswith("/api/workbench/source/workspace-validation?"):
            return response(204)
        raise AssertionError(f"Unexpected request: {method} {path}")


ENVIRONMENT = {
    "APP_URL": "https://workbench.example",
    "APP_RESOURCE": "api://workbench",
    "PRIMARY_CLIENT_ID": "primary-client",
    "UNAUTHORIZED_CLIENT_ID": "secondary-client",
    "DEPLOYMENT_SHA": "0123456789abcdef0123456789abcdef01234567",
}


def token(client_id: str, resource: str) -> str:
    return "primary-token" if client_id == "primary-client" else "secondary-token"



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

        def checked_token(client_id: str, resource: str) -> str:
            self.assertEqual("api://workbench", resource)
            return token(client_id, resource)

        with (
            patch.dict(os.environ, ENVIRONMENT, clear=True),
            patch.object(SMOKE, "managed_identity_token", side_effect=checked_token),
            patch.object(SMOKE, "http_request", side_effect=fake.request),
        ):
            SMOKE.run()

        self.assertEqual("Revoked", fake.approval_state)
        self.assertEqual("Deployment validation 0123456789ab", fake.project_name)
        self.assertFalse(any("/execute" in path for _, path, _ in fake.calls))
        self.assertIn(("GET", "/api/workbench/bootstrap", None), fake.calls)
        self.assertIn(("GET", "/api/workbench/bootstrap", "secondary-token"), fake.calls)
        self.assertIn(("GET", "/api/workbench/bootstrap", "primary-token"), fake.calls)

        assert fake.approval_payload is not None
        self.assertEqual("ValidationOnly", fake.approval_payload["scope"])
        self.assertEqual("sandbox", fake.approval_payload["targetProfileId"])
        self.assertEqual(ASPNET_STACK, fake.approval_payload["request"]["target"])
        self.assertEqual("PlanOnly", fake.approval_payload["request"]["requestedMode"])

    def test_smoke_binds_legacy_java_target_profile(self) -> None:
        fake = FakeWorkbench([profile(JAVA_STACK)])
        self._run(fake)

        assert fake.approval_payload is not None
        self.assertEqual(JAVA_STACK, fake.approval_payload["request"]["target"])
        self.assertEqual("Revoked", fake.approval_state)

    def test_smoke_reuses_newest_version_of_existing_project_profile(self) -> None:
        fake = FakeWorkbench(
            [profile(JAVA_STACK, version=1), profile(ASPNET_STACK, version=2)], existing=True
        )
        self._run(fake)

        assert fake.approval_payload is not None
        self.assertEqual(ASPNET_STACK, fake.approval_payload["request"]["target"])
        self.assertNotIn(("POST", "/api/workbench/projects", "primary-token"), fake.calls)

    def test_smoke_refuses_profile_identity_the_approval_did_not_bind(self) -> None:
        fake = FakeWorkbench()
        original = fake.request

        def drifting(method: str, url: str, **kwargs):
            result = original(method, url, **kwargs)
            if method == "POST" and url.endswith("/approvals") and result.status == 201:
                drifted = json.loads(result.body)
                drifted["targetProfileVersion"] = 99
                return response(201, drifted)
            return result

        with self.assertRaisesRegex(RuntimeError, "server-owned target profile"):
            self._run(fake, handler=drifting)
        self.assertEqual("Revoked", fake.approval_state)
        self.assertEqual(
            [{"expectedVersion": 1, "notes": "Post-deployment validation cleanup."}], fake.revocations
        )
        self.assertTrue(any(method == "DELETE" for method, _, _ in fake.calls))

    def test_smoke_revokes_the_approval_when_a_later_check_fails(self) -> None:
        fake = FakeWorkbench()
        original = fake.request

        def losing_the_listing(method: str, url: str, **kwargs):
            if method == "GET" and url.endswith("/approvals"):
                return response(200, {"approvals": []})
            return original(method, url, **kwargs)

        with self.assertRaisesRegex(RuntimeError, "could not be retrieved"):
            self._run(fake, handler=losing_the_listing)
        self.assertEqual("Revoked", fake.approval_state)
        self.assertEqual(1, len(fake.revocations))
        self.assertTrue(any(method == "DELETE" for method, _, _ in fake.calls))

    def test_failed_cleanup_revocation_preserves_the_original_failure_and_releases_the_workspace(
        self,
    ) -> None:
        fake = FakeWorkbench()
        original = fake.request
        refused: list[str] = []

        def drifting_with_unrevokable_approval(method: str, url: str, **kwargs):
            if method == "POST" and url.endswith("/revoke"):
                refused.append(url)
                return response(409, {"error": "That approval changed while you were deciding it."})
            result = original(method, url, **kwargs)
            if method == "POST" and url.endswith("/approvals") and result.status == 201:
                drifted = json.loads(result.body)
                drifted["targetProfileVersion"] = 99
                return response(201, drifted)
            return result

        with patch("builtins.print") as printed:
            with self.assertRaisesRegex(RuntimeError, "server-owned target profile"):
                self._run(fake, handler=drifting_with_unrevokable_approval)
        self.assertEqual(1, len(refused))
        self.assertEqual("Requested", fake.approval_state)
        self.assertTrue(any(method == "DELETE" for method, _, _ in fake.calls))
        printed.assert_called_once_with(
            "Validation approval approval-validation was not revoked: cleanup revocation returned "
            "HTTP 409. This ValidationOnly record cannot authorize side effects, but revoke it by hand "
            "to complete lifecycle cleanup."
        )

    def test_cleanup_never_guesses_a_version_the_server_did_not_return(self) -> None:
        fake = FakeWorkbench()
        original = fake.request

        def drifting_without_a_version(method: str, url: str, **kwargs):
            result = original(method, url, **kwargs)
            if method == "POST" and url.endswith("/approvals") and result.status == 201:
                drifted = json.loads(result.body)
                drifted["targetProfileVersion"] = 99
                drifted.pop("version")
                return response(201, drifted)
            return result

        with self.assertRaisesRegex(RuntimeError, "server-owned target profile"):
            self._run(fake, handler=drifting_without_a_version)
        self.assertEqual([], fake.revocations)
        self.assertTrue(any(method == "DELETE" for method, _, _ in fake.calls))

    def test_malformed_or_missing_target_profile_fails_closed(self) -> None:
        cases: list[tuple[str, object]] = [
            ("no profile object", None),
            ("profile is not an object", "sandbox"),
            ("no identifier", {"version": 1, "stack": ASPNET_STACK}),
            ("blank identifier", {"targetProfileId": "  ", "version": 1, "stack": ASPNET_STACK}),
            ("no version", {"targetProfileId": "sandbox", "stack": ASPNET_STACK}),
            ("boolean version", {"targetProfileId": "sandbox", "version": True, "stack": ASPNET_STACK}),
            ("zero version", {"targetProfileId": "sandbox", "version": 0, "stack": ASPNET_STACK}),
            ("no stack", {"targetProfileId": "sandbox", "version": 1}),
            ("stack is not an object", {"targetProfileId": "sandbox", "version": 1, "stack": "AspNetCore"}),
            (
                "partial stack",
                {"targetProfileId": "sandbox", "version": 1, "stack": {"database": "PostgreSql"}},
            ),
            (
                "blank back end",
                {
                    "targetProfileId": "sandbox",
                    "version": 1,
                    "stack": {**ASPNET_STACK, "backEnd": "  "},
                },
            ),
            (
                "non-string front end",
                {"targetProfileId": "sandbox", "version": 1, "stack": {**ASPNET_STACK, "frontEnd": 7}},
            ),
        ]

        for label, malformed in cases:
            with self.subTest(label):
                with self.assertRaises(RuntimeError):
                    SMOKE.target_profile_identity(malformed)

    def test_project_without_any_target_profile_fails_closed(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "no server-owned target profile"):
            SMOKE.select_target_profile({"projectId": "prj", "targetProfiles": []})
        with self.assertRaisesRegex(RuntimeError, "no server-owned target profile"):
            SMOKE.select_target_profile({"projectId": "prj"})
        with self.assertRaisesRegex(RuntimeError, "not returned as an object"):
            SMOKE.select_target_profile("prj")
        with self.assertRaisesRegex(RuntimeError, "not returned as an object"):
            SMOKE.select_target_profile({"projectId": "prj", "targetProfiles": ["sandbox"]})

    def test_server_declined_profile_is_reported_rather_than_ignored(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "Server recorded no target profile"):
            SMOKE.select_target_profile(
                {
                    "projectId": "prj",
                    "targetProfile": None,
                    "targetProfileUnavailable": "Set OFM_TARGET_STACK_BACKEND.",
                }
            )

    def test_ambiguous_profile_identity_fails_closed(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "more than one target profile"):
            SMOKE.select_target_profile(
                {
                    "projectId": "prj",
                    "targetProfiles": [profile(ASPNET_STACK), profile(JAVA_STACK, profile_id="staging")],
                }
            )

    def test_smoke_fails_closed_when_deployment_records_no_stack(self) -> None:
        fake = FakeWorkbench([{"targetProfileId": "sandbox", "version": 1}])
        with self.assertRaisesRegex(RuntimeError, "names no generated stack"):
            self._run(fake)
        self.assertIsNone(fake.approval_payload)

    def _run(self, fake: FakeWorkbench, handler=None) -> None:
        with (
            patch.dict(os.environ, ENVIRONMENT, clear=True),
            patch.object(SMOKE, "managed_identity_token", side_effect=token),
            patch.object(SMOKE, "http_request", side_effect=handler or fake.request),
        ):
            SMOKE.run()



if __name__ == "__main__":
    unittest.main()