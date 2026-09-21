# Implementation Plan

1. Reconcile exact-SHA job evidence for specs 006-008; preserve untracked local agent files.
2. Pin and inspect Summit outside the repository. Record original-byte hashes, literal declarations,
   licensing uncertainty, and unresolved dependencies without importing or editing upstream assets.
3. Add source facts to FormsModule and FormsModuleParser. Use a bounded iterative document-order walk
   with per-parent sibling counters; retain declared data, not inferred Forms semantics.
4. Serialize through the existing SourceNormalizationAdapter; extend FormsIntermediateReader with v3
   validation. Keep the existing Java projections and no-source-preview boundary.
5. Test independent fixtures, property omissions, tampering, limits, and adapter-to-reader round trips.
6. Application specialist implementation hands off to independent GPT-5.6 Sol QA.

## Next increments

- Persist a versioned object/property disposition ledger using existing project authorization and durable
  run storage. Decisions must invalidate on relevant source/policy/generation changes.
- Encode approved React/.NET/PostgreSQL rules separately from declared source facts. Decide transaction,
  validation timing, version-column concurrency, lookup and record-action behavior explicitly.
- Generate a bounded master/detail and lookup slice plus independently derived acceptance cases; execute
  backend/frontend/database tests using existing typed evidence and isolation. Preserve the Java route.
- Add coverage/decision views to the existing wizard, then widen ORDERS only as dependencies and rights allow.

No source runtime comparison or full ORDERS completion follows from element counts or compilation.