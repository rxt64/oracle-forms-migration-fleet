# Oracle Forms 6i Azure installation qualification

Date: 2026-09-23/24 UTC
Current status, 2026-09-28 23:13 UTC: the native Meridian source-lab form is compiled, running in Bastion, and tested against Oracle9i. Create/save, cancellation, quantity-two totals, and zero-quantity rejection were verified. Private redirect rules are now durable. Database startup requires the administrator desktop session; unattended reboot recovery is not qualified. See the final section for current evidence; earlier sections are historical.

## Scope and platform

The authorized target was the already-provisioned VM `vm-ofm-forms6i-j6mrrerz` in `eastus2`, resource group `rg-oracle-forms-migration-fleet-dev-b9f0e875`, subscription `d4394e57-c076-4c92-a870-5de6bf44f255`.

Foundation verification immediately before installer execution observed Windows Server 2022 Datacenter Azure Edition, VM Agent ready, one attached NIC, exactly 15 expected NSG rules, no managed identity, no virtual-network peering, inbound deny-all, and public HTTPS egress through `20.109.102.250`. Those two installer attempts did not change network, identity, peering, database, or other Azure resource configuration. The later Bastion network adjustment is recorded below.

Windows Server 2022 is an experimental host for this legacy release and is not an Oracle-certified Forms 6i platform. Results here are installation qualification evidence, not an Oracle support or licensing claim.

## Media provenance

The VM downloaded the raw image over HTTPS from:

```text
https://archive.org/download/Oracle_Forms_and_Reports_6i_Release_2_Win95-98-NT_2001/OracleFormsAndReports6i-r2-Win9598NT.iso
```

Observed and pinned media evidence:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| Raw 2352-byte-sector image | 764,430,576 | `5F84ED194BB6F84CEDC6AF38F4598733DBCF91E8F1008A9C34327090CDA456F4` |
| Converted 2048-byte-sector ISO | 665,626,624 | `72A706D4E48D7E6F2B734ADE97AF859FED1CA91ADB8629415A785CEA76A406BE` |
| Guest converter source | 1,902 | `2A9AF5E664D0F8FABEA718902BBBFE77F88B057F21621D8920EC574FA40F92DB` |
| `INSTALL\SETUP.EXE` | 41,475 | `6FAFB8D50AF62F8C1588DEBAABCF16DCF97F136470333963E7ADF784D12E5827` |

The VM converted the raw image with `Convert-RawCdImage.ps1`, mounted the converted ISO read-only, and copied the installation media to `C:\OracleForms6iSourceLab\media`. Media preparation status was `MediaPreparedNotInstalled`.

## Installer contract

Read-only inspection of the bundled installer found:

- `INSTALL\NT.RSP`, `WIN32.RSP`, and `WIN95.RSP` response templates;
- response fields `language_content` and `install_settings_content`;
- legacy installer switches `/silent`, `/rspsrc`, `/rspdest`, and `/install` in `INSTALL\OICTRL32.DLL`;
- `USER.STP` documentation stating that it is customizable and can bypass the installer window for a predefined typical or custom installation;
- an empty `USER.LIC` body with no license display or acceptance action.

The bundled typical Forms Developer path defaults the restricted Forms Server testing component to `Yes`. The automated product profile excludes that component. It records `<Unknown Customer>` rather than asserting a licensed organization and does not accept a license checkbox or agreement.

## Attempts

### Attempt 1: GUI discovery

Command: `C:\OracleForms6iSourceLab\media\INSTALL\SETUP.EXE` with no arguments.

Bound: 120 seconds.

Observed: bootstrap exit code `0`; child `ORAINST.EXE` remained active in session 0; no visible top-level window was available to the VM Agent window station.

Action: only the started installer process tree was terminated at timeout.
Installed evidence: no Oracle home, Forms binary, or Oracle registry state observed.

### Attempt 2: legacy silent response

Command: `SETUP.EXE /silent /rspsrc "...\OFM-SILENT.RSP" /rspdest "...\OFM-SILENT-RESULT.RSP" /install`.

Bound: 900 seconds.
Observed: bootstrap exit code `0`; x86 child `ORAINST.EXE` remained active in session 0 until the process tree was terminated at the bound. No visible top-level window, response-result file, Oracle home, Forms binary, Oracle registry state, or system-DLL change was observed.

The complete guest artifact was retrieved in bounded chunks after the original host output truncated to the final 4 KiB. The protected local copy is 14,759 bytes and matches guest SHA-256 `BFF488E584F69E29B1319DB41176575EC1492F25AF52B6DFE30F1F418C71025A`.

### Attempt 3: not launched

No third installer process was launched. A session-0 interaction blocker is plausible, but the evidence does not distinguish it from OS incompatibility or another installer defect and does not establish a specific compatibility-layer fix:

- Microsoft documents that session 0 does not support processes that interact with a user and that a noninteractive LocalSystem service and its child processes cannot display UI.
- Microsoft documents that `WindowsXPSp3` compatibility mode includes `RUNASHIGHEST`, which can introduce an elevation prompt that action Run Command cannot service.
- Creating another desktop within the service's noninteractive window station does not make that station interactive.

Accordingly, neither `__COMPAT_LAYER=WINXPSP3` nor a private session-0 desktop was used. No license prompt was bypassed, no system DLL was changed, Defender was not disabled, and no public RDP path was opened.

### Attempt 3: interactive Bastion desktop — succeeded

The installer was run from the operator's interactive Bastion desktop session rather than through action Run Command. The installer GUI rendered immediately, which the session-0 path had never achieved.

This confirms the root cause of attempts 1 and 2 as **session 0 isolation**, not media corruption, not Windows Server 2022 incompatibility, and not a missing compatibility layer. The legacy `ORAINST` installer requires an interactive window station; the VM Agent cannot supply one.

During file copy the installer requested overwrites of protected Windows system libraries. Each such copy was **skipped**, not forced: `MSVCRT40.DLL`, `OLEPRO32.DLL`, and `CTL3D32.DLL` remain absent from `system32`, and `MFC42.DLL`, `MSVCRT.DLL`, and `ODBCTRAC.DLL` retain their Windows-supplied versions. No protected system DLL was replaced and Defender was not disabled.

Installed binaries, verified read-only through the recovered management channel:

| Binary | Bytes | Original timestamp (UTC) |
|---|---:|---|
| `C:\ORANT\BIN\IFBLD60.EXE` | 1,151,488 | 2000-10-27T15:41:20Z |
| `C:\ORANT\BIN\IFCMP60.EXE` | 9,216 | 2000-10-27T15:41:28Z |
| `C:\ORANT\BIN\IFDBG60.EXE` | 23,040 | 2000-10-27T15:41:34Z |
| `C:\ORANT\BIN\IFRUN60.EXE` | 23,040 | 2000-10-27T12:14:36Z |

Oracle Home is `DEFAULT_HOME` at `C:\orant`. The Start menu contains the `Oracle Forms 6i`, `Oracle Forms 6i Admin`, `Oracle Forms & Reports 6i`, and `Oracle for Windows NT` program groups.

`IFBLD60.EXE` was then launched in the same interactive session. Oracle Forms Builder started, displayed the *Welcome to the Form Builder* dialog, and opened a new module as `MODULE1` with a populated Object Navigator (Triggers, Data Blocks, Canvases, LOVs, Program Units, Record Groups, Windows) and the full menu bar. The IDE responded to keyboard interaction, so this is a running program rather than a painted window.

Screenshots retained locally under `.copilot-artifacts/`: `forms-builder-1.png`, `forms-builder-2.png`, `forms-builder-3.png`.

No Forms application has been authored yet, no module has been compiled to `.FMX`, and Forms has not connected to any Oracle database. Those remain open.

### Installer defect: machine PATH rewritten as `REG_SZ`

After the VM was later deallocated and restarted, every action Run Command failed with `'powershell' is not recognized as an internal or external command`, and the same failure appeared for `reg` inside an interactive shell.

The cause was found in the interactive session: the installer had rewritten
`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment\Path`
with value type **`REG_SZ`** instead of **`REG_EXPAND_SZ`**, while the value still contained `%SystemRoot%` tokens. Those tokens were therefore never expanded, so `%SystemRoot%\system32` was not a real directory and no Windows system executable could be resolved. The fault surfaced only after reboot, because the pre-existing process environment masked it until then.

The value was restored to `REG_EXPAND_SZ` with its entries preserved, including the installer's `C:\orant\bin` and `C:\orant\jdk\bin` additions; `GetValueKind('Path')` now reports `ExpandString`. The guest agent services were restarted so the management channel inherited the corrected environment, and action Run Command then succeeded and independently confirmed the installed binaries.

This is a genuine, reproducible legacy-installer defect that any migration of this estate should expect on a modern Windows host.

## Harness hardening

Before any further attempt, the local harness was changed and its focused regression suite passed:

- canonical requested VM resource ID must equal both the foundation verification result and the Azure-returned VM ID;
- guest, retrieval, and converter scripts are copied to a protected snapshot and hashed before invocation;
- installer slots are durably reserved before launch, prior attempt files are counted, and reservations are only completed after matching evidence has been saved;
- the genuine install bound is 180 seconds;
- raw media, converted media, installer, response file, and intentional `USER.STP` delta are revalidated immediately before execution;
- Run Command emits a compact path/hash summary and the complete guest JSON is retrieved in bounded, hash-verified chunks;
- `VerifyInstallation` is read-only inventory and does not launch discovered binaries;
- process observation uses the supported bounded `Process.WaitForExit(Int32)` API, with started-process cleanup in `finally`, rather than sleep polling.

Validation command:

```powershell
.\infra\source-lab\forms6i\Test-Deploy-Forms6iSourceLab.ps1
```

Result: all source-lab, durable-attempt-budget, host/guest safety, and ACL regression checks passed.

Independent parent-invoked review, routed as `GPT-6 Astra (copilot)`, remains **HOLD for safe retry**. Scope: the repaired host/guest installer harness, diagnostic collector, focused tests, and this installation record. No Azure operations or edits were performed by the reviewer; backend model identity is not independently attested. The review confirmed the earlier repairs but found two remaining gaps:

- Process ancestry is reconstructed from live processes, so an exited intermediate parent can hide surviving descendants. Termination errors are suppressed and successful termination is not verified. The `finally` block is not a guarantee that every descendant has exited.
- The install exception handler records an empty DLL-change list instead of comparing the available snapshots or reporting an unknown result.

These gaps must be resolved and behaviorally tested before another installer launch. Passing the current focused suite does not qualify the harness for retry or establish installation success.

## Diagnostic recovery

A read-only diagnostic collector was submitted as action Run Command at `2026-09-24T04:22:08.493Z`, correlation ID `43b285e7-a3ad-43f9-9f0f-8252a482bafc`. The first version performed an overly broad log inventory. Cancelling the local Azure CLI wait did not cancel the guest action. Azure Activity Log records the operation as accepted without a terminal event; subsequent diagnostic calls correctly returned HTTP `409 Conflict` because only one action Run Command may execute at a time.

Microsoft documents that action Run Command output is limited to 4,096 bytes, only one script can run at once, a running script cannot be cancelled, interactive prompts are unsupported, and the platform timeout is 90 minutes. Under the existing source-lab setup authorization, only the newly created lab VM was restarted, preserving its disks and evidence. A subsequent read-only Run Command succeeded, reported last boot at `2026-09-24T04:40:02Z`, no running `ORAINST`/`SETUP` processes, and `False` for all three expected `C:\ORANT\BIN` Builder/compiler/runtime binary paths. The management channel is recovered; Forms remains uninstalled.

The local collector now limits DLL-name regex matches to 128 characters with a two-second regex timeout and skips text files above 512 KiB. An unbounded binary-string scan was identified as another possible cause of the earlier diagnostic hang; the precise cause was not established.

Protected blocker evidence: `diagnostic-command-blocker-20260924T043440187Z.json`, SHA-256 `CF0352EAF4271C4F31A089925609A5C9EAE055F79EE7EBC3935900EA5B8F3F2A`.

The supported execution path was subsequently established as the interactive Bastion desktop, recorded under attempt 3 above. The Run Command harness remains unsuitable for this installer because it cannot provide an interactive window station, and its two outstanding review findings were therefore never exercised against a further silent attempt.

Next action: author the fictional Forms application as reviewable module source, parse and compile it with the installed Oracle tooling, and then establish database connectivity.

## Bastion connectivity remediation

On 2026-09-24, the shared Bastion Developer session reported that the target was unreachable and required NSG access from `168.63.129.16`. Live inspection confirmed the original 15-rule contract and that the authorized VM's sole NIC owned `10.246.0.4`.

The source-lab Bicep and exact-rule verifier now include `Allow-Bastion-Developer-RDP`: inbound TCP from `168.63.129.16/32` to `10.246.0.4/32`, destination port `3389`, priority `100`. `Deny-All-Inbound` moved to priority `110`. The CLI applied these two rule changes only; all 14 outbound rules remained unchanged. Public-source RDP and SSH remain denied. Verification now fails if the VM's private address changes away from the exact RDP destination.

The focused regression suite passed, including rejection of a public RDP source, a broader destination, and SSH access. Parent-invoked independent review routed as `GPT-6 Astra (copilot)` returned **PASS for the narrow existing-VM network change**, conditional on live IP binding and raw/effective rule verification. This routing label is not independent backend identity attestation and does not lift the installer retry hold.

`Deploy-Forms6iSourceLab.ps1 -Operation Verify` succeeded at `2026-09-24T05:16:48.8148530Z`: 16 configured and 16 expected effective rules, one NIC, zero peerings, VM running, guest agent ready, and observed HTTPS egress `20.109.102.250`.

The user authorized future direct Bastion VM-password authentication as `ofmlabadmin`, without Key Vault. A fresh login was submitted using local credential recovery and clipboard paste; the password field and current clipboard were then cleared without emitting the credential. The resulting session was not shared for inspection, so desktop login success remains unverified. No further installer was launched.

## Installation via interactive session

The two earlier failures were caused by **session 0 isolation**, and this is now demonstrated rather than
inferred. Every Forms 6i executable displays a modal version banner before doing any work. Under action
Run Command that banner has no interactive desktop to draw on, so the process waits forever. The same
binaries run normally once a real desktop exists.

Installation was completed by driving the Azure Bastion desktop session. The installer ran to completion
and reported success. Three protected Windows system files were **skipped rather than overwritten** when
the installer requested to replace them: `MSVCRT40.DLL`, `OLEPRO32.DLL` and `CTL3D32.DLL`. A later
read-only check confirmed `MFC42.DLL`, `MSVCRT.DLL` and `ODBCTRAC.DLL` still carry their Windows
versions, so no protected system DLL was replaced.

Observed installed state, by read-only inventory:

| Item | Observation |
|---|---|
| `C:\ORANT\BIN\ifbld60.EXE` | Forms Builder, launches |
| `C:\ORANT\BIN\ifcmp60.EXE` | Forms Compiler, executes |
| `C:\ORANT\BIN\ifrun60.EXE` | Forms Runtime, present |
| `C:\ORANT\NET80\ADMIN\tnsnames.ora` | Net8 client configuration present |
| SQL*Plus | `SQLPLUS.EXE`, `PLUS80.EXE`, `PLUS80W.EXE` present |

Version evidence read from the products themselves, not from the media:

- Forms Builder About: **Forms [32 Bit] Version 6.0.8.11.3 (Production)**, PL/SQL Version 8.0.6.0.0 (Production).
- Form Compiler banner: **Forms 6.0 (Form Compiler) Version 6.0.8.11.3 (Production)**, Oracle Procedure
  Builder V6.0.8.11.0 Build #449, Oracle CORE Version 4.0.6.0.0 (Production).

Forms Builder opens with a working Object Navigator over the full Forms object tree, which is functional
evidence beyond process start.

## Scriptable module conversion

The compiler's own usage banner documents the relevant switches:

```text
ifcmp60 Module=<formname> Userid=<userid/password> [Parameters]
  Module_Type=FORM   Module type (FORM, MENU, LIBRARY, PECS)
  Logon=YES          Logon to database
  Batch=NO           Don't display messages on the screen
  Script=NO          Write script file
  Parse=NO           Parse script file
```

`Logon=NO` makes conversion work with **no database connection**, which matters because no Oracle
database is currently reachable from this VM. Verified end to end:

```text
ifcmp60 module=UE_SAMP.FMB module_type=form script=yes logon=no batch=yes
```

That converted a 45,056-byte binary `.FMB` into an 80,005-byte `.fmt` text module with a clean exit and
no error output. The text format is Oracle's internal `#ROS Script Version 6.0.5.0.1 - Production` object
stream. `Batch=YES` suppresses the modal version banner, which is why conversion succeeds under Run
Command in session 0 while interactive tools hang there.

### The round trip is lossless

`Parse=YES` was verified against a form dumped from this lab, not assumed:

```text
ifcmp60 module=RT_TEST.fmt   module_type=form parse=yes  logon=no batch=yes   -> RT_TEST.fmb
ifcmp60 module=RT_VERIFY.fmb module_type=form script=yes logon=no batch=yes   -> RT_VERIFY.fmt
```

Re-dumping the rebuilt binary reproduced the original text exactly: 3,946 lines in and 3,946 lines out,
zero differences once the module name and the two embedded timestamps are excluded, and a 36,864-byte
`.fmb` both times. Forms modules can therefore be stored and reviewed as text and compiled back into
genuine binaries with no GUI involved.

### The text format is decodable

The `.fmt` is a self-describing typed property stream. Leading `DESCRIBE` blocks declare the record
layouts, then `DEFINE` records carry the data:

| Record | Meaning |
| --- | --- |
| `FRM50_IDFO` | object header: parent index, object id, name, type, owner, child count |
| `F50B` / `F50N` / `F50T` | boolean, numeric and text property values |
| `F50P` / `F50S` | long and PL/SQL source property values |
| `F50O` | object reference property |

Object types occur as container/child pairs, decoded from a sample module: module `22`, block `8`/`7`,
item `31`/`30`, trigger `69`/`68`, canvas `12`/`11`, window `79`/`78`, graphic `10`/`9`. Property numbers
are likewise stable, for example `211` name, `33` canvas, `372`/`373` position, `139` item type, `5` data
type, `464` trigger PL/SQL text, `361`/`121` width and height.

## Driving Forms Builder without screenshots

Forms Builder is interactive-only, but it can be driven and observed entirely as text. Run Command
executes as `SYSTEM` in session 0 with no desktop; it can nonetheless register a scheduled task whose
principal uses `-LogonType Interactive`, which requires no stored password and executes inside the
logged-on Bastion session, where a desktop does exist. A P/Invoke script running there reports the window
tree, the complete menu bar and any dialog, and issues commands by posting `WM_COMMAND`.

Resolve the interactive account from the owner of `explorer.exe`; `Win32_ComputerSystem.UserName` is
empty for RDP sessions and will falsely report that nobody is logged on.

Verified command identifiers read from the live menu bar:

| Command | Id | Command | Id |
| --- | --- | --- | --- |
| File > New > Form | 104 | Program > Run Form > Client/Server | 168 |
| File > Open | 109 | Program > Compile > All | 172 |
| File > Save | 112 | Tools > Data Block Wizard | 184 |
| File > Save As | 113 | Tools > LOV Wizard | 187 |
| File > Connect | 118 | Tools > Object Navigator | 190 |
| File > Administration > Compile File | 125 | Tools > Property Palette | 191 |

The action channel was proven rather than assumed: posting command `112` to the builder hosting
`MRD_ORDER_ENTRY.fmb` changed the file's SHA-256 from `D0EE5BC868E02FE70947FC853E714F8DA7993F00F2EFCB00CB338B441B40C973`
to `108C0B67B9AA0746B632E46DD885B2A55F539FC9F8D5E2D1DDE0BAA1FD3B082D`.

The Property Palette exposes real Win32 `Edit` and `ComboBox` children, so property values are readable
and writable through window messages. The Object Navigator tree is custom-drawn (`ui60Viewcore_W32`) and
exposes no per-node windows, so form content is verified by dumping `.fmt` instead.

## What is still not done

- **The application is a stub.** `MRD_ORDER_ENTRY.fmb` exists and carries the module name, the title
  `Meridian Order Entry` and `WINDOW1`, but its dump contains no blocks, items or triggers.
- **No FMX has been produced** for the fictional application.
- **No database connectivity.** The VM's NSG permits outbound `443` only, so Oracle SQL*Net port `1521`
  is not reachable, and no Oracle database has been contacted from this VM.
- **Database compatibility is unresolved.** The existing lab database is Oracle AI Database 26ai Free.
  Forms 6i ships Net8 8.0.6, and that client is not supported against a modern server. A
  period-appropriate database is likely required, which is a decision to make before promising a
  querying, stored-procedure-invoking, commit/rollback application.

## Evidence locations

Protected local orchestration evidence:

```text
%LOCALAPPDATA%\OracleFormsMigrationFleet\forms6i-source-lab\evidence
```

Guest evidence:

```text
C:\OracleForms6iSourceLab\evidence
```

Microsoft references:

- [Action Run Command restrictions](https://learn.microsoft.com/azure/virtual-machines/windows/run-command#restrictions)
- [Session 0 isolation](https://learn.microsoft.com/windows/win32/services/service-changes-for-windows-vista)
- [Window station and desktop creation](https://learn.microsoft.com/windows/win32/winstation/window-station-and-desktop-creation)
- [Program Compatibility Assistant modes](https://learn.microsoft.com/windows/compatibility/pca-scenarios-for-windows-8#scenarios)

## Oracle 9i source database recovery, 2026-09-28

This update supersedes earlier database-state observations, not the outstanding
application qualification. These are source-lab operations, not migration evidence.

The existing database VM `vm-ofm-oracle9i-j6mrrerz` (`ofmoradb`, `10.246.0.37`)
was reached through Bastion using its existing saved credential. Verification used
text-only guest command output through the Bastion clipboard; no screenshots or
credentials are included in this record.

The installed home is `C:\oracle\ora90`, Oracle9i Personal Edition for Windows 98,
with listener version `9.0.1.1.1`. The guest is Windows Server 2022; this is not a
certified platform combination. A running `oracle.exe` did not establish database
readiness: SQL*Plus initially connected to an idle instance and returned `ORA-01034`.

After checking the existing initialization file, a normal startup succeeded:

```sql
connect / as sysdba
startup pfile='C:\oracle\ora90\database\initorcl.ora';
select status from v$instance;
```

Use `ORACLE_SID=orcl` and SQL*Plus `/nolog` for this local connection. This SQL*Plus
release does not support `-L`. Startup reported both `Database mounted` and
`Database opened`; the query returned `OPEN`. No database recreation or explicit
recovery command was used. All 11 objects returned for owner `MERIDIAN` were `VALID`,
including `PLACE_ORDER`, four tables, their indexes, and `SEQ_ORDER_ID`. Procedure
execution and Forms connectivity were not verified in this recovery session.

Listener startup still binds TCP `10.246.0.37:1521`, then fails its implicit IPC
endpoint `(PROTOCOL=ipc)(PARTIAL=yes)(QUEUESIZE=1)` with `TNS-12560`, `TNS-00530`,
and Windows error `161`, before releasing the TCP endpoint. Support tracing shows
`ntnlsn` attempting `\\.\pipe\NTUS_17F8_B0E715A3.ORA`, followed by the error. The
parent IPC connection succeeds. An independent 32-bit .NET named-pipe creation
test for that name succeeded with explicit exception handling. No listener or
`lsnrctl` compatibility flags were found in either inspected registry hive, and
the diagnostic process had no inherited `__COMPAT_LAYER` value.

Neither process-scoped `WINXPSP3` compatibility nor a temporary
`DIRECT_HANDOFF_TTC_LISTENER=OFF` setting resolved the failure. The setting name was
found in the installed listener binary before testing it. Both tracing attempts
restored the original `listener.ora` bytes in `finally`; no diagnostic setting was
retained. Guest evidence remains at:

```text
C:\oracle\ora90\network\trace\ofm-listener-20260928-171854.trc
C:\oracle\ora90\network\trace\ofm-listener-20260928-172513.trc
C:\oracle\ora90\network\admin\listener.ora.before-trace-20260928-171854
C:\oracle\ora90\network\admin\listener.ora.before-trace-20260928-172513
```

Remaining blocker: this listener's named-pipe initialization on the current host.
The exact API-level cause is unproven. A separate-home test with licensed Oracle9i
Windows NT/2000 media is a candidate next step, not a verified fix or a support claim
for Windows Server 2022. Preserve the existing database, initialization file,
password file, and Forms installation. Automatic database startup after reboot
and end-to-end Forms connectivity remain unverified.

Parameter reference: [Oracle9i 9.0.1 listener parameters](https://docs.oracle.com/cd/A91202_01/901_doc/network.901/a90155/listener.htm).

## Oracle 9i media acquisition follow-up, 2026-09-28

Oracle Support authentication and account registration were completed by the
operator. Read-only account inspection then showed `Patch Download: No Access`
on both the visible membership and its user group. No privileges were changed.
This is not proof that base-media requests are unavailable or that the operator
has no license; the required entitlement remains unverified.

The authenticated [Oracle FAQ1727](https://support.oracle.com/support/?documentId=FAQ1727&page=sptemplate&sptemplate=km-article),
"Downloading 9i, 10g and 11g, 12.1.0.1 Database Software Media," explicitly covers
Oracle9i. It directs requests for unavailable installation media to a
**non-technical media-request SR**, not a technical SR, and references legacy
Note 1071023.1 for the fulfillment procedure. The FAQ does not guarantee that the
requested Windows release can still be supplied.

A non-sensitive availability inquiry was sent to Oracle's AI Support Assistant.
Its suggested Create Service Request action stalled at `Preparing Service Request`.
At 18:04 UTC the portal reported a CORS/fetch failure for its `svcConsumerChats`
request. No request form, SR number, or successful case-creation confirmation was
observed. Refreshing the separate request-list tab returned `Invalid Url`, so
case creation could not be independently verified. Check for an existing request
before retrying; do not report the inquiry as a filed media-request SR.

Later inspection at approximately 18:55 UTC confirmed that the conversational
creation form eventually loaded with the media-inquiry title. It defaulted to
`Technical Issue` under Oracle Database - Personal Edition; no request was
submitted. The portal displayed a session-expiry warning that remained after
the Reauthenticate action. Session renewal, a non-technical submission, and an
SR number remain unconfirmed. This updates the earlier handoff observation,
not the case-creation or entitlement status.

Reusable request wording:

> Please advise whether Oracle9i Database Release 1 (9.0.1) base installation media
> for Microsoft Windows NT/2000 x86 (32-bit) is still available through an authorized
> Oracle media request. If only Release 2 (9.2.0.1) Windows base media is available,
> please identify the official fulfillment path and required entitlement. We need
> Windows NT/2000 base installation media, not Windows 98 Personal Edition, UNIX
> media, or a patch set requiring an existing installation. This is an availability
> and entitlement inquiry, not a claim that this account is licensed or entitled
> to download the software.

Next prerequisite: complete the non-technical request through Oracle Support, or
provide existing licensed Windows installation media with provenance. The
[Oracle Support contact directory](https://www.oracle.com/support/contact.html)
provides an alternative when portal submission fails. No new media was downloaded,
no installer was launched, and no database or Forms configuration was changed
during this follow-up. The previous `OPEN` database result was not remeasured here.

## Oracle 9i short-PATH retest, 2026-09-28

The operator restored Bastion access. Fresh guest output at 18:47 UTC confirmed
`ofmoradb`, no `tnslsnr` process, a stopped listener service, and no listener on
TCP port 1521. The interactive PATH was 211 characters with no Oracle entries;
`ORACLE_HOME`, `TNS_ADMIN`, and `ORACLE_SID` were unset in that shell. The existing
listener configuration contained TCP only, with no explicit IPC or `PIPENAME`.

At 18:48 UTC a child process ran the absolute executable
`C:\oracle\ora90\bin\lsnrctl.exe start LISTENER` with only its PATH replaced:

```text
C:\oracle\ora90\bin;C:\Windows\System32;C:\Windows
```

The child reproduced TCP bind followed by implicit IPC failure, `TNS-12560`,
`TNS-00530`, and Windows error 161. Exit code was 3; no listener process or TCP
listener remained. The parent PATH was unchanged. The listener configuration's
SHA-256 matched before the test and after final verification:

```text
DEE1325914E0267226BCBD5120EEF2DF322CE9A3B0E4270CEF513A2B3528993A
```

Shortening PATH did not resolve this failure. No running listener or occupied TCP
endpoint was found to support the stale-listener hypothesis, but this does not
exclude every Oracle IPC resource or permission issue. The cited UNIX temporary
socket-directory workaround does not apply to this Windows guest. IPC `KEY` is
documented in the [release-matched protocol reference](https://docs.oracle.com/cd/A91202_01/901_doc/network.901/a90155/protocol.htm);
the current configuration has no `PIPENAME` parameter to replace.

The first database health query at 18:49 UTC connected to an idle instance and
returned `ORA-01034`. It launched an Oracle process in the new RDP session 3;
another Oracle process already existed in session 0. The alert log
`C:\oracle\admin\orcl\bdump\orclALRT.LOG` still ended at the prior successful
OPEN at 17:15 UTC. These observations do not prove why local database access was
lost, or that the session-0 process represented an open database.

A normal startup using the existing `C:\oracle\ora90\database\initorcl.ora`
succeeded at 18:51 UTC without `FORCE`, recreation, or an explicit recovery
command. A separate SQL*Plus connection at 18:52 UTC independently confirmed
`orcl` OPEN, 11 MERIDIAN objects, zero invalid objects, and exit code 0. The alert
log recorded `Completed: ALTER DATABASE OPEN` at 18:51:26.

No listener configuration, machine PATH, registry, credential, or Forms changes
were made in this retest. The listener remains unavailable; Forms connectivity,
automatic startup, and persistence across RDP logoff or reboot remain unverified.
The existing database was preserved and opened, not migrated. Correctly licensed
Windows NT/2000 Oracle9i media remains a candidate for a separate-home test, not
a proven fix or a certification claim for this operating system.

## Oracle 9i IPC API diagnosis, 2026-09-28

The API-level cause of the observed IPC startup failure is now identified. This
supersedes the earlier unproven API hypotheses; it does not establish a working
listener, Forms connectivity, or migration acceptance.

Microsoft's official WinDbg bundle `1.2606.22001.0` was downloaded and extracted
under `C:\OracleLab\Diagnostics\WinDbg-1.2606.22001.0`. The x86 CDB executable
reported a valid Microsoft Authenticode signature and debugger version
`10.0.29617.1000`. No debugger installation, postmortem registration, Oracle
binary patch, or database-process attach was performed. Debugging used the normal
heap (`-hd`). Initial parent/child runs were inconclusive; a direct
`tnslsnr.exe LISTENER` run reproduced the same TCP bind, implicit IPC failure,
Windows error 161, and TCP endpoint release.

The loaded `oranipc9.dll` imports named file-mapping, event, and semaphore APIs;
it does not import `CreateNamedPipe`. A breakpoint at `KERNEL32!CreateFileMappingA`
captured the following actual call from `oranipc9+0x18e8`, returning to
`oranipc9+0x18ee`:

| Argument | Captured value |
| --- | --- |
| `hFile` | `0xFFFFFFFF` (`INVALID_HANDLE_VALUE`, paging-file backed) |
| `lpFileMappingAttributes` | `NULL` |
| `flProtect` | `0x00000004` (`PAGE_READWRITE`) |
| `dwMaximumSizeHigh` | `0` |
| `dwMaximumSizeLow` | `0x434` (1076 bytes) |
| `lpName` | `\\.\PIPE\NTUS_1088_B0F318E1.ORA` |

The name buffer contained the expected ASCII bytes followed by a NUL terminator.
The function returned `EAX=0` (NULL). The current x86 thread's raw last-error
field at `TEB+0x34` was `0xA1` (161) immediately after return. The `!gle` extension
could not resolve the required TEB type and printed an unreliable zero; its
decoded output is not used as evidence. The raw field and the independent
reproduction below establish the error instead.

This is a pipe-style name passed to a **file-mapping API**, not a malformed name
passed to a named-pipe API. Microsoft's
[CreateFileMappingA contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createfilemappinga)
allows NULL security attributes but prohibits backslashes in the mapping name
apart from the supported namespace-prefix convention. Consequently, the name
that succeeded in the earlier `NamedPipeServerStream` test is invalid here.

An independent 32-bit P/Invoke probe used the captured file handle, NULL
attributes, protection, and size. It tested the captured pipe-style name against
a unique plain mapping name and closed every successful mapping handle. Both the
workstation and the Server 2022 guest produced:

| API | Pipe-style name | Plain-name control |
| --- | --- | --- |
| `CreateFileMappingA` | NULL, error 161 | Success |
| `CreateFileMappingW` | NULL, error 161 | Success |

Thus ANSI conversion corruption and NULL security attributes are not needed to
explain this failure. `dwOpenMode` and `dwPipeMode` are not arguments of the actual
failing API. The installed IPC implementation's object-naming convention is
incompatible with this host's file-mapping namespace. Removing configured IPC
addresses cannot fix the observed internal mapping call.

The principal guest evidence is retained at:

```text
C:\OracleLab\Diagnostics\listener-ipc-imports-20260928-192853.log
```

After allowing the diagnostic listener to exit, verification at 19:34 UTC found
zero CDB processes, zero listener processes, the original listener configuration
SHA-256, and a fresh SQL*Plus connection reporting `orcl` OPEN, 11 MERIDIAN
objects, zero invalid objects, and exit code 0. No database restart was needed
during API tracing. The Windows NT Oracle9i build has not been tested; replacing
the legacy IPC implementation using properly licensed installation media remains
the next candidate, not a guarantee of Server 2022 compatibility. No permanent
binary modification or compatibility shim was applied.

## Listener memory workaround and Defender restoration, 2026-09-28

The operator authorized a reversible listener patch experiment, then explicitly
authorized temporarily disabling Defender after command execution was interrupted.
Defender Operational events 1116 and 1117 at 19:50:07 UTC recorded
`Behavior:Win32/SuspClickFix.C` against PowerShell with removal reported successful.
This is evidence of a diagnostic-command interruption, not the cause of Oracle's
independently reproduced file-mapping error 161.

On the Oracle lab VM only, a SYSTEM task to restore real-time monitoring was
registered before the temporary setting change. At 19:52:56 UTC both real-time
and behavior monitoring were confirmed disabled. Protection was restored manually
and confirmed enabled at 20:05:33 UTC and again at 20:12 UTC. The temporary
`OFM-RestoreDefender-20260928-195223` task was then removed. No exclusions,
tamper-protection bypass, or workstation protection changes were made.

### Patch and validation

Changing only the generated name prefix caused an access violation at
`oranipc9+0x183c`: an adjacent `strstr` still searched for `\pipe\`, and the caller
dereferenced its NULL result. That disposable listener was terminated. The
successful variant changes the generator and both parser/reconstruction constants
together, without changing lengths:

| DLL file offset | Loaded RVA | Original | Replacement |
| --- | --- | --- | --- |
| `0x36F0` | `0x50F0` | `\\.\pipe\` | `__._pipe_` |
| `0x370C` | `0x510C` | `\pipe\` | `_pipe_` |
| `0x3714` | `0x5114` | `\\.` | `__.` |

Exactly eight bytes change from `0x5C` to `0x5F`, at file offsets `0x36F0`,
`0x36F1`, `0x36F3`, `0x36F8`, `0x370C`, `0x3711`, `0x3714`, and `0x3715`.
The unique `NTUS_%X_%lX.ORA` suffix and all string terminators are preserved.
Original bytes were checked before memory edits. CDB's MASM evaluator requires
`or` or nested conditions here; an initial `&&` guard was rejected before writing.

At 19:58:06 UTC, TCP `lsnrctl status` succeeded for the consistently patched
listener. At 19:58:49 UTC, a separate SQL*Plus process connected through the
explicit TCP descriptor below using existing OS authentication and verified
`orcl` OPEN, 11 MERIDIAN objects, zero invalid objects, and exit code 0:

```text
(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=10.246.0.37)(PORT=1521))(CONNECT_DATA=(SID=orcl)))
```

The listener log independently recorded `service_register * orcl * 0` and the TCP
SQL*Plus `establish * orcl * 0`. This proves a database connection through the
listener, not just an open TCP socket. No database processes were attached,
patched, restarted, or stopped.

### Staged copy and final state

A guarded binary transformation was validated on a local fixture and applied only
to a separate guest copy at:

```text
C:\OracleLab\Diagnostics\listener-compat-20260928-200122
```

The directory retains the patched `oranipc9.dll`, `oranipc9.dll.original`, an
unchanged copy of `tnslsnr.exe`, and `patch-manifest.json` with hashes and offsets.
The patched DLL SHA-256 is
`A7C0E87DFFEBDBF3EEEB7DD1CC723993D75306761894A444C82C5087020FF95E`.
It is **not an activated or verified standalone installation**. The copied
executable still loaded an unpatched adapter; application-local DLL redirection
and a process-local staged-home layout did not produce a working standalone
listener. No registry change or replacement of the installed Oracle DLL was made.

The proven three-constant memory patch was reapplied to a fresh listener with
Defender enabled. CDB then used `qd` to detach and exit, leaving the listener
running. At 20:12:00 UTC, final verification found:

- Listener PID 2044 listening on `10.246.0.37:1521`; zero CDB processes.
- A fresh TCP SQL*Plus connection succeeded: `orcl` OPEN, 11 MERIDIAN objects,
  zero invalid objects, exit code 0.
- Defender real-time and behavior monitoring enabled; restore-task count zero.
- Installed `oranipc9.dll`, `tnslsnr.exe`, and listener configuration hashes
  unchanged from the pre-experiment baseline.

| Installed file | SHA-256 |
| --- | --- |
| `oranipc9.dll` | `5BF3C33D498A3ABB07206A1210614A1921EBE8DCC6876038E8D60D60EEE5ADDF` |
| `tnslsnr.exe` | `81A1E190586B8D0E1B2A6CD042FA76EB198DD5D3FA77F66DFB3815098AAFA5C6` |
| `listener.ora` | `DEE1325914E0267226BCBD5120EEF2DF322CE9A3B0E4270CEF513A2B3528993A` |

Evidence includes `listener-memory-consistent-20260928-195733.log` in the guest
diagnostics directory and `memory-detach.log` in the staged directory. The active
patch exists only in listener memory and is lost when that process exits.
Stopping the experimental listener removes the active workaround; the installed
binaries require no restoration. A persistent activation needs a separately
approved deployment choice. Forms application connectivity, logoff/reboot
persistence, and migration acceptance remain unverified.

## Persistent patch activation and Forms tests, 2026-09-28

The operator explicitly authorized permanent activation and Forms testing. At
20:17:40 UTC, the verified staged DLL replaced the installed
`C:\oracle\ora90\bin\oranipc9.dll` using `File.Replace`, after stopping only the
experimental listener. The replacement preserved an original rollback copy:

```text
C:\oracle\ora90\bin\oranipc9.dll.ofm-original-20260928-201740
```

The installed SHA-256 is
`A7C0E87DFFEBDBF3EEEB7DD1CC723993D75306761894A444C82C5087020FF95E`;
the rollback copy is
`5BF3C33D498A3ABB07206A1210614A1921EBE8DCC6876038E8D60D60EEE5ADDF`.
The eight changed offsets and guarded transformation are recorded above and in
the retained staging manifest. A new, non-debugged listener process (PID 2744)
started from the installed Oracle home and listened on TCP 1521. A fresh TCP
SQL*Plus connection verified `orcl` OPEN, 11 MERIDIAN objects, zero invalid
objects, and exit code 0. No database process was restarted or patched in memory.

This is now an on-disk patch, not the earlier volatile memory workaround. It is
still an unsupported lab modification, not an Oracle certification or vendor fix.
Automatic listener/database startup and reboot behavior were not tested. To roll
back, stop the listener and any process holding the adapter, restore the original
backup to `oranipc9.dll`, and verify the original hash before restarting. Restoring
the original binary also restores its known IPC incompatibility on this host.

### Forms-side results

Forms binaries are not installed on the database VM. The existing Forms VM
`vm-ofm-forms6i-j6mrrerz` (`ofmforms6i`, `10.246.0.4`) was started from its
deallocated state. Its VM agent and Defender extension were verified ready before
guest diagnostics. No Forms reinstallation or system-library replacement occurred.

Net8 8.0.6 could reach the listener at `10.246.0.37:1521`, but Oracle redirected
connections to dynamic server ports. Captured client states included `SYN_SENT`
to ports 56934 and 57011. Both NSGs and Windows Firewall blocked those redirects.
Single-port exceptions were insufficient because each new connection used a new
port. For the bounded test, temporary TCP 49152-65535 exceptions were restricted
to source `10.246.0.4/32` and destination `10.246.0.37/32`; the guest rule also
restricted the program to `C:\oracle\ora90\bin\oracle.exe`. No Internet access
was opened. All three test rules were removed and their absence verified afterward.

The existing protected MERIDIAN lab credential was encrypted to a temporary RSA
key on the Forms VM; orchestration requests contained ciphertext and returned
test output was redacted. Decryption occurred only inside the guest test process.
The legacy compiler received its login through a transient process argument.
The private key was DPAPI-protected and access-restricted, then deleted.
A test-local TNS alias avoided the inline-descriptor resolution failure in Net8.
Installed client configuration and original form modules were not modified.

| Test | Result |
| --- | --- |
| Net8 SQL*Plus login from Forms VM, 20:47:57 UTC | Connected as MERIDIAN; queried CUSTOMERS row count of 3; exit 0 |
| Copy of `MRD_ORDER_ENTRY.fmb` | Compiler exit 3: `FRM-30173: Module contains no canvases` and `FRM-30085`; no FMX produced |
| Copy of installed `UE_SAMP.FMB`, 20:50:26 UTC | Database-logon-enabled compiler exit 0; no compilation errors; FMX produced |
| Original application and sample files | Hashes unchanged by their respective compiler tests |
| Interactive Forms runtime | Not tested: no logged-on desktop session; application stub is not runnable |

Non-secret compiler evidence remains on the Forms VM in:

```text
C:\OracleForms6iSourceLab\evidence\connection-test-20260928
```

`OFM_SAMPLE_TEST.fmx` is 11,168 bytes, SHA-256
`5FA1A2F230D941A635F0798A9D80A21F6CE8048F7531C57E5E1E840466B1B37C`.
The copied FMB files and redacted compiler error reports are retained. The
temporary credential key and test processes were confirmed absent. Defender
real-time and behavior monitoring were enabled on both VMs; no protection changes
were made during this activation/test session.

Final database checks confirmed the installed patched hash, original rollback
hash, listener PID 2744 on TCP 1521, zero CDB processes, and `orcl` OPEN with
11 MERIDIAN objects and zero invalids. The Forms VM remains running for review.
**Ongoing cross-VM Forms access still needs an approved durable redirect-network
solution:** removing the temporary rules restores the original restrictive policy.
Successful sample compilation is not successful Meridian application runtime or
GUI migration acceptance; the missing application canvas/blocks remain separate work.

## Native Meridian runtime verified, 2026-09-28

This section supersedes the earlier stub-only, temporary-network, and runtime-not-tested status.
It records **source-lab application construction and runtime qualification**, not execution or
acceptance of a customer migration through the product GUI. The original stub remains unchanged.
The new native Forms 6i implementation uses the actual existing `CUSTOMERS`, `PRODUCTS`,
`ORDERS`, `ORDER_ITEMS`, and `PLACE_ORDER` objects, not the modern synthetic MRD_* fixture.

### Database and network recovery

The installed IPC patch survived the database VM restart; the startup script verifies its
previously recorded SHA-256 before opening the database. No database was recreated and no
`STARTUP FORCE`, credential rotation, or recovery operation was used.

After reboot, a session-0 listener and a database opened in desktop session 2 produced
`ORA-01034`/`ORA-27101` from Forms. Starting through a SYSTEM task failed with `ORA-01031`;
a local-administrator S4U task failed with `ORA-24314`. Those attempts were superseded by
the interactive administrator task, not retained as successful startup paths.

`Start-Oracle9iSourceLab.ps1 -Action Install` now installs `OFM-StartOracle9i` with an
`ofmlabadmin` logon trigger and interactive, elevated principal. The observed run completed
with task result 0: normal startup mounted and opened `orcl`, a fresh local SQL session
returned `OPEN`, and the validated Oracle listener was moved into the same desktop session.
At 22:36 UTC, active database PID 832 and listener PID 2724 were both in session 2;
the separate idle session-0 Oracle process was not killed.

Durable private rules, also represented in the source-lab deployment contracts:

| Location | Rule | Scope |
| --- | --- | --- |
| Forms NSG | `Allow-Oracle9i-Redirect-Outbound`, priority 226 | TCP 49152-65535, 10.246.0.4/32 to 10.246.0.37/32 |
| Database NSG | `Allow-Forms6i-Redirect-Inbound`, priority 106 | Same source, destination, and port range |
| Database guest firewall | `OFM-Forms6i-Oracle9i-Redirect` | Same private endpoints and TCP range; restricted to `C:\oracle\ora90\bin\oracle.exe` |

Existing TCP 1521 access remains. No public RDP or public database access was added.
The Forms deployment regression suite passed, including rejection of broadened redirect
sources, destinations, and ports. No Defender changes were made during this runtime phase.

### Native application and tests

`Build-MeridianForms6i.ps1` uses the installed x86 Forms Open API to create a separate FMB,
then the installed Forms compiler with the protected MERIDIAN login. Lists need an initial
design-time element even though runtime `POPULATE_LIST` replaces it with database values.
All six triggers compiled with no errors; final compiler exit was 0.

| Native runtime test | Observed result |
| --- | --- |
| Startup | `ifrun60.exe` loaded Meridian Order Entry in desktop session 2; customer `Contoso Manufacturing` and product `MRD-1001 - Industrial Bearing 40mm` were populated from Oracle |
| Create Draft then Save | Independent Net8 session read order 1001, customer 2, product 1, quantity 1, unit price and total 84.50 |
| Create Draft then Cancel Draft | Independent session still read only order 1001; cancelled order 1002 remained absent when the next successful order was 1003 |
| Quantity 0, entered through Bastion, then Create and Save | Native edit control contained `0`; independent session found no additional order or item |
| Quantity 2, entered through Bastion, then Create and Save | Independent session read order 1003 with quantity 2, unit price 84.50, and total 169.00 |
| Normal close | Alt+F4 closed the saved runtime cleanly; no `ifrun60` process remained before final FMX replacement |
| Final layout-only rebuild and reopen | Compiled exit 0; maximized automatically, populated real choices, and child window 976x559 fit inside the 1248x563 MDI client |

Functional transaction checks preceded the final layout-only rebuild. The final module was
then reopened and independently inspected; no transaction-trigger logic changed in that rebuild.
No screenshots were taken for this phase. The lab retains the two deliberately saved test
orders, 1001 and 1003; no cleanup deleted database rows.

Final artifacts on the Forms VM:

```text
C:\OracleForms6iSourceLab\app\generated\MRD_ORDER_ENTRY.fmb
C:\OracleForms6iSourceLab\app\generated\MRD_ORDER_ENTRY.fmx
C:\OracleForms6iSourceLab\app\generated\MRD_ORDER_ENTRY.err
C:\OracleForms6iSourceLab\evidence\runtime\controls*.json
C:\OracleForms6iSourceLab\evidence\runtime\net8-*.txt
```

Final FMX: 23,476 bytes, SHA-256
`605497A31B104FC7D1B99D9C7585169821F9E6452B1847C3109A883A4EB7620F`.
Final independent Net8 evidence at 23:13:01 UTC returned exit 0 with three customers and
the two saved orders above. Runtime inspection at 23:13:47 UTC had no error and confirmed
the final window bounds and populated choices. These files contain no login password.

### Operation and limits

- On the Forms VM, use the public desktop **Meridian Order Entry** shortcut. It invokes
  `C:\OracleForms6iSourceLab\connection\Start-MeridianForms6i.ps1`; no password is embedded
  in the shortcut or script. `-Action LaunchDesktop` also launches through the existing
  interactive session for agent-driven operation.
- The existing MERIDIAN credential is DPAPI LocalMachine-protected in `connection\meridian.dpapi`.
  That directory has inheritance disabled, SYSTEM/Administrators full control, and
  `ofmlabadmin` read/execute. The temporary RSA transfer key was deleted. The legacy runtime
  necessarily receives the decrypted login in its transient process command line; do not
  capture process arguments or treat DPAPI as protection from local administrators.
- Database startup code and transcript are under `C:\OracleLab\Operations` on the database VM.
  After a reboot, sign in to that VM as `ofmlabadmin`; the logon task is configured to open
  the database and align the listener. Its manual invocation succeeded, but a new full
  reboot/logon cycle has not been qualified.
- Keep the database administrator session signed in. Closing the Bastion browser tab
  disconnects it; Windows sign-out is not a qualified operating mode for this Win98 Oracle build.
- Repeated Azure deallocations were observed earlier, but their cause was not established.
  No management locks, governance exemptions, or policy bypasses were applied.
- Close the Forms runtime before rebuilding its FMX: an open runtime locks that file.
  Generated FMB backups are retained; the original application stub is not overwritten.
- Windows Server 2022 remains an experimental, uncertified host for these legacy binaries.
  This outcome does not establish unattended-service reliability, migration acceptance,
  or product GUI conversion/deployment success.