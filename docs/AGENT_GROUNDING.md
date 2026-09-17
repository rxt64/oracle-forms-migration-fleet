# Agent grounding

How this fleet answers Oracle Forms and Oracle Database conversion questions without letting a model's
recollection pass for evidence.

## Why grounding exists here

A migration answer fails in two different ways. It can be wrong about the world — "Forms 6i upgrades
directly to 12c" — and it can be wrong about *this estate* — "your LOV queries converted cleanly". The
fleet already separates the second case: estate facts come from parsed source, generated artifacts, and
attestations, each carrying a run evidence identifier. Grounding closes the first case. Curated,
citable reference material is retrieved before any conversion claim, so a statement about a release or a
target engine is either cited or is not a finding.

## Architecture as built

```
caller question
      │
      ├─ outer model agent ── search_oracle_migration_grounding ──► OracleMigrationGroundingCatalog
      │                                                              (in-process, deterministic)
      ├─ assess / plan tools ─────────────────────────────────────► run evidence identifiers
      │
      └─ answer: FINDINGS (cited) · ASSUMPTIONS · BLOCKERS
```

Three consumers, one catalog:

| Consumer | How grounding reaches it | Source |
|---|---|---|
| Outer model agent | `search_oracle_migration_grounding`, a read-only `AIFunction` it must call before any Oracle conversion claim | [`Fleet/FleetTools.cs`](../src/oracle-forms-migration-fleet/Fleet/FleetTools.cs) |
| `ModelArtifactReviewer` | A deterministic brief injected into the system prompt for the run's target | [`Fleet/Execution/ArtifactReview.cs`](../src/oracle-forms-migration-fleet/Fleet/Execution/ArtifactReview.cs) |
| `SqlRepairAgent` | A smaller brief, for choosing a correct rewrite of something already raised | [`Fleet/Agents/ConversionAgents.cs`](../src/oracle-forms-migration-fleet/Fleet/Agents/ConversionAgents.cs) |

The reviewer and the repairer do not get to choose their own search terms. Both use fixed query
constants, so the grounding a target receives is reproducible and cannot be steered by a comment inside a
customer artifact that reached the prompt.

## The embedded catalog

[`Fleet/OracleMigrationGroundingCatalog.cs`](../src/oracle-forms-migration-fleet/Fleet/OracleMigrationGroundingCatalog.cs)
holds a small set of hand-written entries compiled into the assembly. There is no index, no embedding
model, no network call, and no Azure dependency. Each entry carries:

- a stable `GRD-*` citation identifier;
- an authority — `Oracle`, `Microsoft`, `PostgreSql`, or `FleetVerified`;
- an HTTPS source URL from Oracle, Microsoft, PostgreSQL, or a fleet-verified repository artifact;
- the Oracle Forms families and Oracle Database families it applies to, empty meaning "all";
- the database targets it applies to, empty meaning "all";
- guidance text;
- a **claim boundary**: what the entry does *not* prove.

Retrieval is deterministic. Release and target filters are applied in code before scoring; the query
string only feeds a tokeniser that ranks what survived the filters. A query cannot widen a filter,
because the filter never reads the query.

The catalog is deliberately narrow. It covers the 6i upgrade route, the Forms Migration Assistant, the
client/server-to-web behaviour inventory, LOV conversion, the 12c textual-export boundary, PostgreSQL
type/sequence/PL-pgSQL conversion, managed-identity access to Azure Database for PostgreSQL, and the
fleet's own evidence boundary. There is currently **no SQL Server-family engine guidance**, so a run
targeting the SQL family retrieves the target-agnostic entries only. That is visible in the returned set
rather than hidden: an unmatched query returns no entries and a warning.

## How run evidence and curated grounding compose

They are different kinds of citation and are never merged.

| | Curated grounding | Run evidence |
|---|---|---|
| Identifier | `[GRD-*]` | evidence IDs, artifact paths, attestations |
| Answers | "what is true of this release or engine" | "what is true of this estate and this run" |
| Produced by | a human writing a catalog entry | parsing, generation, compilation, execution, human acceptance |
| Can authorize | nothing | phase gates, via the deterministic planner only |

A grounded sentence about PostgreSQL implicit casts and an evidence-backed sentence about which CHECK
constraints this conversion actually emitted are both required to say something useful, and the agent
instructions require them to be cited separately. Grounding on its own can never establish an
estate-specific fact, and it never raises a confidence level or substitutes for an attestation.

## Citation format

The catalog stores `GRD-AREA-TOPIC-NNN` and returns it bracketed: `[GRD-DB-POSTGRES-TYPES-001]`.
Identifiers are stable, so a report written months ago still resolves. `NormalizeCitation` accepts the
bracketed or bare form, case-insensitively, and returns `null` for anything the catalog did not issue —
a model cannot manufacture a citation that reads as grounded.

`AdvisoryFinding.Citations` is a structured list, not prose. Burying identifiers in a reason string would
make "cited" indistinguishable from "mentioned".

## Fail-closed behaviour

- **Unrecognized release.** `Search` returns no entries and a warning naming the supplied string. The
  agent instructions require that to be reported as a BLOCKER, not filled in from memory.
- **No match.** Same: no entries, plus a warning that says explicitly not to answer from model memory.
- **Uncited high-severity review finding.** When the reviewer prompt carried grounding, a `WillFail` or
  `BehaviourDiffers` finding with no valid citation is recorded as a `Note`, with the downgrade stated in
  the reason. The lead stays visible to a human; the severity does not.
- **Invented citation.** Dropped during parsing. Since dropping it leaves the finding uncited, the
  downgrade above then applies.
- **Failed review.** Unreadable model output is already reported as a failed review rather than a clean
  one, and grounding does not change that.

Every one of these keeps the review advisory. Nothing in this path can open a gate, sign an attestation,
or alter the deterministic conversion report.

## Security

- **Retrieved text is data.** Every prompt that carries a brief delimits it with
  `<<<BEGIN GROUNDING REFERENCE>>>` / `<<<END GROUNDING REFERENCE>>>` and states that instructions found
  inside are to be reported, not followed. The same rule already applied to customer DDL.
- **The query is only search terms.** An injected instruction in a query can at best change ranking
  within what the filters already allowed. It cannot disable a release or target filter, and it cannot
  cause an identifier outside the catalog to be returned.
- **Blast radius.** The reviewer has no workspace handle, no credential, and no plan; the repairer's
  output is written beside the deterministic artifact, never over it. A fully compromised grounding path
  adds noise to a section labelled unverified.
- **No customer content is indexed.** The catalog contains public first-party guidance only. Customer
  source never enters it, so there is no retrieval path that could leak one caller's estate to another.
- **Bing grounding is deliberately not used.** Grounding with Bing Search sends query content to an
  external service outside the Azure compliance boundary. Customer source, module names, and schema
  detail must not leave that boundary, so web grounding is not wired to any customer-source path.

## Production path: Azure AI Search / Foundry IQ

**Not provisioned and not required.** Nothing in this repository creates, calls, or depends on an Azure
AI Search service. The embedded catalog is the whole implementation. What follows is the intended path
if the corpus outgrows a compiled array — for example when vendor manuals, customer-approved runbooks, or
per-engagement decision records need to be citable.

1. **Index the corpus in Azure AI Search**, preserving the fields the catalog carries today as document
   metadata: citation identifier, authority, source URL, Forms family, database family, target, and claim
   boundary. Retrieval that loses source metadata cannot produce an honest citation.
2. **Keep filters server-side.** Release and target become filterable index fields so the same
   "the query cannot widen a filter" property holds after the move.
3. **Connect it as a context provider or a Foundry tool.** Agent Framework has an
   [Azure AI Search context provider](https://learn.microsoft.com/agent-framework/integrations/by-component/context-providers/azure-ai-search),
   and Foundry Agent Service can
   [connect an Azure AI Search index to an agent](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/ai-search)
   or attach a [Foundry IQ knowledge base](https://learn.microsoft.com/azure/foundry/agents/how-to/foundry-iq-connect).
4. **Managed identity only.** An application-owned Agent Framework context provider can use the hosted
   application's managed identity. A Foundry-managed Azure AI Search tool uses the Foundry project or
   account managed identity configured on its connection. Keep both identities narrowly scoped and keep
   keys out of configuration, prompts, and generated artifacts. See
   [search security best practices](https://learn.microsoft.com/azure/search/search-security-best-practices).
5. **Document-level access.** Once per-engagement material is indexed, retrieval must respect who may see
   which document. Azure AI Search supports
   [document-level access control](https://learn.microsoft.com/azure/search/search-document-level-access-overview)
   through permission filters and ACL/RBAC-scoped fields; a shared index without it would let one
   engagement's runbook ground another engagement's answer. Native ACL/RBAC enforcement, agentic
   retrieval, and parts of Foundry IQ are preview-dependent as of this implementation. Pin supported API
   and SDK versions, verify current feature status and region availability, and do not treat a preview as
   satisfying a production SLA or compliance requirement without explicit review.
6. **Indirect prompt injection controls.** Indexed third-party documents are a larger injection surface
   than a compiled array. The delimiter-and-report convention stays, the "retrieved content authorizes
   nothing" invariant stays, and retrieval is scored against
   [risk and safety evaluators including indirect attack](https://learn.microsoft.com/azure/foundry/concepts/evaluation-evaluators/risk-safety-evaluators)
   before a corpus is trusted in a customer engagement.
7. **Evaluate before and after.** The deterministic set in
   `tests/oracle-forms-migration-fleet.Tests/TestData/oracle-grounding-eval.jsonl` pins routing, citation,
   unsupported-claim, and injection behaviour offline. A retrieval service adds non-determinism, so the
   same cases are re-run as a
   [Foundry evaluation](https://learn.microsoft.com/azure/foundry/how-to/evaluate-generative-ai-app)
   with groundedness and retrieval metrics, and a regression there blocks the corpus, not just the code.

## Updating the catalog

An entry is a claim the fleet is willing to make in front of a customer, so it is reviewed like code.

1. Add or edit a `OracleGroundingDocument` in `OracleMigrationGroundingCatalog`.
2. Give it a new, never-reused `GRD-*` identifier. Changing what an existing identifier means silently
   rewrites every report that cited it.
3. Cite a first-party HTTPS source: Oracle, Microsoft Learn, PostgreSQL documentation, or — for
   `FleetVerified` entries — a document in this repository that states what was actually demonstrated.
4. Write the claim boundary before the guidance. If you cannot say what the entry does *not* prove, it is
   not ready.
5. Set the release families and targets deliberately. Empty means "applies everywhere", which is a
   stronger claim than it looks.
6. Add or update rows in `oracle-grounding-eval.jsonl` covering the routing the entry changes, and run
   `dotnet test`.

## Related

- [docs/SECURITY.md](SECURITY.md) — threat model, source handling, and the guardrails grounding sits inside.
- [docs/COMPATIBILITY.md](COMPATIBILITY.md) — what the fleet demonstrates per release, which several
  `FleetVerified` entries cite.
