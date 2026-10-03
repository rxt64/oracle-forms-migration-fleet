[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublicCertificateBase64,

    [Parameter(Mandatory)]
    [string] $TransferModulePath,

    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{2,31}$')]
    [string] $OdbcDsn = 'OFM_GATEWAY_ORACLE9I',

    [string] $SqlPlusPath = 'C:\oracle\ora90\bin\sqlplus.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:COMPUTERNAME -cne 'OFMORADB') {
    throw 'This operation is restricted to the approved Oracle 9i source-lab host.'
}
if (-not (Test-Path -LiteralPath $TransferModulePath -PathType Leaf)) {
    throw 'The reviewed credential-transfer module was not staged.'
}
if (-not (Test-Path -LiteralPath $SqlPlusPath -PathType Leaf)) {
    throw 'The approved Oracle 9i SQL*Plus executable was not found.'
}

Import-Module $TransferModulePath -Force
$env:ORACLE_HOME = 'C:\oracle\ora90'
$env:ORACLE_SID = 'orcl'
$env:TNS_ADMIN = 'C:\oracle\ora90\network\admin'
$env:PATH = 'C:\oracle\ora90\bin;' + $env:PATH

$certificateBytes = $null
$certificate = $null
$password = $null
$sql = New-SourceGatewayOracleProvisioningSql
$connectValue = $null
$plaintext = $null
$ciphertext = $null
$accountProvisioned = $false
$completed = $false
try {
    $certificateBytes = [Convert]::FromBase64String($PublicCertificateBase64)
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateBytes)
    $password = New-SourceGatewayOraclePassword
    Invoke-SourceGatewaySqlPlus -SqlPlusPath $SqlPlusPath -ProvisioningSql $sql -Password $password
    $accountProvisioned = $true

    $connectValue = "Dsn=$OdbcDsn;Uid=OFM_GATEWAY_RO;Pwd=$password"
    $plaintext = [Text.Encoding]::UTF8.GetBytes($connectValue)
    $ciphertext = Protect-SourceGatewayCredentialBytes -Certificate $certificate -Plaintext $plaintext
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $ciphertextSha256 = ([BitConverter]::ToString($sha256.ComputeHash($ciphertext))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    New-SourceGatewayOracleCredentialResult -Dsn $OdbcDsn `
        -CiphertextBase64 ([Convert]::ToBase64String($ciphertext)) `
        -CiphertextSha256 $ciphertextSha256 -CertificateThumbprint $certificate.Thumbprint |
        ConvertTo-Json -Compress
    $completed = $true
}
finally {
    if (-not $completed -and $accountProvisioned -and $null -ne $sql) {
        Set-SourceGatewayOracleAccountLocked -SqlPlusPath $SqlPlusPath -ProvisioningSql $sql
    }
    if ($null -ne $plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
    if ($null -ne $ciphertext) { [Array]::Clear($ciphertext, 0, $ciphertext.Length) }
    if ($null -ne $certificateBytes) { [Array]::Clear($certificateBytes, 0, $certificateBytes.Length) }
    $password = $null
    $sql = $null
    $connectValue = $null
    if ($null -ne $certificate) { $certificate.Dispose() }
}