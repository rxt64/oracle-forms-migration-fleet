#!/usr/bin/env python3

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


REPOSITORY = "rxt64/oracle-forms-migration-fleet"
SHA = "0123456789abcdef0123456789abcdef01234567"
VERIFIER = Path(__file__).with_name("verify_required_ci.py")
REQUIRED_JOBS = (
    "Native source worker contract",
    "build-and-test",
    "Container image builds",
    "Guided UI browser checks",
)


def run_evidence(run: dict[str, Any], jobs: dict[str, Any], mode: str = "automatic") -> int:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        run_path = root / "run.json"
        jobs_path = root / "jobs.json"
        run_path.write_text(json.dumps(run), encoding="utf-8")
        jobs_path.write_text(json.dumps(jobs), encoding="utf-8")
        return subprocess.run(
            [sys.executable, str(VERIFIER), REPOSITORY, SHA, mode, str(run_path), str(jobs_path)],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        ).returncode


def passing_run() -> dict[str, Any]:
    return {
        "head_sha": SHA,
        "head_repository": {"full_name": REPOSITORY},
        "head_branch": "main",
        "event": "push",
        "path": ".github/workflows/ci.yml",
        "status": "in_progress",
        "conclusion": None,
    }


def passing_jobs() -> dict[str, Any]:
    return {
        "jobs": [
            {"name": "Native source worker contract", "status": "completed", "conclusion": "success"},
            {"name": "build-and-test", "status": "completed", "conclusion": "success"},
            {"name": "Container image builds", "status": "completed", "conclusion": "success"},
            {"name": "Guided UI browser checks", "status": "completed", "conclusion": "success"},
        ]
    }


def expect_failure(run: dict[str, Any], jobs: dict[str, Any], mode: str = "automatic") -> None:
    if run_evidence(run, jobs, mode) == 0:
        raise AssertionError("Expected CI verification to fail closed.")


def main() -> None:
    run = passing_run()
    jobs = passing_jobs()
    assert run_evidence(run, jobs) == 0

    for required in REQUIRED_JOBS:
        missing = {"jobs": [job for job in passing_jobs()["jobs"] if job["name"] != required]}
        expect_failure(run, missing)

    manual = {**run, "status": "completed", "conclusion": "success"}
    assert run_evidence(manual, jobs, "manual") == 0
    expect_failure(run, jobs, "manual")

    for conclusion in ("failure", "cancelled", "skipped", "timed_out"):
        changed = passing_jobs()
        changed["jobs"][2]["conclusion"] = conclusion
        expect_failure(run, changed)

    incomplete = passing_jobs()
    incomplete["jobs"][2].update(status="in_progress", conclusion=None)
    expect_failure(run, incomplete)

    expect_failure({**run, "head_sha": "f" * 40}, jobs)
    expect_failure({**run, "head_branch": "feature"}, jobs)
    expect_failure({**run, "event": "pull_request"}, jobs)
    expect_failure({**run, "path": ".github/workflows/other.yml"}, jobs)
    expect_failure({**run, "head_repository": {"full_name": "someone/fork"}}, jobs)

    duplicated = passing_jobs()
    duplicated["jobs"].append(duplicated["jobs"][0].copy())
    expect_failure(run, duplicated)

    print("Exact-SHA CI verifier tests passed.")


if __name__ == "__main__":
    main()