# 009 - Export Source Facts

## Goal

Retain source evidence that the existing Forms projections discard, before making React, ASP.NET Core,
or PostgreSQL architecture decisions. Extend the current normalization adapter and neutral IR rather
than introduce another import service or representation.

## Acceptance

- Existing export intake and authorized SourceNormalization retain every module element in document order,
  qualified source-object paths, parent/order, declared attributes, and retained direct text.
- Facts are explicitly Declared. Absence is not a default, a decision, or runtime evidence.
- Preserve program-unit text, record-group queries, relation conditions, layout and control properties,
  references, unknown properties, and wrapper version without translating or decoding source again.
- Bind facts to the module source path and SHA-256 of UTF-8 parsed source text. This text digest is not
  the original-file byte digest or the workspace snapshot hash.
- IR v3 producer and strict reader round-trip facts; reject old IR, malformed trees, duplicate keys,
  inconsistent identities, unsupported fact kinds, and exceeded limits without truncation.
- Artifact retention uses the existing owner-scoped run path. Raw source text remains excluded from
  browser previews. Discovered counts are diagnostics, never completeness or verification percentages.
- Independently authored fixtures with different identities and omission mutations exercise the path.

## Tracks and boundaries

Export track: source fact retention is this increment. Persistent disposition decisions, versioned target
rules, generated .NET/PostgreSQL master/detail behavior, and GUI decision controls follow in separate
reviewable increments. No ledger decisions, generated target, or executed target tests are claimed here.

Native track: spec 007 prerequisites remain BlockedPrerequisite; spec 008 only proves a worker protocol.
No export import promotes a native source profile. No FMB, Oracle library, or source runtime is executed.

Summit qualification is pinned in summit-source-manifest.json. No applicable license grant was identified;
operator authorization remains a prerequisite for product import/use. No upstream source, screenshots,
generated code, or mapping rules are vendored. Numeric wrapper version 122010400 is retained verbatim by
the parser; the current release-adjudication gate rejects it rather than guessing a release.

## Delivery context

Dependent branch: feat/export-source-facts, based on PR #32 head 714113c3a0482ac276280c979dc5e96ffee29a74.
PR #32 remains open; green CI is not merge authorization. Existing Java generation and approval gates
remain unchanged. No infrastructure or deployment changes belong to this increment.