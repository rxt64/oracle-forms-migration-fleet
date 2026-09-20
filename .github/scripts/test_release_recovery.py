#!/usr/bin/env python3

from release_recovery import decide_recovery


def main() -> None:
    compatible = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=True,
        applied_schema=3,
        previous_min_schema=0,
        previous_max_schema=3,
    )
    assert compatible.action == "rollback"

    lower_boundary = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=True,
        applied_schema=2,
        previous_min_schema=2,
        previous_max_schema=3,
        rollback_mutation_succeeded=True,
        rollback_readiness_succeeded=True,
    )
    assert lower_boundary.action == "rollback"

    incompatible = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=True,
        applied_schema=3,
        previous_min_schema=0,
        previous_max_schema=2,
    )
    assert incompatible.action == "forward-recovery"

    unknown = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=True,
        applied_schema=3,
        previous_min_schema=None,
        previous_max_schema=None,
    )
    assert unknown.action == "forward-recovery"

    for invalid_min, invalid_max in ((-1, 3), (3, 2), (None, 3), (0, None)):
        malformed = decide_recovery(
            smoke_succeeded=False,
            cleanup_succeeded=True,
            applied_schema=3,
            previous_min_schema=invalid_min,
            previous_max_schema=invalid_max,
        )
        assert malformed.action == "forward-recovery"

    cleanup_failure = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=False,
        applied_schema=3,
        previous_min_schema=0,
        previous_max_schema=3,
    )
    assert cleanup_failure.action == "rollback"
    assert not cleanup_failure.cleanup_succeeded

    mutation_failure = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=True,
        applied_schema=3,
        previous_min_schema=0,
        previous_max_schema=3,
        rollback_mutation_succeeded=False,
    )
    assert mutation_failure.action == "rollback-failed"

    readiness_failure = decide_recovery(
        smoke_succeeded=False,
        cleanup_succeeded=True,
        applied_schema=3,
        previous_min_schema=0,
        previous_max_schema=3,
        rollback_mutation_succeeded=True,
        rollback_readiness_succeeded=False,
    )
    assert readiness_failure.action == "rollback-failed"

    passing_release_with_cleanup_failure = decide_recovery(
        smoke_succeeded=True,
        cleanup_succeeded=False,
        applied_schema=3,
        previous_min_schema=0,
        previous_max_schema=3,
    )
    assert passing_release_with_cleanup_failure.action == "none"
    assert not passing_release_with_cleanup_failure.release_succeeded

    print("Release recovery policy tests passed.")


if __name__ == "__main__":
    main()