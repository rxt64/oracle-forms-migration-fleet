#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('PrepareMedia', 'ProbeInstaller', 'AttemptInstall', 'VerifyInstallation')]
    [string] $Operation,

    [string] $ConverterSourceBase64,

    [string] $InstallerArguments = '',

    [ValidateRange(15, 1800)]
    [int] $InstallerTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$labRoot = 'C:\OracleForms6iSourceLab'
$downloadRoot = Join-Path $labRoot 'downloads'
$mediaRoot = Join-Path $labRoot 'media'
$evidenceRoot = Join-Path $labRoot 'evidence'
$rawImagePath = Join-Path $downloadRoot 'OracleFormsAndReports6i-r2-Win9598NT.raw.iso'
$convertedImagePath = Join-Path $downloadRoot 'Forms6i-data.iso'
$converterPath = Join-Path $labRoot 'Convert-RawCdImage.ps1'
$rawImageUri = 'https://archive.org/download/Oracle_Forms_and_Reports_6i_Release_2_Win95-98-NT_2001/OracleFormsAndReports6i-r2-Win9598NT.iso'
$expectedRawHash = '5F84ED194BB6F84CEDC6AF38F4598733DBCF91E8F1008A9C34327090CDA456F4'
$expectedRawBytes = 764430576L
$expectedConvertedHash = '72A706D4E48D7E6F2B734ADE97AF859FED1CA91ADB8629415A785CEA76A406BE'
$expectedConvertedBytes = 665626624L

function Initialize-LabDirectories {
    foreach ($path in @($labRoot, $downloadRoot, $evidenceRoot)) {
        [void](New-Item -ItemType Directory -Path $path -Force)
    }
}

function Get-FileEvidence {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $item = Get-Item -LiteralPath $Path
    [ordered]@{
        path = $item.FullName
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Assert-FileEvidence {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [long] $ExpectedBytes,
        [Parameter(Mandatory)] [string] $ExpectedHash
    )

    $evidence = Get-FileEvidence -Path $Path
    if ($null -eq $evidence -or $evidence.bytes -ne $ExpectedBytes -or
        -not [string]::Equals($evidence.sha256, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "File evidence did not match the pinned byte count and SHA-256: $Path"
    }
    return $evidence
}

function Invoke-StreamingDownload {
    param(
        [Parameter(Mandatory)] [uri] $Uri,
        [Parameter(Mandatory)] [string] $Destination
    )

    Add-Type -AssemblyName System.Net.Http
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $temporaryPath = "$Destination.partial"
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromHours(1)
    $response = $null
    $inputStream = $null
    $outputStream = $null
    try {
        $response = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        [void]$response.EnsureSuccessStatusCode()
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $inputStream.CopyTo($outputStream)
        $outputStream.Flush()
    }
    finally {
        if ($null -ne $outputStream) { $outputStream.Dispose() }
        if ($null -ne $inputStream) { $inputStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
    Move-Item -LiteralPath $temporaryPath -Destination $Destination -Force
}

function Save-Converter {
    if ([string]::IsNullOrWhiteSpace($ConverterSourceBase64)) {
        throw 'PrepareMedia requires the converter source as base64.'
    }

    $bytes = [Convert]::FromBase64String($ConverterSourceBase64)
    [IO.File]::WriteAllBytes($converterPath, $bytes)
    return Get-FileEvidence -Path $converterPath
}

function Copy-MountedMedia {
    $diskImage = Mount-DiskImage -ImagePath $convertedImagePath -StorageType ISO -Access ReadOnly -PassThru
    try {
        $volume = $diskImage | Get-Volume | Where-Object DriveLetter | Select-Object -First 1
        if ($null -eq $volume) {
            throw 'The converted image mounted without a drive-letter volume.'
        }
        $source = "$($volume.DriveLetter):\"
        $staging = "$mediaRoot.staging"
        if (Test-Path -LiteralPath $staging) {
            & attrib.exe -R "$staging\*" /S /D
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction Stop
        }
        [void](New-Item -ItemType Directory -Path $staging -Force)
        Copy-Item -Path (Join-Path $source '*') -Destination $staging -Recurse -Force
        if (-not (Test-Path -LiteralPath (Join-Path $staging 'INSTALL\SETUP.EXE') -PathType Leaf)) {
            throw 'Mounted media did not contain INSTALL\SETUP.EXE.'
        }
        & attrib.exe -R "$staging\*" /S /D
        if (Test-Path -LiteralPath $mediaRoot) {
            & attrib.exe -R "$mediaRoot\*" /S /D
            Remove-Item -LiteralPath $mediaRoot -Recurse -Force -ErrorAction Stop
        }
        Move-Item -LiteralPath $staging -Destination $mediaRoot
    }
    finally {
        [void](Dismount-DiskImage -ImagePath $convertedImagePath -ErrorAction SilentlyContinue)
    }
}

function Get-ProcessTree {
    param([Parameter(Mandatory)] [int] $RootProcessId)

    $allProcesses = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine)
    $selectedIds = [Collections.Generic.HashSet[int]]::new()
    [void]$selectedIds.Add($RootProcessId)
    do {
        $added = $false
        foreach ($process in $allProcesses) {
            if ($selectedIds.Contains([int]$process.ParentProcessId) -and $selectedIds.Add([int]$process.ProcessId)) {
                $added = $true
            }
        }
    } while ($added)
    return @($allProcesses | Where-Object { $selectedIds.Contains([int]$_.ProcessId) })
}

function Get-TopLevelWindows {
    param([Parameter(Mandatory)] [int[]] $ProcessIds)

    if (-not ('Forms6iWindowInspector' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Forms6iWindowInspector {
    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
    public static string[] ReadVisibleWindows(int[] processIds) {
        var ids = new HashSet<int>(processIds);
        var results = new List<string>();
        EnumWindows(delegate(IntPtr handle, IntPtr parameter) {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (ids.Contains((int)processId) && IsWindowVisible(handle)) {
                var text = new StringBuilder(2048);
                GetWindowText(handle, text, text.Capacity);
                results.Add(processId.ToString() + "|" + handle.ToInt64().ToString() + "|" + text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return results.ToArray();
    }
}
'@
    }
    return @([Forms6iWindowInspector]::ReadVisibleWindows($ProcessIds) | ForEach-Object {
            $parts = $_ -split '\|', 3
            [ordered]@{ processId = [int]$parts[0]; handle = $parts[1]; title = $parts[2] }
        })
}

function Stop-StartedProcessTree {
    param([Parameter(Mandatory)] [int] $RootProcessId)

    $tree = @(Get-ProcessTree -RootProcessId $RootProcessId | Sort-Object ProcessId -Descending)
    foreach ($process in $tree) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-BoundedInstaller {
    param(
        [Parameter(Mandatory)] [string] $Executable,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Arguments,
        [Parameter(Mandatory)] [int] $TimeoutSeconds
    )

    $startedUtc = [DateTime]::UtcNow
    $processParameters = @{
        FilePath = $Executable
        WorkingDirectory = Split-Path $Executable
        PassThru = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $processParameters.ArgumentList = $Arguments
    }
    $process = Start-Process @processParameters
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $tree = @()
    $alive = @($process)
    $completed = $false
    $windows = @()
    $exitCode = $null
    try {
        do {
            $tree = @(Get-ProcessTree -RootProcessId $process.Id)
            $alive = @($tree | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
            if ($alive.Count -eq 0) { break }
            $remainingMilliseconds = [int][Math]::Max(1, ($TimeoutSeconds - $stopwatch.Elapsed.TotalSeconds) * 1000)
            $waitingProcess = Get-Process -Id $alive[0].ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $waitingProcess) {
                try { [void]$waitingProcess.WaitForExit($remainingMilliseconds) }
                finally { $waitingProcess.Dispose() }
            }
        } while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
        $tree = @(Get-ProcessTree -RootProcessId $process.Id)
        $completed = $tree.Count -eq 0
        $processIds = @($tree.ProcessId | ForEach-Object { [int]$_ })
        if ($processIds.Count -eq 0) { $processIds = @($process.Id) }
        $windows = @(Get-TopLevelWindows -ProcessIds $processIds)
        $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    }
    finally {
        try {
            if (-not $completed) { Stop-StartedProcessTree -RootProcessId $process.Id }
        } finally { $process.Dispose() }
    }

    [ordered]@{
        executable = $Executable
        arguments = $Arguments
        startedUtc = $startedUtc.ToString('O')
        observedUtc = [DateTime]::UtcNow.ToString('O')
        timeoutSeconds = $TimeoutSeconds
        completed = $completed
        exitCode = $exitCode
        processTree = $tree
        visibleWindows = $windows
        terminatedAfterTimeout = -not $completed
    }
}

function Get-OracleInstallationEvidence {
    $candidateNames = @('ifbld60.exe', 'ifcmp60.exe', 'ifrun60.exe')
    $roots = @('C:\ORANT', 'C:\ORAWIN', 'C:\Oracle') | Where-Object { Test-Path -LiteralPath $_ }
    $binaries = foreach ($root in $roots) {
        Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -in $candidateNames } | ForEach-Object {
            $version = $_.VersionInfo
            [ordered]@{
                path = $_.FullName
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                fileVersion = $version.FileVersion
                productVersion = $version.ProductVersion
                architecture = $(try { (Get-PEArchitecture -Path $_.FullName) } catch { 'Unknown' })
            }
        }
    }
    $registry = foreach ($root in @('HKLM:\SOFTWARE\ORACLE', 'HKLM:\SOFTWARE\WOW6432Node\ORACLE')) {
        if (Test-Path -LiteralPath $root) {
            foreach ($path in @($root) + @(Get-ChildItem -LiteralPath $root -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSPath)) {
                [ordered]@{
                    path = $path
                    values = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue |
                        Select-Object * -ExcludeProperty PSPath, PSParentPath, PSChildName, PSDrive, PSProvider
                }
            }
        }
    }
    [ordered]@{ roots = $roots; binaries = @($binaries); registry = @($registry) }
}

function Get-PEArchitecture {
    param([Parameter(Mandatory)] [string] $Path)

    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return 'NotPE' }
        switch ($reader.ReadUInt16()) {
            0x014c { return 'x86' }
            0x8664 { return 'x64' }
            default { return 'Other' }
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-SystemDllEvidence {
    $names = @('msvcrt40.dll', 'msvcrt.dll', 'msvcirt.dll', 'mfc40.dll', 'mfc42.dll', 'olepro32.dll', 'oleaut32.dll')
    $rows = foreach ($directory in @((Join-Path $env:SystemRoot 'System32'), (Join-Path $env:SystemRoot 'SysWOW64'))) {
        foreach ($name in $names) {
            $path = Join-Path $directory $name
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $item = Get-Item -LiteralPath $path
                [ordered]@{
                    path = $path
                    bytes = $item.Length
                    fileVersion = $item.VersionInfo.FileVersion
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                }
            }
            else {
                [ordered]@{ path = $path; bytes = $null; fileVersion = $null; sha256 = $null }
            }
        }
    }
    return @($rows)
}

function Compare-SystemDllEvidence {
    param(
        [Parameter(Mandatory)] [object[]] $Before,
        [Parameter(Mandatory)] [object[]] $After
    )

    $changes = foreach ($beforeItem in $Before) {
        $afterItem = @($After | Where-Object { $_.path -eq $beforeItem.path })[0]
        if ($null -eq $afterItem -or $beforeItem.sha256 -ne $afterItem.sha256) {
            [ordered]@{ path = $beforeItem.path; beforeSha256 = $beforeItem.sha256; afterSha256 = $afterItem.sha256 }
        }
    }
    return @($changes)
}

function Initialize-SilentFormsDeveloperInstall {
    $installRoot = Join-Path $mediaRoot 'INSTALL'
    $responsePath = Join-Path $installRoot 'OFM-SILENT.RSP'
    $responseResultPath = Join-Path $installRoot 'OFM-SILENT-RESULT.RSP'
    $userSetupPath = Join-Path $installRoot 'USER.STP'
    $originalUserSetupPath = Join-Path $installRoot 'USER.STP.OFM-ORIGINAL'
    if (-not (Test-Path -LiteralPath $originalUserSetupPath -PathType Leaf)) {
        Copy-Item -LiteralPath $userSetupPath -Destination $originalUserSetupPath
    }

    $responseContent = @'
language_content=list("English")
install_settings_content=list("<Unknown Customer>","C:\ORANT")
'@
    $userSetupContent = @'
{
  product_home = current_directory();
  w32install = "ntinstall";
  forms_install = true;
  reports_install = false;
  forms_v = "true";
  reports_v = "false";
  relnotes_v = "true";
  test_feature = FALSE;
  bootstrap = TRUE;
  selected_products = list("w32pj60", "w32otm60", "w32qb60", "w32sch60", "w32d2dh60",
    "w32oin60", "w32cdesql60", "w32fdes60", "w32frun60", "w32fapi60", "w32fccsnd60",
    "w32fsql60", "w32fjava60", "w32graph60", "w32pb60", "w32ocx60", "w32netclt80",
    "w32net8a80", "w32plus80", "w32relnotef", "%w32install%");
}
'@
    [IO.File]::WriteAllText($responsePath, $responseContent, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($userSetupPath, $userSetupContent, [Text.Encoding]::ASCII)
    Remove-Item -LiteralPath $responseResultPath -Force -ErrorAction SilentlyContinue

    [ordered]@{
        response = Get-FileEvidence -Path $responsePath
        responseResultPath = $responseResultPath
        originalUserSetup = Get-FileEvidence -Path $originalUserSetupPath
        activeUserSetup = Get-FileEvidence -Path $userSetupPath
        oracleHome = 'C:\ORANT'
        companyField = '<Unknown Customer>'
        language = 'English'
        productProfile = 'Forms Developer components without restricted Forms Server test component'
        installerArguments = "/silent /rspsrc `"$responsePath`" /rspdest `"$responseResultPath`" /install"
    }
}

function Write-Evidence {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [object] $Evidence
    )

    $path = Join-Path $evidenceRoot "$Name-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
    $Evidence | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8
    $sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $summary = [ordered]@{
        operation = $Evidence.operation; status = $Evidence.status; attemptNumber = $Evidence.attemptNumber
        evidencePath = $path; evidenceSha256 = $sha256; completed = $Evidence.attempt.completed
        exitCode = $Evidence.attempt.exitCode; terminatedAfterTimeout = $Evidence.attempt.terminatedAfterTimeout
        installedBinaryCount = @($Evidence.installation.binaries).Count
        systemDllChangeCount = @($Evidence.systemDllChanges).Count
    }
    Write-Output "FORMS6I_EVIDENCE_PATH=$path"
    Write-Output "FORMS6I_EVIDENCE_SHA256=$sha256"
    Write-Output "FORMS6I_RESULT=$($summary | ConvertTo-Json -Depth 4 -Compress)"
}

function Reserve-AttemptSlot {
    $mutex = [Threading.Mutex]::new($false, 'Global\OracleForms6iInstallerAttemptBudget')
    if (-not $mutex.WaitOne([TimeSpan]::FromSeconds(10))) { throw 'Could not lock the installer attempt budget.' }
    try {
        $attemptFiles = @(Get-ChildItem -LiteralPath $evidenceRoot -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'probe-installer-*.json' -or $_.Name -like 'attempt-install-*.json' })
        $activeReservations = @(Get-ChildItem -LiteralPath $evidenceRoot -File -Filter 'installer-attempt-reservation-*.json' -ErrorAction SilentlyContinue |
                ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } | Where-Object status -eq 'Reserved')
        $consumed = $attemptFiles.Count + $activeReservations.Count
        if ($consumed -ge 3) { throw 'The three-attempt installer budget has been exhausted on this VM.' }
        $record = [ordered]@{ id = [Guid]::NewGuid().ToString('N'); attemptNumber = $consumed + 1; status = 'Reserved'; operation = $Operation; reservedUtc = [DateTime]::UtcNow.ToString('O') }
        $path = Join-Path $evidenceRoot "installer-attempt-reservation-$($record.id).json"
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 4))
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        return [pscustomobject]@{ Path = $path; Id = $record.id; AttemptNumber = $record.attemptNumber }
    }
    finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
}

function Complete-AttemptReservation {
    param([Parameter(Mandatory)] [object] $Reservation)
    $savedEvidence = @(Get-ChildItem -LiteralPath $evidenceRoot -File |
        Where-Object { $_.Name -like 'probe-installer-*.json' -or $_.Name -like 'attempt-install-*.json' } |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } |
        Where-Object { $_.reservationId -eq $Reservation.Id })
    if ($savedEvidence.Count -eq 0) { return }
    $record = Get-Content -LiteralPath $Reservation.Path -Raw | ConvertFrom-Json
    $record.status = 'Completed'
    $record | Add-Member -NotePropertyName completedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O'))
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Reservation.Path -Encoding UTF8
}

function Assert-PreparedInstallerProvenance {
    param([Parameter(Mandatory)] [string] $InstallerPath)
    $prepareFile = Get-ChildItem -LiteralPath $evidenceRoot -File -Filter 'prepare-media-*.json' -ErrorAction Stop | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $prepareFile) { throw 'Prepared-media provenance evidence is missing.' }
    $prepare = Get-Content -LiteralPath $prepareFile.FullName -Raw | ConvertFrom-Json
    $raw = Assert-FileEvidence -Path $rawImagePath -ExpectedBytes $expectedRawBytes -ExpectedHash $expectedRawHash
    $converted = Assert-FileEvidence -Path $convertedImagePath -ExpectedBytes $expectedConvertedBytes -ExpectedHash $expectedConvertedHash
    $installer = Get-FileEvidence -Path $InstallerPath
    if ($null -eq $installer -or -not [string]::Equals($installer.sha256, [string]$prepare.installer.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Prepared installer no longer matches its media-preparation evidence.' }
    return [ordered]@{ prepareEvidencePath = $prepareFile.FullName; rawImage = $raw; convertedImage = $converted; installer = $installer }
}

function Get-LatestAttemptSummary {
    $latest = Get-ChildItem -LiteralPath $evidenceRoot -File -Filter 'attempt-install-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $latest) { return $null }
    $attempt = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    [ordered]@{
        evidencePath = $latest.FullName
        attemptNumber = $attempt.attemptNumber
        status = $attempt.status
        executable = $attempt.attempt.executable
        arguments = $attempt.attempt.arguments
        completed = $attempt.attempt.completed
        exitCode = $attempt.attempt.exitCode
        terminatedAfterTimeout = $attempt.attempt.terminatedAfterTimeout
        systemDllChangeCount = @($attempt.systemDllChanges).Count
        installedBinaryCount = @($attempt.installation.binaries).Count
    }
}

Initialize-LabDirectories
$common = [ordered]@{
    operation = $Operation
    capturedUtc = [DateTime]::UtcNow.ToString('O')
    computerName = $env:COMPUTERNAME
    os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture)
    processSessionId = (Get-Process -Id $PID).SessionId
}

switch ($Operation) {
    'PrepareMedia' {
        $converter = Save-Converter
        $raw = Get-FileEvidence -Path $rawImagePath
        if ($null -eq $raw -or $raw.bytes -ne $expectedRawBytes -or $raw.sha256 -ne $expectedRawHash) {
            Remove-Item -LiteralPath $rawImagePath -Force -ErrorAction SilentlyContinue
            Invoke-StreamingDownload -Uri $rawImageUri -Destination $rawImagePath
        }
        $raw = Assert-FileEvidence -Path $rawImagePath -ExpectedBytes $expectedRawBytes -ExpectedHash $expectedRawHash

        $converted = Get-FileEvidence -Path $convertedImagePath
        if ($null -eq $converted -or $converted.bytes -ne $expectedConvertedBytes -or $converted.sha256 -ne $expectedConvertedHash) {
            Remove-Item -LiteralPath $convertedImagePath -Force -ErrorAction SilentlyContinue
            $conversion = & $converterPath -SourcePath $rawImagePath -DestinationPath $convertedImagePath
        }
        $converted = Assert-FileEvidence -Path $convertedImagePath -ExpectedBytes $expectedConvertedBytes -ExpectedHash $expectedConvertedHash
        Copy-MountedMedia
        $setup = Get-FileEvidence -Path (Join-Path $mediaRoot 'INSTALL\SETUP.EXE')
        $evidence = $common + [ordered]@{
            sourceUri = $rawImageUri
            rawImage = $raw
            converter = $converter
            conversion = $conversion
            convertedImage = $converted
            mediaRoot = $mediaRoot
            installer = $setup
            status = 'MediaPreparedNotInstalled'
        }
        Write-Evidence -Name 'prepare-media' -Evidence $evidence
    }
    'ProbeInstaller' {
        $reservation = Reserve-AttemptSlot
        $attemptNumber = $reservation.AttemptNumber
        $installer = Join-Path $mediaRoot 'INSTALL\SETUP.EXE'
        if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Prepared installer media is missing.' }
        $provenance = Assert-PreparedInstallerProvenance -InstallerPath $installer
        $attempt = Invoke-BoundedInstaller -Executable $installer -Arguments '' -TimeoutSeconds $InstallerTimeoutSeconds
        $evidence = $common + [ordered]@{
            attemptNumber = $attemptNumber
            reservationId = $reservation.Id
            provenance = $provenance
            attempt = $attempt
            installation = Get-OracleInstallationEvidence
            status = 'InstallerProbeOnly'
        }
        try { Write-Evidence -Name 'probe-installer' -Evidence $evidence } finally { Complete-AttemptReservation -Reservation $reservation }
    }
    'AttemptInstall' {
        if ($InstallerTimeoutSeconds -gt 180) { throw 'A genuine install attempt is limited to 180 seconds.' }
        $reservation = Reserve-AttemptSlot
        $attemptNumber = $reservation.AttemptNumber
        $installer = Join-Path $mediaRoot 'INSTALL\SETUP.EXE'
        try {
            if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Prepared installer media is missing.' }
            $silentInstall = Initialize-SilentFormsDeveloperInstall
            if (-not [string]::IsNullOrWhiteSpace($InstallerArguments) -and $InstallerArguments -ne $silentInstall.installerArguments) { throw 'Installer arguments differ from the guest-staged, hash-evidenced legacy response-file command.' }
            $provenance = Assert-PreparedInstallerProvenance -InstallerPath $installer
            if (-not [string]::Equals((Get-FileHash -LiteralPath (Join-Path $mediaRoot 'INSTALL\USER.STP') -Algorithm SHA256).Hash, $silentInstall.activeUserSetup.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'The intentional USER.STP delta changed before installer execution.' }
            $systemDllsBefore = @(Get-SystemDllEvidence)
            [void](Assert-FileEvidence -Path $silentInstall.response.path -ExpectedBytes $silentInstall.response.bytes -ExpectedHash $silentInstall.response.sha256)
            $attempt = Invoke-BoundedInstaller -Executable $installer -Arguments $silentInstall.installerArguments -TimeoutSeconds $InstallerTimeoutSeconds
            $systemDllsAfter = @(Get-SystemDllEvidence)
            $systemDllChanges = @(Compare-SystemDllEvidence -Before $systemDllsBefore -After $systemDllsAfter)
            $status = if ($systemDllChanges.Count -eq 0) { 'InstallAttemptCompletedNotYetVerified' } else { 'ProhibitedSystemDllChangeObserved' }
            $evidence = $common + [ordered]@{ attemptNumber = $attemptNumber; reservationId = $reservation.Id; provenance = $provenance; silentInstall = $silentInstall; attempt = $attempt; systemDllsBefore = $systemDllsBefore; systemDllsAfter = $systemDllsAfter; systemDllChanges = $systemDllChanges; installation = Get-OracleInstallationEvidence; status = $status }
            Write-Evidence -Name 'attempt-install' -Evidence $evidence
        }
        catch {
            $failureEvidence = $common + [ordered]@{ attemptNumber = $attemptNumber; reservationId = $reservation.Id; attempt = $attempt; systemDllsBefore = @($systemDllsBefore); systemDllsAfter = @(Get-SystemDllEvidence); systemDllChanges = @(); installation = Get-OracleInstallationEvidence; failure = [ordered]@{ type = $_.Exception.GetType().FullName; message = $_.Exception.Message }; status = 'InstallAttemptFailed' }
            Write-Evidence -Name 'attempt-install' -Evidence $failureEvidence
            throw
        }
        finally { Complete-AttemptReservation -Reservation $reservation }
    }
    'VerifyInstallation' {
        $installation = Get-OracleInstallationEvidence
        $status = if (@($installation.binaries).Count -gt 0) { 'InstalledBinariesObserved' } else { 'NoInstalledBinariesObserved' }
        $evidence = $common + [ordered]@{
            latestAttempt = Get-LatestAttemptSummary
            installation = $installation
            verificationMode = 'ReadOnlyInventory'
            status = $status
        }
        Write-Evidence -Name 'verify-installation' -Evidence $evidence
    }
}