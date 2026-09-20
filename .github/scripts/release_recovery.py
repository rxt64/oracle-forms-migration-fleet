#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class RecoveryDecision:
    action: str
    reason: str
    cleanup_succeeded: bool
    release_succeeded: bool


def decide_recovery(
    *,
    smoke_succeeded: bool,
    cleanup_succeeded: bool,
    applied_schema: int,
    previous_min_schema: int | None,
    previous_max_schema: int | None,
    rollback_mutation_succeeded: bool | None = None,
    rollback_readiness_succeeded: bool | None = None,
) -> RecoveryDecision:
    if smoke_succeeded:
        return RecoveryDecision(
            action="none",
            reason="The release passed smoke verification.",
            cleanup_succeeded=cleanup_succeeded,
            release_succeeded=cleanup_succeeded,
        )

    metadata_is_valid = (
        previous_min_schema is not None
        and previous_max_schema is not None
        and 0 <= previous_min_schema <= previous_max_schema
    )
    rollback_is_compatible = (
        metadata_is_valid
        and previous_min_schema <= applied_schema <= previous_max_schema
    )
    if not rollback_is_compatible:
        if not metadata_is_valid:
            reason = "Previous release schema compatibility is unknown; forward recovery is required."
        else:
            reason = (
                f"Applied platform schema {applied_schema} is outside the previous release range "
                f"{previous_min_schema}..{previous_max_schema}; forward recovery is required."
            )
        return RecoveryDecision(
            action="forward-recovery",
            reason=reason,
            cleanup_succeeded=cleanup_succeeded,
            release_succeeded=False,
        )

    if rollback_mutation_succeeded is False:
        return RecoveryDecision(
            action="rollback-failed",
            reason="The compatible previous template could not be restored.",
            cleanup_succeeded=cleanup_succeeded,
            release_succeeded=False,
        )
    if rollback_readiness_succeeded is False:
        return RecoveryDecision(
            action="rollback-failed",
            reason="The compatible previous template was restored but did not become healthy and ready.",
            cleanup_succeeded=cleanup_succeeded,
            release_succeeded=False,
        )

    return RecoveryDecision(
        action="rollback",
        reason=(
            f"The previous release supports applied platform schema {applied_schema} "
            f"within range {previous_min_schema}..{previous_max_schema}."
        ),
        cleanup_succeeded=cleanup_succeeded,
        release_succeeded=False,
    )


def optional_integer(value: str) -> int | None:
    return None if value == "unknown" else int(value)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--smoke-succeeded", action="store_true")
    parser.add_argument("--cleanup-succeeded", action="store_true")
    parser.add_argument("--applied-schema", type=int, required=True)
    parser.add_argument("--previous-min-schema", type=optional_integer, required=True)
    parser.add_argument("--previous-max-schema", type=optional_integer, required=True)
    args = parser.parse_args()

    decision = decide_recovery(
        smoke_succeeded=args.smoke_succeeded,
        cleanup_succeeded=args.cleanup_succeeded,
        applied_schema=args.applied_schema,
        previous_min_schema=args.previous_min_schema,
        previous_max_schema=args.previous_max_schema,
    )
    print(json.dumps(asdict(decision), separators=(",", ":")))


if __name__ == "__main__":
    main()