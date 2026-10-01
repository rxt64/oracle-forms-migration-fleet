[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $OutputDirectory = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\transfer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this command elevated on the approved Forms VM.'
}

$serviceName = 'OFMSourceGateway'
$serviceSid = "NT SERVICE\$serviceName"
$serviceAccount = 'NT AUTHORITY\LocalService'
. (Join-Path $PSScriptRoot 'SourceGatewayInstaller.Common.ps1')
if ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    throw "$serviceName must be installed before a credential transfer key is created."
}

if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, 'Create one-time source-gateway credential transfer key')) {
    return
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Set-RestrictedPathAcl -Path $OutputDirectory -Grants @(
    'SYSTEM:(OI)(CI)(F)',
    'Administrators:(OI)(CI)(F)',
    "$serviceAccount`:(OI)(CI)(RX)",
    "$serviceSid`:(OI)(CI)(RX)"
)
$transferId = [guid]::NewGuid().ToString('N')
$certificate = $null
$certificatePath = $null
$privateKeyPath = $null
$creationComplete = $false
$operationError = $null
try {
    $certificate = New-SelfSignedCertificate -Type Custom `
        -Subject "CN=OFM Source Gateway Credential Transfer $transferId" `
        -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage KeyEncipherment, DataEncipherment `
        -NotAfter (Get-Date).AddHours(8)
    $certificatePath = "Cert:\LocalMachine\My\$($certificate.Thumbprint)"

    $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    try {
        if ($privateKey -isnot [Security.Cryptography.RSACng]) {
            throw 'The transfer private key is not in the expected Windows CNG key store.'
        }
        $privateKeyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($privateKey.Key.UniqueName)"
        Grant-PathAccess -Path $privateKeyPath -Identity $serviceAccount -Rights '(R)'
        Grant-PathAccess -Path $privateKeyPath -Identity $serviceSid -Rights '(R)'
    }
    finally {
        if ($null -ne $privateKey) { $privateKey.Dispose() }
    }

    $publicCertificatePath = Join-Path $OutputDirectory "credential-transfer-$transferId.cer"
    $metadataPath = Join-Path $OutputDirectory "credential-transfer-$transferId.json"
    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Type CERT | Out-Null
    @{
        schemaVersion = 1
        transferId = $transferId
        certificateThumbprint = $certificate.Thumbprint
        publicCertificateFile = [IO.Path]::GetFileName($publicCertificatePath)
        expiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
    $creationComplete = $true
}
catch {
    $operationError = $_
}
finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
    if (-not $creationComplete -and $null -ne $certificatePath) {
        try {
            Remove-TransferCertificateAndKey -CertificatePath $certificatePath -PrivateKeyPath $privateKeyPath
        }
        catch {
            if ($null -eq $operationError) { $operationError = $_ }
            else {
                $operationError = [InvalidOperationException]::new(
                    "$($operationError.Exception.Message) Transfer-key cleanup also failed: $($_.Exception.Message)",
                    $operationError.Exception)
            }
        }
    }
}
if ($null -ne $operationError) { throw $operationError }

Write-Output "One-time public certificate: $publicCertificatePath"
Write-Output "Transfer metadata: $metadataPath"
Write-Output 'Copy only the CER and metadata off the VM. The private key is non-exportable and expires within eight hours.'