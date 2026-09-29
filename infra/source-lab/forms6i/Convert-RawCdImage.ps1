[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePath,
    [Parameter(Mandatory)][string]$DestinationPath
)

$ErrorActionPreference = 'Stop'
$source = [IO.File]::OpenRead($SourcePath)
$destination = $null
try {
    if ($source.Length -eq 0 -or $source.Length % 2352 -ne 0) {
        throw 'Expected a nonempty raw CD image containing complete 2352-byte sectors.'
    }
    $destination = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    $sector = [byte[]]::new(2352)
    $sectorNumber = 0
    while ($source.Position -lt $source.Length) {
        $received = 0
        while ($received -lt $sector.Length) {
            $count = $source.Read($sector, $received, $sector.Length - $received)
            if ($count -eq 0) { throw "Unexpected end of sector $sectorNumber." }
            $received += $count
        }
        if ($sector[0] -ne 0 -or $sector[11] -ne 0) { throw "Invalid sector sync at $sectorNumber." }
        foreach ($index in 1..10) {
            if ($sector[$index] -ne 255) { throw "Invalid sector sync at $sectorNumber." }
        }
        if ($sector[15] -eq 1) {
            $offset = 16
        } elseif ($sector[15] -eq 2 -and ($sector[18] -band 32) -eq 0) {
            foreach ($index in 16..19) {
                if ($sector[$index] -ne $sector[$index + 4]) { throw "Inconsistent Mode 2 subheader at $sectorNumber." }
            }
            $offset = 24
        } else {
            throw "Unsupported sector mode or Mode 2 Form 2 at $sectorNumber."
        }
        $destination.Write($sector, $offset, 2048)
        $sectorNumber++
    }
    $destination.Flush()
    [pscustomobject]@{ Sectors = $sectorNumber; Bytes = $destination.Length; Destination = $DestinationPath }
} finally {
    if ($null -ne $destination) { $destination.Dispose() }
    $source.Dispose()
}