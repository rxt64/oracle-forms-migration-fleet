# Coding Agent Instructions

> **Read `.github/copilot-instructions.md` first.** It carries the operator's standing order: the Oracle
> database **and** the Oracle Forms application must both be migrated to Azure components, from the GUI.
> When a phase cannot run, build the missing capability rather than reporting the limit.

This project was built with the microsoft-foundry skill. Before working on or answering questions about foundry agents, read the microsoft-foundry skill first. If you are in VS Code, read the vscode-microsoft-foundry skill first.

This project is a **Microsoft Foundry hosted agent** — a containerized AI agent that runs in [Foundry Agent Service](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents). The platform handles containerization, hosting, security, scaling, and observability so you can focus on agent logic.

It implements the **Oracle Forms Migration Fleet**: a service that plans **and performs** migrations of Oracle Forms applications and their Oracle databases onto Azure components. The deterministic planner authorizes phases; execution adapters carry them out and write artifacts into the operator's session workspace.

## Key files

- `azure.yaml` — azd project and the single `oracle-forms-migration-fleet` service definition
- `src/oracle-forms-migration-fleet/Program.cs` — Responses-protocol host and the outer model-backed agent
- `src/oracle-forms-migration-fleet/Fleet/` — deterministic specialist fleet (contracts, roles, orchestrator, advisor, guardrails, tools)
- `src/oracle-forms-migration-fleet/Dockerfile` — container definition
- `tests/oracle-forms-migration-fleet.Tests/` — offline unit tests; these must never call Azure

## Design rules

- All stage transitions and the platform recommendation live in `Fleet/` and stay pure: no network, no Azure SDK, no model call. The model reaches them only as tools.
- Domain types are immutable records and enums in `Fleet/FleetContracts.cs`. Add new state there, not as mutable fields.
- The human approval gate is the only path to `MigrationPlan.IsAccepted == true`. Do not add a bypass.
- The execution lifecycle in `Fleet/MigrationRunPlanner.cs` stays deterministic too: it authorizes phases, it never runs one. Sandbox mutation needs its own execution approval and production cutover needs its own production approval plus successful attestations. Do not collapse those gates into the assessment plan approval.
- Never claim behaviour was preserved, a sandbox was loaded, or data was reconciled without a matching successful attestation. Conversion and generation phases produce artifacts and deliberately no attestation, so report those by what was written and state what remains unverified: generated code that has never been compiled is not working software, and DDL that has never executed is not a migrated schema.
- Never emit executable SQL from the deterministic core in `Fleet/`, and never claim a migration was performed there. Execution belongs to the adapters in `Fleet/Execution/`, which run only when the planner authorized the phase.
- Never place secrets in prompts, evidence, logs, or output. `FleetGuardrails.ContainsPotentialSecret` rejects obvious credential material at intake.
- Oracle Forms and Oracle Database conversion knowledge comes from `Fleet/OracleMigrationGroundingCatalog.cs`, never from model memory. Release and target filters are applied in code; a query is only search terms. Cite `[GRD-*]` for release/engine claims and run evidence identifiers for estate claims, and keep the two apart. Grounding authorizes nothing: an unmatched query is a blocker, and an uncited high-severity review finding is downgraded to a note. See [docs/AGENT_GROUNDING.md](docs/AGENT_GROUNDING.md).

## Development workflow

The **Azure Developer CLI (`azd`)** manages the full lifecycle:

```bash
azd ai agent run --no-client               # Run locally on http://localhost:8088
azd ai agent invoke --local "your message" # Test the local agent
azd deploy                                 # Deploy to Foundry
azd ai agent invoke "your message"         # Invoke the deployed agent
```

Build and test locally (no Azure access required for the test suite):

```bash
dotnet build
dotnet test
```

## Microsoft Foundry Skill

Install the **Microsoft Foundry Skill** for guided deployment, evaluation, and troubleshooting workflows.

Direct install (preferred, works with any coding agent):

```bash
npx skills add https://github.com/microsoft/azure-skills --skill microsoft-foundry
```

Or install the Azure Skills Plugin:

- **Copilot CLI**: `/plugin marketplace add microsoft/azure-skills` then `/plugin install azure@azure-skills`
- **Claude Code**: `/plugin install azure@claude-plugins-official`

Then ask naturally, e.g. `Use the Microsoft Foundry Skill to deploy this agent.`

## References

- [Hosted agents overview](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents)
- [Microsoft Foundry Skill](https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/use-microsoft-foundry-skill)
- [docs/AGENT_GROUNDING.md](docs/AGENT_GROUNDING.md) — how conversion claims are grounded and cited