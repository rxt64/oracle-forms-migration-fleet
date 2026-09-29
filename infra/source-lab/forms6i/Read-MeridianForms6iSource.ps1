#requires -Version 5.1
param(
    [ValidateSet('Metadata', 'Chunk', 'Validate')][string]$Operation = 'Metadata',
    [ValidateRange(0, 1048576)][int]$Offset = 0,
    [ValidateRange(1, 1800)][int]$Length = 1800
)
$ErrorActionPreference = 'Stop'
if ($Operation -eq 'Validate') { 'Read-only native source transport parameters valid.'; return }
if ($env:COMPUTERNAME -ne 'OFMFORMS6I') { throw 'Wrong Forms host.' }
$path = 'C:\OracleForms6iSourceLab\app\generated\MRD_ORDER_ENTRY.fmb'
$item = Get-Item -LiteralPath $path
if ($item.Length -le 0 -or $item.Length -gt 1048576) { throw 'Native input is outside the bounded transport size.' }
$source = [IO.File]::ReadAllBytes($path)
$buffer = New-Object IO.MemoryStream
try {
    $compressor = New-Object IO.Compression.GZipStream($buffer, [IO.Compression.CompressionMode]::Compress, $true)
    try { $compressor.Write($source, 0, $source.Length) } finally { $compressor.Dispose() }
    $compressed = $buffer.ToArray()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString($hasher.ComputeHash($source)).Replace('-', '')
        $transportHash = [BitConverter]::ToString($hasher.ComputeHash($compressed)).Replace('-', '')
    } finally { $hasher.Dispose() }
    if ($Operation -eq 'Metadata') {
        $metadata = [ordered]@{ path = $path; bytes = $source.Length; sha256 = $hash; compressedBytes = $compressed.Length; compressedSha256 = $transportHash }
        'OFM_SOURCE_METADATA=' + ($metadata | ConvertTo-Json -Compress)
    } else {
        if ($Offset -ge $compressed.Length) { throw 'Chunk offset is outside the source transport.' }
        $count = [Math]::Min($Length, $compressed.Length - $Offset)
        'OFM_SOURCE_CHUNK=' + [Convert]::ToBase64String($compressed, $Offset, $count)
    }
} finally { $buffer.Dispose() }