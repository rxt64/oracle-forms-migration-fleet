[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('InitializeLabConnection', 'ValidatePrerequisites', 'NewTransferKey', 'ProvisionOracle',
        'CompleteTransfer', 'CleanupTransferKey')]
    [string] $Operation,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://github\.com/rxt64/oracle-forms-migration-fleet/releases/download/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\.zip$')]
    [string] $BundleUrl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-f0-9]{64}$')]
    [string] $BundleSha256,

    [string] $PublicCertificateBase64,

    [ValidatePattern('^[a-f0-9]{32}$')]
    [string] $TransferId,

    [ValidatePattern('^[A-Fa-f0-9]{40,64}$')]
    [string] $TransferCertificateThumbprint,

    [string] $CiphertextBase64,

    [ValidatePattern('^[a-f0-9]{64}$')]
    [string] $CiphertextSha256,

    [ValidateSet('OFM_GATEWAY_ORACLE9I')]
    [string] $OdbcDsn = 'OFM_GATEWAY_ORACLE9I',

    [ValidateSet('OFM_ORCL9I')]
    [string] $TnsAlias = 'OFM_ORCL9I',

    [ValidateSet('10.246.0.37')]
    [string] $OracleHost = '10.246.0.37',

    [ValidateSet(1521)]
    [int] $OraclePort = 1521,

    [ValidateSet('orcl')]
    [string] $OracleSid = 'orcl',

    [ValidateSet('true', 'false')]
    [string] $PlanOnly = 'true'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedFiles = @(
    'Complete-SourceGatewayCredentialTransfer.ps1',
    'Initialize-SourceGatewayLabOracleConnection.ps1',
    'New-SourceGatewayCredentialTransferKey.ps1',
    'New-SourceGatewayOracleCredential.ps1',
    'SourceGatewayCredentialTransfer.psm1',
    'SourceGatewayInstaller.Common.ps1'
)
$temporaryRoot = Join-Path $env:TEMP "ofm-oracle-credential-$([guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $temporaryRoot 'bundle.zip'
$bundleRoot = Join-Path $temporaryRoot 'bundle'

function Write-AllowlistedResult {
    param([Parameter(Mandatory)] [Collections.IDictionary] $Value)
    Write-Output ($Value | ConvertTo-Json -Compress)
}

function Assert-FormsOdbcPrerequisite {
    param([Parameter(Mandatory)] [string] $Dsn, [Parameter(Mandatory)] [string] $Server)

    $resolved = Resolve-SourceGatewayFormsOdbcDriver -Dsn $Dsn -OdbcRoot 'HKLM:\SOFTWARE\WOW6432Node\ODBC' `
        -SystemRoot $env:SystemRoot `
        -AllowedDriverRoots (Get-SourceGatewayAllowedOdbcDriverRoots -SystemRoot $env:SystemRoot) `
        -ExpectedServer $Server
    $tnsAdmin = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\oracle-net'
    [void](Assert-SourceGatewayLabTnsBinding -ServiceName 'OFMSourceGateway' -TnsAdmin $tnsAdmin `
        -ExpectedTnsNames (New-SourceGatewayTnsNamesContent -Alias $Server -OracleHost $OracleHost `
            -OraclePort $OraclePort -OracleSid $OracleSid))
    return $resolved
}

try {
    [void](New-Item -ItemType Directory -Path $temporaryRoot)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $BundleUrl -OutFile $archivePath -UseBasicParsing
    $actualBundleSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualBundleSha256 -cne $BundleSha256) {
        throw 'The reviewed Oracle credential bundle changed digest.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        if (($entryNames -join "`n") -cne (($expectedFiles | Sort-Object) -join "`n") -or
            @($archive.Entries | Where-Object { $_.FullName -ne $_.Name }).Count -ne 0) {
            throw 'The Oracle credential bundle does not contain exactly the reviewed files.'
        }
    }
    finally {
        $archive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $bundleRoot)
    $commonModulePath = Join-Path $bundleRoot 'SourceGatewayInstaller.Common.ps1'
    . $commonModulePath

    if ($Operation -eq 'ProvisionOracle') {
        if ($env:COMPUTERNAME -cne 'OFMORADB') {
            throw 'The Oracle credential leg reached an unexpected host.'
        }
        if ([string]::IsNullOrWhiteSpace($PublicCertificateBase64)) {
            throw 'The Oracle credential leg requires the one-time public certificate.'
        }
        & (Join-Path $bundleRoot 'New-SourceGatewayOracleCredential.ps1') `
            -PublicCertificateBase64 $PublicCertificateBase64 `
            -TransferModulePath (Join-Path $bundleRoot 'SourceGatewayCredentialTransfer.psm1') `
            -OdbcDsn $OdbcDsn
        return
    }

    if ($env:COMPUTERNAME -cne 'OFMFORMS6I') {
        throw 'The source-gateway credential leg reached an unexpected host.'
    }

    Import-Module (Join-Path $bundleRoot 'SourceGatewayCredentialTransfer.psm1') -Force

    $programDataRoot = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway'
    $transferRoot = Join-Path $programDataRoot 'transfer'

    function Remove-BoundTransferMaterial {
        param([Parameter(Mandatory)] [string] $Id)

        $metadataPath = Join-Path $transferRoot "credential-transfer-$Id.json"
        $privateKeyName = $null
        $privateKeyPath = $null
        $metadataBinding = Read-SourceGatewayTransferMetadata -TransferId $Id -MetadataPath $metadataPath
        if (-not [string]::IsNullOrWhiteSpace([string]$metadataBinding.PrivateKeyName)) {
            $privateKeyName = [string]$metadataBinding.PrivateKeyName
            $privateKeyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$privateKeyName"
        }

        $subject = "CN=OFM Source Gateway Credential Transfer $Id"
        $certificates = @(Get-SourceGatewayTransferCertificates -TransferId $Id `
            -Certificates @(Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My'))
        foreach ($certificate in $certificates) {
            $certificatePrivateKeyPath = $null
            $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
            try {
                if ($privateKey -isnot [Security.Cryptography.RSACng]) {
                    throw 'The transfer private key is not in the expected Windows CNG key store.'
                }
                $certificatePrivateKeyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($privateKey.Key.UniqueName)"
            }
            finally {
                if ($null -ne $privateKey) { $privateKey.Dispose() }
            }
            Remove-TransferCertificateAndKey -CertificatePath $certificate.PSPath -PrivateKeyPath $certificatePrivateKeyPath
        }
        if ($certificates.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($privateKeyPath) -and
            (Test-Path -LiteralPath $privateKeyPath -PathType Leaf)) {
            $cngKey = [Security.Cryptography.CngKey]::Open(
                $privateKeyName,
                [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider,
                [Security.Cryptography.CngKeyOpenOptions]::MachineKey)
            try { $cngKey.Delete() }
            finally { $cngKey.Dispose() }
        }

        $residualFiles = @(Get-ChildItem -LiteralPath $transferRoot -Filter "credential-transfer-$Id.*" -ErrorAction SilentlyContinue)
        foreach ($residualFile in $residualFiles) {
            Remove-Item -LiteralPath $residualFile.FullName -Force -ErrorAction Stop
        }
        $remainingFiles = @(Get-ChildItem -LiteralPath $transferRoot -Filter "credential-transfer-$Id.*" -ErrorAction SilentlyContinue)
        $privateKeyRemains = -not [string]::IsNullOrWhiteSpace($privateKeyPath) -and
            (Test-Path -LiteralPath $privateKeyPath -PathType Leaf)
        $certificateRemains = @(Get-SourceGatewayTransferCertificates -TransferId $Id `
            -Certificates @(Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My')).Count -ne 0
        if ($certificateRemains -or $privateKeyRemains -or $remainingFiles.Count -ne 0) {
            throw 'One-time transfer material remained after transfer-key cleanup.'
        }
    }

    if ($Operation -eq 'InitializeLabConnection') {
        $bootstrapArguments = @{
            OdbcDsn = $OdbcDsn
            TnsAlias = $TnsAlias
            OracleHost = $OracleHost
            OraclePort = $OraclePort
            OracleSid = $OracleSid
            CommonModulePath = $commonModulePath
        }
        if ($PlanOnly -ceq 'true') { $bootstrapArguments['PlanOnly'] = $true }
        & (Join-Path $bundleRoot 'Initialize-SourceGatewayLabOracleConnection.ps1') @bootstrapArguments
        return
    }

    if ($Operation -eq 'ValidatePrerequisites') {
        if ($null -eq (Get-Service -Name 'OFMSourceGateway' -ErrorAction SilentlyContinue)) {
            throw 'The source gateway must be installed before Oracle credential provisioning.'
        }
        $odbc = Assert-FormsOdbcPrerequisite -Dsn $OdbcDsn -Server $TnsAlias
        Write-AllowlistedResult ([ordered]@{
            schemaVersion = 1
            status = 'prerequisites-verified'
            dsn = $OdbcDsn
            driverName = $odbc.DriverName
            driverPath = $odbc.DriverPath
            driverMachine = $odbc.DriverMachine
            tnsAlias = $odbc.Server
            oracleEndpoint = "${OracleHost}:${OraclePort}/$OracleSid"
            serviceName = 'OFMSourceGateway'
        })
        return
    }
    if ($Operation -eq 'NewTransferKey') {
        if ([string]::IsNullOrWhiteSpace($TransferId)) {
            throw 'Transfer-key creation requires the runner-generated transfer identifier.'
        }
        New-Item -ItemType Directory -Path $transferRoot -Force | Out-Null
        Set-RestrictedPathAcl -Path $transferRoot -Grants @(
            'SYSTEM:(OI)(CI)(F)',
            'Administrators:(OI)(CI)(F)',
            'NT AUTHORITY\LocalService:(OI)(CI)(RX)',
            'NT SERVICE\OFMSourceGateway:(OI)(CI)(RX)'
        )
        $metadataPath = Join-Path $transferRoot "credential-transfer-$TransferId.json"
        $publicCertificatePath = Join-Path $transferRoot "credential-transfer-$TransferId.cer"
        if ((Test-Path -LiteralPath $metadataPath) -or (Test-Path -LiteralPath $publicCertificatePath) -or
            @(Get-SourceGatewayCertificateStoreItems -StorePath 'Cert:\LocalMachine\My' |
                Where-Object { $_.Subject -ceq "CN=OFM Source Gateway Credential Transfer $TransferId" }).Count -ne 0) {
            throw 'Refusing to overwrite existing material for the runner-generated transfer identifier.'
        }
        $certificate = $null
        $privateKey = $null
        $certificateBytes = $null
        try {
            $certificate = New-SelfSignedCertificate -Type Custom `
                -Subject "CN=OFM Source Gateway Credential Transfer $TransferId" `
                -FriendlyName "OFM Source Gateway Credential Transfer $TransferId" `
                -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 `
                -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage KeyEncipherment, DataEncipherment `
                -NotAfter (Get-Date).AddHours(8)
            $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
            if ($privateKey -isnot [Security.Cryptography.RSACng]) {
                throw 'The transfer private key is not in the expected Windows CNG key store.'
            }
            $privateKeyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($privateKey.Key.UniqueName)"
            Grant-PathAccess -Path $privateKeyPath -Identity 'NT AUTHORITY\LocalService' -Rights '(R)'
            Grant-PathAccess -Path $privateKeyPath -Identity 'NT SERVICE\OFMSourceGateway' -Rights '(R)'
            Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Type CERT | Out-Null
            [ordered]@{
                schemaVersion = 1
                transferId = $TransferId
                certificateThumbprint = $certificate.Thumbprint
                publicCertificateFile = [IO.Path]::GetFileName($publicCertificatePath)
                expiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
                privateKeyName = $privateKey.Key.UniqueName
            } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
            $certificateBytes = [IO.File]::ReadAllBytes($publicCertificatePath)
            $certificateSha256 = (Get-FileHash -LiteralPath $publicCertificatePath -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-AllowlistedResult ([ordered]@{
                schemaVersion = 1
                status = 'transfer-key-created'
                transferId = $TransferId
                certificateThumbprint = $certificate.Thumbprint
                expiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
                publicCertificateBase64 = [Convert]::ToBase64String($certificateBytes)
                publicCertificateSha256 = $certificateSha256
            })
        }
        catch {
            if ($null -ne $privateKey) {
                $privateKey.Dispose()
                $privateKey = $null
            }
            if ($null -ne $certificate) {
                $certificate.Dispose()
                $certificate = $null
            }
            Remove-BoundTransferMaterial -Id $TransferId
            throw
        }
        finally {
            if ($null -ne $privateKey) { $privateKey.Dispose() }
            if ($null -ne $certificate) { $certificate.Dispose() }
            if ($null -ne $certificateBytes) { [Array]::Clear($certificateBytes, 0, $certificateBytes.Length) }
        }
        return
    }

    if ($Operation -eq 'CleanupTransferKey') {
        if ([string]::IsNullOrWhiteSpace($TransferId)) {
            throw 'Transfer-key cleanup requires the runner-generated transfer identifier.'
        }
        . (Join-Path $bundleRoot 'SourceGatewayInstaller.Common.ps1')
        Remove-BoundTransferMaterial -Id $TransferId
        Write-AllowlistedResult ([ordered]@{
            schemaVersion = 1
            status = 'transfer-key-removed'
            transferId = $TransferId
        })
        return
    }

    if ([string]::IsNullOrWhiteSpace($CiphertextBase64)) {
        throw 'The completion leg requires the encrypted credential payload.'
    }
    [void](Assert-FormsOdbcPrerequisite -Dsn $OdbcDsn -Server $TnsAlias)
    . (Join-Path $bundleRoot 'SourceGatewayInstaller.Common.ps1')
    $ciphertext = [Convert]::FromBase64String($CiphertextBase64)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $actualCiphertextSha256 = ([BitConverter]::ToString($sha256.ComputeHash($ciphertext))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
        if ($actualCiphertextSha256 -cne $CiphertextSha256) {
            throw 'The encrypted Oracle credential changed digest before completion.'
        }

        $payloadPath = Join-Path $transferRoot "credential-transfer-$TransferId.bin"
        if (Test-Path -LiteralPath $payloadPath) {
            throw 'Refusing to overwrite an existing encrypted credential payload.'
        }
        [IO.File]::WriteAllBytes($payloadPath, $ciphertext)
        Set-RestrictedFileAcl -Path $payloadPath -Grants @('SYSTEM:(F)', 'Administrators:(F)')
        try {
            & (Join-Path $bundleRoot 'Complete-SourceGatewayCredentialTransfer.ps1') `
                -EncryptedPayloadPath $payloadPath `
                -TransferCertificateThumbprint $TransferCertificateThumbprint `
                -VariableName 'OFM_GATEWAY_ORACLE_MERIDIAN_RO' | Out-Null
        }
        finally {
            Remove-BoundTransferMaterial -Id $TransferId
        }
    }
    finally {
        [Array]::Clear($ciphertext, 0, $ciphertext.Length)
    }

    $protectedCredential = Join-Path $programDataRoot 'credentials\OFM_GATEWAY_ORACLE_MERIDIAN_RO.dpapi'
    $service = Get-Service -Name 'OFMSourceGateway' -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $protectedCredential -PathType Leaf) -or $service.Status -ne 'Running') {
        throw 'The protected Oracle credential or running source-gateway service could not be verified.'
    }
    $remainingTransferFiles = @(Get-ChildItem -LiteralPath $transferRoot -Filter "credential-transfer-$TransferId.*" -ErrorAction SilentlyContinue)
    if ($remainingTransferFiles.Count -ne 0) {
        throw 'One-time transfer material remained after credential completion.'
    }
    Write-AllowlistedResult ([ordered]@{
        schemaVersion = 1
        status = 'provisioned'
        variableName = 'OFM_GATEWAY_ORACLE_MERIDIAN_RO'
        username = 'OFM_GATEWAY_RO'
        schema = 'MERIDIAN'
        dsn = $OdbcDsn
        oracleConnectionRegistered = $true
        serviceName = 'OFMSourceGateway'
        serviceStatus = [string]$service.Status
    })
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}