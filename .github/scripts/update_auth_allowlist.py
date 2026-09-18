#!/usr/bin/env python3
"""Build or verify a Container Apps authConfig principal allowlist without stringly CLI updates."""

from __future__ import annotations

import argparse
import json
import uuid
from pathlib import Path
from typing import Any


def canonical_id(value: str) -> str:
    return str(uuid.UUID(value))


def current_identities(document: dict[str, Any]) -> list[str]:
    try:
        values = document["properties"]["identityProviders"]["azureActiveDirectory"]["validation"][
            "defaultAuthorizationPolicy"
        ]["allowedPrincipals"]["identities"]
    except (KeyError, TypeError) as error:
        raise ValueError("The authConfig document has no allowed-principal identity array.") from error
    if not isinstance(values, list):
        raise ValueError("The authConfig allowed-principal identities value is not an array.")
    return values


def current_applications(document: dict[str, Any]) -> list[str]:
    try:
        values = document["properties"]["identityProviders"]["azureActiveDirectory"]["validation"][
            "defaultAuthorizationPolicy"
        ].get("allowedApplications", [])
    except (KeyError, TypeError) as error:
        raise ValueError("The authConfig document has no default authorization policy.") from error
    if not isinstance(values, list):
        raise ValueError("The authConfig allowed-applications value is not an array.")
    return values


def normalized_ids(values: list[Any]) -> set[str]:
    normalized: set[str] = set()
    for value in values:
        if not isinstance(value, str):
            continue
        try:
            normalized.add(canonical_id(value))
        except ValueError:
            continue
    return normalized


def build(
    document: dict[str, Any],
    required: set[str],
    denied: set[str],
    required_applications: set[str] | None = None,
    denied_applications: set[str] | None = None,
) -> dict[str, Any]:
    identities = (normalized_ids(current_identities(document)) - denied) | required
    policy = document["properties"]["identityProviders"]["azureActiveDirectory"]["validation"][
        "defaultAuthorizationPolicy"
    ]
    policy["allowedPrincipals"]["identities"] = sorted(identities)
    applications = (
        normalized_ids(current_applications(document)) - (denied_applications or set())
    ) | (required_applications or set())
    policy["allowedApplications"] = sorted(applications)
    return {"properties": document["properties"]}


def verify(
    document: dict[str, Any],
    required: set[str],
    denied: set[str],
    required_applications: set[str] | None = None,
    denied_applications: set[str] | None = None,
) -> None:
    values = current_identities(document)
    normalized = normalized_ids(values)
    if len(normalized) != len(values):
        raise ValueError("The stored allowlist contains a non-canonical or non-UUID principal value.")
    missing = required - normalized
    forbidden = denied & normalized
    if missing:
        raise ValueError(f"The stored allowlist is missing required principals: {sorted(missing)}.")
    if forbidden:
        raise ValueError(f"The stored allowlist contains denied principals: {sorted(forbidden)}.")

    applications = current_applications(document)
    normalized_applications = normalized_ids(applications)
    if len(normalized_applications) != len(applications):
        raise ValueError("The stored allowlist contains a non-canonical or non-UUID application value.")
    missing_applications = (required_applications or set()) - normalized_applications
    forbidden_applications = (denied_applications or set()) & normalized_applications
    if missing_applications:
        raise ValueError(f"The stored allowlist is missing required applications: {sorted(missing_applications)}.")
    if forbidden_applications:
        raise ValueError(f"The stored allowlist contains denied applications: {sorted(forbidden_applications)}.")


def parse_ids(values: list[str]) -> set[str]:
    return {canonical_id(value) for value in values}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("build", "verify"))
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path, nargs="?")
    parser.add_argument("--required", action="append", default=[])
    parser.add_argument("--denied", action="append", default=[])
    parser.add_argument("--required-application", action="append", default=[])
    parser.add_argument("--denied-application", action="append", default=[])
    args = parser.parse_args()

    document = json.loads(args.input.read_text(encoding="utf-8"))
    required = parse_ids(args.required)
    denied = parse_ids(args.denied)
    required_applications = parse_ids(args.required_application)
    denied_applications = parse_ids(args.denied_application)
    if args.mode == "build":
        if args.output is None:
            parser.error("build mode requires an output path")
        args.output.write_text(
            json.dumps(
                build(document, required, denied, required_applications, denied_applications),
                separators=(",", ":"),
            ),
            encoding="utf-8",
        )
    else:
        verify(document, required, denied, required_applications, denied_applications)


if __name__ == "__main__":
    main()