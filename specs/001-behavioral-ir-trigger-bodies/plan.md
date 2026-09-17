# Implementation Plan

## Design

Extend `FormsTrigger` with an optional `Body`. The parser reads Oracle's two known body encodings after the shared XML loader has rejected DTDs, mixed structural namespaces, and qualified competing attributes.

The normalization adapter emits trigger objects containing `name` and nullable `body`, and increments the IR schema from `1` to `2`. The strict reader validates every trigger field with `JsonDocument`; it does not deserialize untrusted IR into an executable type graph.

## Security Boundaries

- Trigger bodies remain inert strings.
- Direct text nodes are read from `TriggerText`; nested foreign content is not concatenated.
- Foreign-namespace `TriggerText` is structural ambiguity and is rejected.
- Body size is bounded at 200,000 UTF-16 characters per trigger.
- No body is inserted into Java, TypeScript, SQL, prompts, or Markdown by this increment.
- `forms-ir.json` is not browser-previewable because it contains normalized customer PL/SQL. It remains in the owner-scoped run export as a provenance artifact; exporting the run therefore exports this normalized source text.

## Compatibility

IR v1 used string arrays for triggers. IR v2 uses objects. Producer and reader ship together; stale workspace output is rejected by the existing schema-version guard and must be regenerated.

## Verification

- Focused parser, reader, normalization, and executor tests.
- Full solution test suite.
- Independent application-code review followed by QA review.
