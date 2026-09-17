# Behavioral IR: Oracle Forms Trigger Bodies

## Status

Implemented on `feat/behavioral-ir-trigger-bodies`.

Spec Kit is not installed in the current environment, so this directory records the equivalent specification artifacts without claiming that `/speckit.*` commands ran.

## Problem

The Forms XML parser retained a trigger's name and scope but discarded its PL/SQL body. Two exports with the same trigger identity and different behavior therefore produced the same trigger model and normalized IR. Downstream evidence could not detect that the source behavior had changed.

## Requirements

- **IR-001**: Read trigger text from both Oracle export forms: the `TriggerText` attribute and the Forms-namespaced `TriggerText` child element.
- **IR-002**: Retain XML-normalized body text as untrusted source text. Do not translate, execute, or treat it as verified behavior.
- **IR-003**: A changed body with unchanged trigger name and scope must change the normalized IR.
- **IR-004**: Version the trigger object contract so stale name-only IR is rejected clearly rather than interpreted under new semantics.
- **IR-005**: Reject malformed trigger entries, non-string bodies, blank bodies, and bodies longer than 200,000 characters. Never truncate silently.
- **IR-006**: A foreign-namespace `TriggerText` element must make the Forms export fail closed.
- **IR-007**: Existing findings continue to label trigger behavior unsupported until a separate conversion capability translates and tests it.

## Acceptance Criteria

1. Attribute and child-element trigger bodies round-trip parser -> IR -> strict reader.
2. Two otherwise identical exports with different trigger bodies produce different `forms-ir.json` content.
3. IR schema version is `2`, and version `1` is rejected by the existing version gate.
4. Malformed or oversized trigger bodies produce no module and an actionable refusal.
5. No application emitter consumes or executes retained body text in this increment.

## Out of Scope

- PL/SQL-to-Java translation.
- Program-unit body or LOV query preservation.
- Runtime equivalence claims.
- Native `.fmb` extraction without authorized Oracle tooling.
