# NDAPI and .NET feasibility

Status: feasible architecture and candidate implementation path, not a completed native integration
or a .NET target compatibility claim.

## Decision

[felipebz/ndapi](https://github.com/felipebz/ndapi) is a viable optional source-extraction provider for
the migration fleet. It is not a Forms-to-.NET converter. NDAPI is an MIT-licensed .NET wrapper over
Oracle Forms Open API native libraries; it can open, inspect, create, modify, save, and compile Forms
modules. The fleet should use only an allowlisted read surface in an isolated worker and feed the
resulting intermediate representation into target emitters.

A .NET application target is also feasible, but it is a separate feature: generate ASP.NET Core and
Blazor from the fleet IR, then gate the output with `dotnet test`, `dotnet publish`, PostgreSQL, and
browser workflow tests. NDAPI supplies source facts; it does not generate that target.

## Verified NDAPI facts

Research was performed against repository `felipebz/ndapi` at commit
`bf6f395e25f363b6a87b9ed7f1f98df57a5a6d61` and the NuGet `Ndapi` 13.1.0 page.

| Concern | Verified behavior |
|---|---|
| License | MIT for the NDAPI wrapper. Oracle Forms native libraries remain governed separately by the customer's Oracle agreement. |
| Managed runtime | Stable NuGet 13.1.0 targets .NET 8 or later. Current source multi-targets .NET 8, 9, and 10. |
| Forms 6i | Exact documented build `6.0.8.22.1`, Windows x86. The process must target `x86`. |
| Forms 12c | `12.2.1.3`, `12.2.1.4`, and `12.2.1.19`, Windows or Linux x64. |
| Forms 14c | Current repository documents `14.1.2.0` on Windows/Linux x64; current source is `14.0.0-alpha`. Stable NuGet 13.1.0 should not be treated as 14c support. |
| Unsupported gap | NDAPI does not document direct 9i, 10g, or 11g support. Normalize those releases with compatible Oracle tooling to a supported 12c build, or add and test a version binding before use. |
| Windows native library | x86 resolves the general `frmd2f` imports to `ifd2f60`; x64 loads `frmd2f` through normal native search. |
| Linux native libraries | Requires `ORACLE_HOME`; preloads `$ORACLE_HOME/lib/libfrmjapi.so` globally, then loads `$ORACLE_HOME/lib/libd2f.so`. |
| Module types | Opens FMB, MMB, OLB, and PLL. It also exposes binary-to-text and text-to-binary conversion methods. |
| Mutability | The public model exposes setters plus `Save`, `CompileFile`, `CompileObjects`, creation, attachment, conversion, and database-connect methods. It is not read-only by design. |
| Tests | The repository solution contains no test project. CI builds/packages managed code and NativeAOT-publishes a Linux x64 sample; it does not prove native Forms 6i execution. |

## Source evidence NDAPI can expose

The object model is substantially richer than the fleet's current XML subset. It exposes:

- form, menu, object-library, and PL/SQL-library modules;
- blocks, items, block relations, canvases, windows, tab pages, coordinates, and visual attributes;
- form, block, item, menu, and object-group triggers, including trigger source text and execution
  properties;
- program units and library program units, including source text and unit type;
- menus, menu items, roles, parameters, and startup code;
- LOVs, record groups and columns, editors, alerts, property classes, object groups, reports, and
  attached libraries;
- query/DML target, insert/update/delete permissions, locking mode, key mode, navigation, validation,
  layout, fonts, prompts, formats, and many other Builder properties;
- Builder and module file versions.

NDAPI's source generator memoizes trigger text, program-unit text, and menu-item code. Its comment says
repeated native reads of those properties can cause large unmanaged leaks in Forms 6i libraries such
as `pls805.dll` and `CA60.DLL`. A long-lived multi-tenant process is therefore the wrong host.

## Required worker boundary

Do not add the NDAPI NuGet package to the fleet web process. Use a separate executable selected by
Forms release and architecture.

### Forms 6i worker

- Windows worker with the exact compatible Forms `6.0.8.22.1` installation.
- .NET 8+ executable published for `win-x86` with `PlatformTarget=x86`.
- One process per module, or a very small bounded batch, with memory and wall-clock limits.
- Oracle native library resolution constrained to the approved Oracle home.

### Forms 12c worker

- Windows x64 or Linux x64 worker with a matching supported Forms installation.
- On Linux, explicit `ORACLE_HOME` and a validated `libfrmjapi.so`/`libd2f.so` pair.
- One Oracle Forms release per process. NDAPI keeps its context, native resolver, object registry, and
  module list in static process state.

### Security controls

- read-only source copy mounted into an isolated working directory;
- no target credentials, no database credentials, and denied network egress;
- never call `NdapiContext.ConnectToDatabase`;
- deny or omit code paths for setters, constructors, `Save`, `CompileFile`, `CompileObjects`,
  `ConvertFromText`, attachment, and object creation;
- write only a fleet-owned JSON result and diagnostics to a separate output directory;
- hash every input and output; record NDAPI version, worker hash, Oracle home identifier, Builder
  version, process architecture, OS, duration, peak memory, and every warning;
- kill the worker on timeout, memory budget, native crash, unexpected output, or module/version
  mismatch;
- treat native process exit as untrusted. A managed build badge is not evidence that the required
  Oracle Open API call worked.

## Proposed extraction contract

The host should send a request without secrets:

```json
{
  "schemaVersion": 1,
  "modulePath": "source/forms/ORDER_ENTRY.fmb",
  "expectedFormsRelease": "6.0.8.22.1",
  "moduleKind": "Form",
  "readProfile": "FullDesignTimeMetadata",
  "outputPath": "out/ORDER_ENTRY.ndapi.json"
}
```

The worker result should contain:

- input SHA-256, module kind, NDAPI package version, Builder version, file version, OS, architecture,
  and a success/failure diagnostic;
- stable identities and typed properties for every extracted object;
- trigger and program-unit source with scope and owner path;
- all references to libraries, menus, reports, record groups, database targets, and external code;
- an explicit list of unreadable/unsupported properties rather than omitted fields;
- no passwords, connection strings, environment dump, or raw native addresses.

The fleet then converts this result into the same normalized IR used by textual exports. A complete
module coverage manifest must still gate application generation.

## Can the fleet generate a .NET application?

Yes, independently of NDAPI. The natural target is:

| Layer | Proposed target |
|---|---|
| Web UI | Blazor Web App or Blazor WebAssembly, selected explicitly rather than implied by NDAPI |
| API | ASP.NET Core on the fleet's current .NET 10 baseline |
| Persistence | EF Core/Npgsql for PostgreSQL or EF Core SQL Server provider for Azure SQL |
| Identity | Microsoft Entra ID with ASP.NET Core authentication/authorization |
| Background work | Hosted services or Azure Functions only when source semantics require them |
| Validation | xUnit integration tests, `dotnet test`, `dotnet publish`, PostgreSQL/SQL execution, and Playwright differential workflows |

Required fleet changes before exposing this as a selectable target:

1. Add `DotNetAspNetCore` to the backend target contract and `Blazor` to the frontend target contract,
   without changing existing enum numeric values.
2. Build deterministic emitters for contracts, services, authorization, persistence, UI, and tests.
3. Map the same normalized source semantics to Java/browser and .NET targets; do not fork extraction.
4. Generate a dependency-complete pilot from real authorized source and pass the same acceptance suite
   as the Java/browser target.
5. Report target-specific unsupported constructs and never fall back silently to generic CRUD.

Until those emitters and tests exist, `.NET` is a **feasible target roadmap**, not an implemented output
of the fleet.

## Recommendation

Adopt NDAPI experimentally as an out-of-process extraction provider for its exact documented Forms
builds. Start with a customer-authorized Forms 6i `6.0.8.22.1` Windows x86 fixture and compare NDAPI
output against Forms2XML/JDAPI plus observed runtime behavior. Do not use NDAPI as the only path for the
entire 6i-to-12c range, and do not load Oracle native libraries into the fleet web host.

## Sources

- [felipebz/ndapi](https://github.com/felipebz/ndapi)
- [NDAPI README](https://github.com/felipebz/ndapi/blob/main/README.md)
- [NDAPI MIT license](https://github.com/felipebz/ndapi/blob/main/LICENSE)
- [NuGet Ndapi 13.1.0](https://www.nuget.org/packages/Ndapi/13.1.0)
- [NDAPI native bootstrap](https://github.com/felipebz/ndapi/blob/main/Ndapi/OracleFormsBootstrap.cs)
- [NDAPI context and native resolver](https://github.com/felipebz/ndapi/blob/main/Ndapi/NdapiContext.cs)
- [NDAPI module operations](https://github.com/felipebz/ndapi/blob/main/Ndapi/NdapiModule.cs)
- [NDAPI build workflow](https://github.com/felipebz/ndapi/blob/main/.github/workflows/build.yml)
