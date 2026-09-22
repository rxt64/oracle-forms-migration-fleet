# Native source prerequisites and source-lab readiness

Evidence date: 2026-09-22 UTC. Scope: subscription
`d4394e57-c076-4c92-a870-5de6bf44f255`, resource group
`rg-oracle-forms-migration-fleet-dev-b9f0e875`, and known source paths in this repository only.
No Azure resource was changed, no container shell was opened, and no secret was read or supplied.

## Outcomes

These outcomes are independent and neither is complete.

### 1. Export/fixture source lab

**Current state: Oracle connectivity is verified; the requested order-entry fixture is not installed or
verified.** The deployed database is the repository's synthetic banking estate. It can support later
product-path work, but it is not native Forms evidence and it is not the requested original fictional
order-entry source.

### 2. Native Forms 6i qualification

**Current state: required external components are missing.** No genuine Forms module was found in the
bounded workspace inventory, and no authorized Forms 6i installation, matching Open API libraries,
verified 32-bit Oracle client/database tuple, or executable Forms baseline was provided. The existing
ASP.NET application is not Oracle Forms. Native extraction and source/target differential testing have
therefore not run.

## Native prerequisite manifest

`6.0.8.22.1` is the exact candidate for the first NDAPI experiment because NDAPI 13.1.0 documents that
tested tuple as Windows x86. Oracle's current upgrade guide discusses the Forms 6i family but does not
certify that NDAPI tuple or direct modern JDAPI access to a 6i binary. The installed Oracle binaries and
authorized media records must independently establish the exact patch.

| Missing component | Exact version or constraint | Evidence and authority | Supplied by | Validation command or check | Impact while absent |
|---|---|---|---|---|---|
| Authorized Oracle Forms Developer/Server 6i media and installed Oracle home | Exact installed Forms build `6.0.8.22.1`; 32-bit Windows binaries | NDAPI 13.1.0 lists `6.0.8.22.1` as its tested 32-bit Forms 6i binding. Oracle's current upgrade guide requires Forms 6i Builder or Compiler for 6i FMT/MMT conversion, but does not certify this NDAPI patch tuple. | Customer or operator with rights to use the media; no partnership requirement | Hash only the supplied media with `Get-FileHash -Algorithm SHA256 -LiteralPath <authorized-media-file>`. After installation, record Windows file `ProductVersion` and `FileVersion` for the operator-identified Builder, Compiler, and Runtime executables under the configured Oracle home. All must identify the intended 6i patch; retain media identity, hashes, install log, and Oracle-provided readme or support record. | The worker must continue to refuse native extraction; no Forms 6i compile or runtime baseline can be claimed. |
| Forms 6i Open API native library set | Libraries from the same `6.0.8.22.1` 32-bit Oracle home; NDAPI's x86 binding resolves `frmd2f` to `ifd2f60` | NDAPI source/README documents the x86 binding. The libraries are Oracle components and are not supplied by the MIT wrapper or this repository. | Same authorized Oracle installation as above | In the configured Oracle home only, run `Get-Item -LiteralPath <approved-ifd2f60.dll> | Select-Object FullName,Length,@{n='FileVersion';e={$_.VersionInfo.FileVersion}},@{n='ProductVersion';e={$_.VersionInfo.ProductVersion}}` and `Get-FileHash -Algorithm SHA256 -LiteralPath <approved-ifd2f60.dll>`. Then run the pinned x86 worker's allowlisted probe/open test against a disposable copy of the genuine module and retain exit code plus structured diagnostics. | NDAPI/Open API calls cannot be loaded or trusted. A managed worker build alone proves nothing about native extraction. |
| Isolated Windows worker host capable of an x86 process | Windows host; worker process architecture must report `X86`; one Forms release per process | The implemented worker contract and NDAPI's matrix require Windows/x86 for this candidate. No Oracle source establishes Linux support for Forms 6i. | Operator-managed isolated lab host after the media and module are available | Run the hash-pinned worker `OracleFormsMigrationFleet.SourceWorker.exe --probe` with the versioned JSON request and require `processArchitecture: X86`, expected release `6.0.8.22.1`, and exact protocol validation. Before native enablement, its expected exit is `2` with typed missing prerequisites. | No native provider may be enabled. Do not provision an empty VM merely to satisfy the host row. |
| Genuine owned Forms source and dependency closure | At least one editable 6i `FMB` plus its required `MMB`, `PLL`, `OLB`, Reports files, assets, and source database objects; matching `FMX`/`MMX`/`PLX` retained only for baseline evidence | Oracle requires source modules to be opened, saved, and compiled for upgrade. Compiled runtime artifacts are not a substitute for editable source. The bounded repository search found none of these module types. | Customer/operator, or a new original fictional fixture built with the authorized toolchain | Run `Get-FileHash -Algorithm SHA256 -LiteralPath <module>` for every supplied artifact; produce a dependency manifest; open a disposable copy with the pinned native worker; compile with the installed Oracle compiler; require zero unresolved module/library references. | There is no native input to extract and no executable source behavior to compare. Synthetic XML must not be relabeled as native extraction. |
| Forms 6i Builder/Compiler and Runtime baseline | Builder, Compiler, and Runtime from the exact installed 6i patch used for the pilot | Oracle documents using the 6i Builder or Compiler to turn 6i FMT/MMT into 6i FMB/MMB. A successful compile is necessary but does not prove runtime behavior. | Same authorized 6i installation | Compile the owned fixture from a clean source copy with the vendor-provided compiler, retain the exact command and complete log, and require a generated runtime artifact. Launch that artifact with the Forms runtime and execute the approved order-entry baseline, retaining screenshots/event evidence and database before/after assertions. | Native extraction could at most recover design-time facts; source behavior and differential equivalence remain unverified. |
| Oracle Net client and database combination for the native runtime | 32-bit Oracle client usable by the 6i process, with a database server/version combination supported by the governing Oracle documentation for that client and Forms installation | The current ASP.NET replica uses managed ODP.NET and reaches Oracle Database Free 23. That does not establish that a legacy 32-bit Forms client can connect to Oracle Free 23, and no such compatibility evidence was found. | Customer/operator supplies the authorized client and the release-specific Oracle certification/readme evidence; the existing database may be used only after this tuple passes | From the isolated host, use the configured 32-bit SQL*Plus without placing a password in the command or log. After secure interactive authentication, execute `SELECT 1 FROM DUAL;`, `SELECT SYS_CONTEXT('USERENV','DB_NAME') AS DB_NAME, SYS_CONTEXT('USERENV','SERVICE_NAME') AS SERVICE_NAME FROM DUAL;`, and `SELECT BANNER_FULL FROM V$VERSION WHERE BANNER_FULL LIKE 'Oracle Database%';`. Then require the compiled form to query and commit/roll back against the same service. | The current database cannot be selected as the native Forms baseline database. Connectivity from ASP.NET is not transferable evidence. |
| Forms 10g bridge toolchain | Oracle Forms `10.1.2.x` Builder/Compiler when the module requires or follows the recommended bridge; exact patch is not established and must be fixed from the supplied authorized media before use | Oracle's current 14.1.2 upgrade guide recommends opening and saving older modules in `10.1.2.x` in most cases; `FRM-18130` makes the bridge mandatory when raised. | Customer/operator with authorized 10.1.2 media | On a disposable copy, upgrade dependencies in `.olb`, `.pll`, `.mmb`, `.fmb` order; retain exact commands and logs. Require successful save/recompile and no unresolved dependencies before proceeding. | A 6i module that cannot be opened directly by the selected current toolchain cannot be normalized. |
| Current Oracle Forms normalization/export toolchain | Oracle Forms `14.1.2` Builder/Compiler and the corresponding Migration Assistant/Forms XML export tooling for this documented path | Oracle documents open/save/compile and Migration Assistant as the supported upgrade path. The current public guide used here is release 14.1.2. It does not prove that current JDAPI directly opens unbridged 6i modules. | Customer/operator with authorized 14.1.2 installation | Record tool versions and hashes, run only after the 6i/10.1.2 steps, retain Migration Assistant and compiler logs, export text, hash all outputs, and compare module/dependency counts to the original manifest. | No provenance-bearing normalized text exists for the fleet. Do not use modern JDAPI directly on 6i unless a separate, reproducible compatibility test proves that exact path. |
| Native extraction provider binding and allowlist | Pinned NDAPI package/source revision plus a binding proven against the supplied `6.0.8.22.1` libraries; x86 self-contained worker | The repository currently ships a refusal-only worker with no NDAPI or Oracle reference. NDAPI is mutable by design and exposes save, compile, creation, and database-connect methods. | Product implementation after the external tuple above is available | Add only the required package/binding to the separate worker; deny database connect and mutating APIs; run one process per module; compare extracted identities, properties, trigger/program-unit text, and dependency counts against an independent Oracle export and observed runtime. | The production worker cannot open a module. Enabling NDAPI in the web process or treating its README as extraction evidence is unacceptable. |

### Toolchain tuple disposition

| Purpose | Candidate tuple | Disposition |
|---|---|---|
| Read-only design-time extraction | Windows x86 process + Forms `6.0.8.22.1` Open API libraries + pinned NDAPI binding + self-contained x86 worker; database connection disabled | Candidate only. The wrapper documents this exact tuple, but the Oracle media, libraries, and genuine module are not present and no native call has run. |
| Compile and executable source baseline | Forms 6i Builder/Compiler/Runtime from the same exact patch + genuine dependency-complete source + 32-bit Oracle Net client + database server tuple supported by the supplied Oracle documentation | Not established. Neither the client version nor a supported server version has been supplied. Oracle Free 23 must not be assumed compatible. |
| Oracle-supported normalization | Forms 6i source copy -> Forms `10.1.2.x` bridge where recommended/required -> Forms `14.1.2` open/save/compile/Migration Assistant/export | Documented route, not executed. The exact 10.1.2 patch and both authorized installations are missing. |
| Direct modern JDAPI against 6i | Current JDAPI/current Oracle home opening an unbridged 6i binary | Not authorized as a plan. No release-specific evidence found establishes this combination; use the documented bridge or prove a separate exact tuple experimentally. |

### OS and architecture disposition

- **Qualified candidate:** a Windows process running x86 against an authorized Forms
  `6.0.8.22.1` Oracle home. This is the only configuration documented by NDAPI for its 6i binding.
- **Potential lab host, not a support claim:** x86 process execution under WOW64 on an isolated x64
  Windows host. The installer, every native dependency, Builder/Compiler, Runtime, and the genuine
  module must pass on that host before it is called viable. No such test has run.
- **Historical 32-bit Windows guest:** usable only where the supplied Oracle media's own platform
  certification and the organization's security controls permit it. It need not be in Azure.
- **Not selected:** Linux for Forms 6i, a Linux container, or a 64-bit worker. NDAPI documents Linux
  x64 only for listed 12c/14c releases, not for 6i.
- **No host should be provisioned yet.** Media, module, library, and client/database evidence must be
  present first so a host is not created empty.

## Source-lab evidence

### Azure control plane and images

Read-only observations at 2026-09-22 UTC:

| Resource | Observed state | Image evidence | Meaning |
|---|---|---|---|
| `ca-ofmfleet-db-dev-ykbpnrpd` | `Succeeded`, `Running`; revision `--0000001` active, `Healthy`, `RunningAtMaxScale`, one replica | `acrofmfleedevykbpnrpd.azurecr.io/oracle-forms-legacy-db:v2`; ACR digest `sha256:240986d8ddf1ddad722a5d5d0692af5075923c893cf6f57f353d9b6a5bdfcafd` | Internal-only TCP 1521 database container is running. This is not a Forms runtime and not by itself a data-correctness result. |
| `ca-ofmfleet-forms-dev-ykbpnrpd` | `Succeeded`, `Running`; revision `--0000004` active, `Healthy`, `RunningAtMaxScale`, one replica | `acrofmfleedevykbpnrpd.azurecr.io/oracle-forms-demo:v5`; ACR digest `sha256:d1da53d89d5a9ae6414411fe0952167de7814763fc8de8f1da2969cea3e552f5` | Public ASP.NET Core workflow replica. It is not Oracle Forms, Forms Services, Builder, Runtime, or a native source host. |

Azure Resource Health returned no per-Container-App record in this scope. AppLens reported Container
Apps unsupported by that diagnostic command. Those are tool limitations, not healthy or unhealthy
findings; revision state and application endpoints provide the evidence above.

### Database connectivity check

The existing public replica was called without credentials:

```text
2026-09-22T03:09:55.7778247Z GET /healthz    -> 200 {"status":"ok","database":"not-checked"}
2026-09-22T03:09:56.2208513Z GET /api/health -> 200 {"status":"ok","database":"available"}
```

The exact database query behind `/api/health` is:

```sql
SELECT 1 FROM DUAL
```

The implementation opens a new Oracle connection, executes that scalar query, and returns available
only when the value is `1`. This verifies DNS/network/listener/authentication/session/query reachability
from the ASP.NET replica at the observation time. `/healthz` explicitly does not access Oracle.

### Data and fixture readiness

- The deployed `v2` image is built from Oracle Database Free 23 and repository init scripts for the
  synthetic **banking** estate. It is database-only; its Dockerfile contains no Oracle Forms software.
- The repository's initialization verifier expects 8 account requests, 5 accounts, 2 staff users,
  12 transactions, zero invalid objects, balance 3300 for account 500001, and successful customer and
  manager login checks before printing `LEGACY ESTATE: SEED OK`.
- The current 300-line console tail contained no `LEGACY ESTATE`, `SEED OK`, `SEED INCOMPLETE`,
  `ORA-`, `PLS-`, or `SP2-` lines. The startup verifier is encoded in the immutable image, but its
  historical successful output was not present in the retained tail. Therefore current seed contents
  were **not reverified in this inspection**.
- No table/count/business-rule query was exposed by the no-secret health endpoint. The HTTP 200 must
  not be promoted to banking data correctness.
- The bounded repository search found no native `FMB`, `FMX`, `MMB`, `MMX`, `PLL`, `PLX`, or `OLB`
  artifact. No authorized external source path was supplied, and no other user directory was scanned.
- The requested original fictional order-entry fixture is not present in the deployed image or known
  source paths. Native runtime execution, native extraction, and source/target differential tests did
  not occur.

## Exact read-only commands used

The two app names were substituted into these commands. Queries return identifiers and state only;
they do not return application settings, registry credentials, or secrets.

```powershell
az containerapp show --subscription d4394e57-c076-4c92-a870-5de6bf44f255 `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 --name <app> `
  --query "{name:name,provisioningState:properties.provisioningState,runningStatus:properties.runningStatus,latestRevisionName:properties.latestRevisionName,latestReadyRevisionName:properties.latestReadyRevisionName,image:properties.template.containers[0].image,fqdn:properties.configuration.ingress.fqdn,external:properties.configuration.ingress.external,transport:properties.configuration.ingress.transport,targetPort:properties.configuration.ingress.targetPort,exposedPort:properties.configuration.ingress.exposedPort}" -o json

az containerapp revision list --subscription d4394e57-c076-4c92-a870-5de6bf44f255 `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 --name <app> `
  --query "[].{name:name,active:properties.active,healthState:properties.healthState,runningState:properties.runningState,replicas:properties.replicas,createdTime:properties.createdTime,image:properties.template.containers[0].image}" -o json

az acr repository show-manifests --subscription d4394e57-c076-4c92-a870-5de6bf44f255 `
  --name acrofmfleedevykbpnrpd --repository <oracle-forms-legacy-db-or-oracle-forms-demo> `
  --detail --query "[].{digest:digest,tags:tags,lastUpdateTime:lastUpdateTime}" -o json

Invoke-WebRequest -UseBasicParsing `
  'https://ca-ofmfleet-forms-dev-ykbpnrpd.jollyground-7a57bcec.eastus2.azurecontainerapps.io/healthz'
Invoke-WebRequest -UseBasicParsing `
  'https://ca-ofmfleet-forms-dev-ykbpnrpd.jollyground-7a57bcec.eastus2.azurecontainerapps.io/api/health'

az containerapp logs show --subscription d4394e57-c076-4c92-a870-5de6bf44f255 `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd --type console --tail 300
```

## Next fixture setup path

Do not provision a native VM yet. The next bounded implementation is an owned export/fixture slice:

1. Add original order-entry SQL under `infra/legacy-estate/oracle/initdb/` in new ordered scripts for
   customers, products, order headers, order lines, stock, and an explicit order transaction routine.
   Include invalid quantity, missing customer/product, insufficient stock, decimal total, concurrent
   stock change, and full rollback cases.
2. Add a final SQL verifier that fails initialization unless exact row counts, object validity,
   transaction behavior, rollback behavior, and expected error paths pass. Do not reuse the generated
   target implementation as the oracle for those expectations.
3. Add an independently authored textual source package beside the existing estate fixtures, clearly
   labeled `SyntheticExport`; include source hashes and dependency coverage. Do not create or imitate
   an FMB.
4. Build and release a new immutable database image through the trusted repository workflow, then
   deploy it only under a separate approval. Record the new tag, digest, revision, verifier output,
   and the exact no-secret health/query evidence.
5. Keep the native path separate. Once every external row in the manifest is supplied, validate the
   tuple on an isolated Windows x86-capable host, build the original fictional order form with the real
   6i Builder, compile and execute it against the verified database tuple, and only then enable the
   pinned native provider.

## References

Oracle authority:

- [Preparing to Upgrade](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/preparing-upgrade.html)
- [Convert Forms 6i FMT/MMT files](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/steps-convert-forms-6i-fmts-latest-oracle-forms-fmbs.html)
- [Upgrade from pre-Forms 6i](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/upgrade-forms-6i-applications.html)
- [Client/server and Forms runtime changes](https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/changes-client-server-deployment-and-forms-runtime.html)

Implementation-specific authority and local evidence:

- [NDAPI README and tested version matrix](https://github.com/felipebz/ndapi/blob/main/README.md)
- [NDAPI 13.1.0 package](https://www.nuget.org/packages/Ndapi/13.1.0)
- [NDAPI and .NET feasibility](NDAPI_DOTNET_FEASIBILITY.md)
- [Oracle Forms 6i research](ORACLE_FORMS_6I_RESEARCH.md)
- [Source environment probe evidence](../specs/007-source-environment-profile-and-forms-compatibility-probe/verification.md)
- [Native worker contract evidence](../specs/008-native-source-worker-contract/verification.md)