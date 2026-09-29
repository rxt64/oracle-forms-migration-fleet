#requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$labRoot = 'C:\OracleForms6iSourceLab'
$installRoot = Join-Path $labRoot 'media\INSTALL'
$evidenceRoot = Join-Path $labRoot 'evidence'

function Get-PeFacts {
    param([Parameter(Mandatory)] [string] $Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    $subsystemOffset = if ($magic -eq 0x10b) { $optional + 68 } else { $optional + 88 }
    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    $imports = [regex]::Matches($ascii, '(?i)([A-Z0-9_.-]{1,128}\.DLL)', [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(2)) | ForEach-Object Value | Sort-Object -Unique
    [ordered]@{
        path = $Path
        bytes = $bytes.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        machine = switch ($machine) { 0x014c { 'x86' } 0x8664 { 'x64' } default { "0x$($machine.ToString('X4'))" } }
        subsystem = switch ([BitConverter]::ToUInt16($bytes, $subsystemOffset)) { 2 { 'WindowsGui' } 3 { 'WindowsConsole' } default { "Other:$($_)" } }
        fileVersion = (Get-Item -LiteralPath $Path).VersionInfo.FileVersion
        importedDllNames = @($imports)
    }
}

function Get-TextFileFacts {
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [ordered]@{ path = $Path; exists = $false } }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -gt 524288) { return [ordered]@{ path = $Path; exists = $true; bytes = $item.Length; contentSkipped = 'Exceeds 512 KiB diagnostic limit' } }
    $lines = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue)
    [ordered]@{
        path = $Path; exists = $true; bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        diagnosticLines = @($lines | Where-Object { $_ -match '(?i)error|warn|fail|unsupported|platform|operating system|response|silent' } | Select-Object -Last 80)
    }
}

$attemptStart = [datetime]'2026-09-24T03:51:36Z'
$logDirectories = @($installRoot, $evidenceRoot, $env:TEMP, 'C:\Program Files\Oracle', 'C:\Program Files (x86)\Oracle') |
    Where-Object { Test-Path -LiteralPath $_ -PathType Container }
$logCandidates = @($logDirectories | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -File -ErrorAction SilentlyContinue
    } | Where-Object {
        $_.LastWriteTimeUtc -ge $attemptStart -and ($_.Extension -in @('.log', '.out', '.err', '.txt', '.rsp'))
    } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 100)
$compatibilityLayers = foreach ($key in @(
        'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
        'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')) {
    if (Test-Path -LiteralPath $key) {
        [ordered]@{ path = $key; values = Get-ItemProperty -LiteralPath $key | Select-Object * -ExcludeProperty PSPath, PSParentPath, PSChildName, PSDrive, PSProvider }
    }
}
$result = [ordered]@{
    capturedUtc = [DateTime]::UtcNow.ToString('O')
    operation = 'DiagnoseInstallerReadOnly'
    os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
    currentSessionId = (Get-Process -Id $PID).SessionId
    sessions = @(Get-CimInstance Win32_LogonSession | Select-Object LogonId, LogonType, StartTime)
    activeInstallerProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('SETUP.EXE', 'ORAINST.EXE') } | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, SessionId)
    installerFiles = @((Join-Path $installRoot 'SETUP.EXE'), (Join-Path $installRoot 'ORAINST.EXE'), (Join-Path $installRoot 'OICTRL32.DLL')) | ForEach-Object { Get-PeFacts -Path $_ }
    responseFiles = @((Join-Path $installRoot 'OFM-SILENT.RSP'), (Join-Path $installRoot 'OFM-SILENT-RESULT.RSP'), (Join-Path $installRoot 'NT.RSP'), (Join-Path $installRoot 'WIN32.RSP'), (Join-Path $installRoot 'USER.STP'), (Join-Path $installRoot 'USER.STP.OFM-ORIGINAL')) | ForEach-Object { Get-TextFileFacts -Path $_ }
    compatibilityLayers = @($compatibilityLayers)
    logs = @($logCandidates | ForEach-Object { Get-TextFileFacts -Path $_.FullName })
    status = 'ReadOnlyDiagnosticsCollected'
}
$path = Join-Path $evidenceRoot "diagnose-installer-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
$sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
Write-Output "FORMS6I_EVIDENCE_PATH=$path"
Write-Output "FORMS6I_EVIDENCE_SHA256=$sha256"
Write-Output "FORMS6I_RESULT=$(([ordered]@{ status = $result.status; evidencePath = $path; evidenceSha256 = $sha256; activeInstallerProcessCount = @($result.activeInstallerProcesses).Count; logCount = @($result.logs).Count }) | ConvertTo-Json -Compress)"