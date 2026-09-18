#!/usr/bin/env python3
"""Fail closed unless one trusted CI run passed every required deployment gate."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import Any


REQUIRED_JOBS = (
    "build-and-test",
    "Container image builds",
    "Guided UI browser checks",
)


class VerificationError(RuntimeError):
    pass


def read_json(path: str) -> dict[str, Any]:
    evidence = Path(path)
    if not evidence.is_file() or evidence.stat().st_size == 0:
        raise VerificationError(f"Required CI evidence file '{path}' is missing or empty.")

    try:
        value = json.loads(evidence.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise VerificationError(f"Required CI evidence file '{path}' is not readable JSON.") from error

    if not isinstance(value, dict):
        raise VerificationError(f"Required CI evidence file '{path}' must contain a JSON object.")
    return value


def require_text(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value:
        raise VerificationError(f"CI evidence field '{field}' is missing or empty.")
    return value


def verify(repository: str, sha: str, mode: str, run: dict[str, Any], jobs: dict[str, Any]) -> None:
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise VerificationError("The deployment SHA must be a full lowercase 40-character commit SHA.")
    if mode not in {"automatic", "manual"}:
        raise VerificationError("Verification mode must be automatic or manual.")

    head_repository = run.get("head_repository")
    actual_repository = head_repository.get("full_name") if isinstance(head_repository, dict) else None
    expected = {
        "head_sha": sha,
        "head_repository.full_name": repository,
        "head_branch": "main",
        "event": "push",
        "path": ".github/workflows/ci.yml",
    }
    actual = {
        "head_sha": run.get("head_sha"),
        "head_repository.full_name": actual_repository,
        "head_branch": run.get("head_branch"),
        "event": run.get("event"),
        "path": run.get("path"),
    }
    for field, expected_value in expected.items():
        if actual[field] != expected_value:
            raise VerificationError(
                f"CI evidence field '{field}' was '{actual[field]}', expected '{expected_value}'."
            )

    status = require_text(run.get("status"), "status")
    conclusion = run.get("conclusion") or ""
    if mode == "manual":
        if status != "completed" or conclusion != "success":
            raise VerificationError(
                "Manual deployment requires a completed successful CI workflow; "
                f"got status '{status}', conclusion '{conclusion}'."
            )
    elif status == "completed" and conclusion != "success":
        raise VerificationError(
            f"Automatic deployment cannot use a completed CI run with conclusion '{conclusion}'."
        )
    elif status not in {"in_progress", "completed"}:
        raise VerificationError(f"Automatic deployment cannot use CI run status '{status}'.")

    job_values = jobs.get("jobs")
    if not isinstance(job_values, list):
        raise VerificationError("CI jobs evidence must contain a jobs array.")

    for required_name in REQUIRED_JOBS:
        matching = [job for job in job_values if isinstance(job, dict) and job.get("name") == required_name]
        if len(matching) != 1:
            raise VerificationError(
                f"Required CI job '{required_name}' must appear exactly once; found {len(matching)}."
            )
        job = matching[0]
        if job.get("status") != "completed" or job.get("conclusion") != "success":
            raise VerificationError(
                f"Required CI job '{required_name}' did not pass: status '{job.get('status')}', "
                f"conclusion '{job.get('conclusion')}'."
            )


def main(argv: list[str]) -> int:
    if len(argv) != 6:
        print(
            "Usage: verify_required_ci.py <repository> <sha> <mode> <run-json> <jobs-json>",
            file=sys.stderr,
        )
        return 2

    try:
        verify(argv[1], argv[2], argv[3], read_json(argv[4]), read_json(argv[5]))
    except VerificationError as error:
        print(error, file=sys.stderr)
        return 1

    print(f"Verified required CI jobs for exact SHA {argv[2]}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))