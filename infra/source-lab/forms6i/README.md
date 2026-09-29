# Isolated Forms 6i Windows source lab

## Current Status, 2026-09-29

The foundation and installer design below describes the original isolated setup. Subsequent
authorized work added private Bastion access, a separate Oracle9i VM, scoped connectivity,
and a genuine Meridian Forms runtime. See the
[installation and runtime record](../../../docs/FORMS6I_AZURE_INSTALLATION_VALIDATION.md)
for observed outcomes and the [delivery tracker](../../../docs/DELIVERY_TRACKER.md)
for remaining product qualification gates. The source database still depends on the
administrator desktop session; unattended recovery is not qualified.

Build/start/test/read scripts in this directory support the owned source lab. They are
not customer migration adapters. Publishing them does not run infrastructure changes,
install media, or authorize a migration. Credentials and licensed Oracle media/binaries
are not included. The fleet's new gateway and extraction code remains unqualified
against this live source; no target migration has been performed.

## Original Foundation Design

`Deploy-Forms6iSourceLab.ps1` provisions only the Windows VM foundation for experimental Oracle Forms 6i source work. Foundation deployment does not contain, download, inspect, or execute Oracle media.

`Install-Forms6iSourceLab.ps1` is a separate, explicitly invoked qualification workflow for the already-provisioned lab VM. It downloads the pinned raw source image inside the VM, verifies its SHA-256 and byte count, converts it with `Convert-RawCdImage.ps1`, mounts the converted image read-only, and runs bounded genuine Oracle installer attempts. Media preparation is never reported as installation.

## Fixed deployment scope

- Subscription: `d4394e57-c076-4c92-a870-5de6bf44f255`
- Tenant: `1984d248-06ca-4d04-a3b8-4c0c1577ab86`
- Resource group: `rg-oracle-forms-migration-fleet-dev-b9f0e875`
- Region: `eastus2`
- VM: `Standard_D2as_v7`, Windows Server 2022 Datacenter Azure Edition small disk
- Disk: 32 GiB Standard SSD LRS

The VM receives a Standard static public IPv4 address solely to provide explicit outbound SNAT. The NSG denies all inbound traffic at priority 100, including virtual-network traffic. There is no RDP or SSH allow rule, no peering, no private-resource route, and no managed identity.

Outbound access fails closed. Narrow rules allow Azure DNS, VM Agent/WireServer, Instance Metadata Service, Windows activation, Azure HTTPS, and public HTTPS. Rules then deny virtual networks, RFC1918 ranges, shared address space, link-local space, and all remaining traffic.

## Commands

Run from PowerShell 7.2 or later:

```powershell
.\Deploy-Forms6iSourceLab.ps1 -Operation Preview
.\Deploy-Forms6iSourceLab.ps1 -Operation Deploy
.\Deploy-Forms6iSourceLab.ps1 -Operation Verify
```

After `Verify` succeeds, the media and installer operations are:

```powershell
.\Install-Forms6iSourceLab.ps1 -Operation PrepareMedia
.\Install-Forms6iSourceLab.ps1 -Operation ProbeInstaller -InstallerTimeoutSeconds 120
.\Install-Forms6iSourceLab.ps1 -Operation AttemptInstall -InstallerTimeoutSeconds 180
.\Install-Forms6iSourceLab.ps1 -Operation VerifyInstallation
```

The guest script durably reserves each installer launch before process creation and enforces a maximum of three actual launches, including legacy attempt evidence without double-counting completed reservations. Genuine install attempts are capped at 180 seconds. The harness terminates only the process tree it started when a timeout expires. The silent attempt uses legacy switches found in the bundled installer engine (`/silent`, `/rspsrc`, `/rspdest`, and `/install`) and a hash-evidenced `USER.STP` product profile. Immediately before execution it revalidates the raw image, converted image, prepared installer, response file, and intentional `USER.STP` delta. That profile installs Forms Developer components without the restricted Forms Server test component. The response records `<Unknown Customer>` rather than asserting a licensed company or accepting license terms. If a license-acceptance action is presented, automation must stop for the operator.

The host binds the requested VM to its canonical Azure resource ID before guest execution. It snapshots and hashes the guest, retrieval, and converter scripts into the protected local evidence directory before invoking them. VM Run Command receives only a compact final summary; the complete guest artifact is retrieved in bounded chunks and accepted locally only when its SHA-256 matches. `VerifyInstallation` performs read-only file and registry inventory and never launches an installed binary.

Windows Server 2022 is an experimental host for this legacy release and is not an Oracle-certified Forms 6i platform. Success means only that exact binaries, registry state, versions, architecture, process outcomes, and hashes were observed on this isolated VM. It is not a support or licensing claim.

For a new foundation, `Preview` compiles Bicep, runs ARM validation, and requires what-if to contain only creates or no-change results. For an existing VM, `Preview` uses Azure control-plane reads only: it validates the actual VM, NIC, subnet, NSG, public IP, identity, and peering associations without invoking guest commands or external HTTPS. `Deploy` repeats the new-foundation gates, deploys, and runs a harmless VM Agent readiness command. `Verify` performs no deployment and includes the guest-readiness and egress probe.

The deployment password is generated with `RandomNumberGenerator`. Azure receives it only through a randomly named temporary state directory under the current user's protected local state. Inheritance is removed and the directory and parameter file are restricted to the current Windows SID before plaintext is written. Cleanup failures are surfaced without logging secret content. The retained local credential is DPAPI-protected for the current Windows user and stored outside the repository under:

```text
%LOCALAPPDATA%\OracleFormsMigrationFleet\forms6i-source-lab\admin-credential.dpapi.json
```

Non-secret what-if, recovered ARM deployment-operation, deployment-output, and verification evidence is stored beside that credential under `evidence`. What-if and verification attempts use timestamped filenames so recovery and retry evidence cannot overwrite an earlier network record.

Installation orchestration evidence is also retained under that protected local `evidence` directory. Guest evidence remains under `C:\OracleForms6iSourceLab\evidence` and includes media hashes, installer commands and exit state, process/window observations, system-DLL pre/post hashes, installed binary hashes and versions, and registry observations.

No interactive desktop path is intentionally exposed. A future desktop-access design requires separate authorization and a private management path such as Azure Bastion or an isolated point-to-site connection; do not add public RDP.