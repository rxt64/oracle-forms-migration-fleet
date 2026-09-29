#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Metadata', 'Chunk')]
    [string] $Operation,

    [Parameter(Mandatory)]
    [ValidatePattern('^C:\\OracleForms6iSourceLab\\evidence\\[A-Za-z0-9._-]+\.json$')]
    [string] $EvidencePath,

    [ValidateRange(0, 1073741824)]
    [int] $Offset = 0,

    [ValidateRange(1, 1800)]
    [int] $Length = 1800
)

$ErrorActionPreference = 'Stop'
$item = Get-Item -LiteralPath $EvidencePath -ErrorAction Stop
if ($Operation -eq 'Metadata') {
    $metadata = [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    Write-Output "FORMS6I_EVIDENCE_METADATA=$($metadata | ConvertTo-Json -Compress)"
    return
}

if ($Offset -ge $item.Length) { throw 'Chunk offset is outside the evidence file.' }
$stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $stream.Position = $Offset
    $buffer = [byte[]]::new([Math]::Min($Length, $item.Length - $Offset))
    $read = $stream.Read($buffer, 0, $buffer.Length)
    if ($read -ne $buffer.Length) { throw 'Guest evidence chunk was not read completely.' }
    Write-Output "FORMS6I_EVIDENCE_CHUNK=$([Convert]::ToBase64String($buffer))"
}
finally { $stream.Dispose() }