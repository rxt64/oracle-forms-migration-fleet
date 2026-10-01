[CmdletBinding(DefaultParameterSetName = 'Schedule', SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $EncryptedPayloadPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40,64}$')]
    [string] $TransferCertificateThumbprint,

    [ValidatePattern('^OFM_GATEWAY_ORACLE_[A-Z0-9_]+$')]
    [string] $VariableName = 'OFM_GATEWAY_ORACLE_MERIDIAN_RO',

    [string] $InstallRoot = 'C:\Program Files\OracleFormsMigrationFleet\SourceGateway',
    [string] $ProgramDataRoot = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway',

    [Parameter(Mandatory, ParameterSetName = 'Service')]
    [switch] $AsService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'OFMSourceGateway'
$serviceSid = "NT SERVICE\$serviceName"
$serviceAccount = 'NT AUTHORITY\LocalService'
$workerPath = Join-Path $InstallRoot 'OracleFormsMigrationFleet.SourceWorker.exe'
$credentialRoot = Join-Path $ProgramDataRoot 'credentials'
$resultPath = "$EncryptedPayloadPath.result.json"
$normalizedThumbprint = ($TransferCertificateThumbprint -replace '[^A-Fa-f0-9]', '').ToUpperInvariant()
. (Join-Path $PSScriptRoot 'SourceGatewayInstaller.Common.ps1')

if ($AsService) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.User.Value -ne 'S-1-5-19') {
        throw 'The service leg must run as NT AUTHORITY\LocalService.'
    }
    if (-not (Test-Path -LiteralPath $EncryptedPayloadPath -PathType Leaf)) {
        throw 'The encrypted transfer payload was not found.'
    }

    $certificate = Get-ChildItem -Path 'Cert:\LocalMachine\My' |
        Where-Object Thumbprint -eq $normalizedThumbprint |
        Select-Object -First 1
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey -or
        $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw 'The one-time transfer certificate is missing, expired, or has no private key.'
    }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    if ($null -eq $rsa) {
        throw 'The one-time transfer certificate private key could not be opened.'
    }
    $ciphertext = [IO.File]::ReadAllBytes($EncryptedPayloadPath)
    $plaintext = $null
    $characters = $null
    $serviceError = $null
    try {
        $plaintext = $rsa.Decrypt($ciphertext, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
        $characters = [Text.Encoding]::UTF8.GetChars($plaintext)

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $workerPath
        $startInfo.Arguments = "--protect-credential $VariableName"
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $startInfo.EnvironmentVariables['OFM_GATEWAY_PROTECTED_CREDENTIAL_ROOT'] = $credentialRoot

        $process = [Diagnostics.Process]::Start($startInfo)
        try {
            $process.StandardInput.WriteLine($characters)
            $process.StandardInput.Close()
            $process.WaitForExit()
            $exitCode = $process.ExitCode
        }
        finally {
            $process.Dispose()
        }
        if ($exitCode -ne 0) {
            throw "The worker refused credential protection with exit code $exitCode."
        }

    }
    catch {
        $serviceError = $_
    }
    finally {
        if ($null -ne $characters) { [Array]::Clear($characters, 0, $characters.Length) }
        if ($null -ne $plaintext) { [Array]::Clear($plaintext, 0, $plaintext.Length) }
        [Array]::Clear($ciphertext, 0, $ciphertext.Length)
        $rsa.Dispose()
    }
    try {
        Remove-Item -LiteralPath $EncryptedPayloadPath -Force -ErrorAction Stop
    }
    catch {
        if ($null -eq $serviceError) { $serviceError = $_ }
        else {
            $serviceError = [InvalidOperationException]::new(
                "$($serviceError.Exception.Message) Ciphertext cleanup also failed: $($_.Exception.Message)",
                $serviceError.Exception)
        }
    }
    if ($null -ne $serviceError) { throw $serviceError }

    @{ schemaVersion = 1; exitCode = 0; variableName = $VariableName; certificateCleanupRequired = $true } |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    return
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the scheduling leg elevated on the approved Forms VM.'
}
if (-not (Test-Path -LiteralPath $EncryptedPayloadPath -PathType Leaf)) {
    throw 'The encrypted transfer payload was not found.'
}
if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
    throw "$serviceName is not installed at the expected stable content root."
}
if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Provision $VariableName under the $serviceName LocalService identity")) {
    return
}

$transferCertificatePath = "Cert:\LocalMachine\My\$normalizedThumbprint"
$transferPrivateKeyPath = $null
$trustedScriptPath = Join-Path $ProgramDataRoot 'Complete-SourceGatewayCredentialTransfer.ps1'
$trustedCommonPath = Join-Path $ProgramDataRoot 'SourceGatewayInstaller.Common.ps1'
$operationError = $null
try {
    $transferCertificate = Get-Item -LiteralPath $transferCertificatePath -ErrorAction Stop
    try {
        if (-not $transferCertificate.HasPrivateKey -or
            $transferCertificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
            throw 'The one-time transfer certificate is expired or has no private key.'
        }
        $transferPrivateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($transferCertificate)
        try {
            if ($transferPrivateKey -isnot [Security.Cryptography.RSACng]) {
                throw 'The transfer private key is not in the expected Windows CNG key store.'
            }
            $transferPrivateKeyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($transferPrivateKey.Key.UniqueName)"
        }
        finally {
            if ($null -ne $transferPrivateKey) { $transferPrivateKey.Dispose() }
        }
    }
    finally {
        $transferCertificate.Dispose()
    }
    if (-not (Test-Path -LiteralPath $transferPrivateKeyPath -PathType Leaf)) {
        throw 'The one-time transfer private key file was not found.'
    }

    Copy-Item -LiteralPath $PSCommandPath -Destination $trustedScriptPath -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SourceGatewayInstaller.Common.ps1') -Destination $trustedCommonPath -Force
    Set-RestrictedFileAcl -Path $trustedScriptPath -Grants @('SYSTEM:(F)', 'Administrators:(F)', "${serviceAccount}:(RX)")
    Set-RestrictedFileAcl -Path $trustedCommonPath -Grants @('SYSTEM:(F)', 'Administrators:(F)', "${serviceAccount}:(RX)")
    Set-RestrictedPathAcl -Path (Split-Path $EncryptedPayloadPath -Parent) -Grants @(
        'SYSTEM:(OI)(CI)(F)', 'Administrators:(OI)(CI)(F)', "${serviceAccount}:(OI)(CI)(M)"
    )
    Set-RestrictedFileAcl -Path $EncryptedPayloadPath -Grants @(
        'SYSTEM:(F)', 'Administrators:(F)', "${serviceAccount}:(R,D)"
    )

    $taskName = "OFMSourceGateway-Credential-$([guid]::NewGuid().ToString('N'))"
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned',
        '-File', $trustedScriptPath,
        '-EncryptedPayloadPath', $EncryptedPayloadPath,
        '-TransferCertificateThumbprint', $normalizedThumbprint,
        '-VariableName', $VariableName,
        '-InstallRoot', $InstallRoot,
        '-ProgramDataRoot', $ProgramDataRoot,
        '-AsService'
    )
    $encodedArguments = $arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }
    $action = New-ScheduledTaskAction -Execute 'PowerShell.exe' -Argument ($encodedArguments -join ' ')
    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    try {
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
        Start-ScheduledTask -TaskName $taskName
        $deadline = [DateTime]::UtcNow.AddMinutes(5)
        do {
            if (Test-Path -LiteralPath $resultPath -PathType Leaf) { break }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'LocalService credential provisioning timed out.' }
            Start-Sleep -Milliseconds 250
        } while ((Get-ScheduledTask -TaskName $taskName).State -ne 'Ready')

        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName
            throw "LocalService credential provisioning failed with task result $($taskInfo.LastTaskResult)."
        }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.exitCode -ne 0 -or $result.variableName -cne $VariableName -or
            $result.certificateCleanupRequired -ne $true) {
            throw 'LocalService credential provisioning did not return the expected nonsecret result.'
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    }

    Restart-Service -Name $serviceName
}
catch {
    $operationError = $_
}
finally {
    try {
        Remove-TransferCertificateAndKey -CertificatePath $transferCertificatePath -PrivateKeyPath $transferPrivateKeyPath
    }
    catch {
        if ($null -eq $operationError) { $operationError = $_ }
        else {
            $operationError = [InvalidOperationException]::new(
                "$($operationError.Exception.Message) Privileged transfer-key cleanup also failed: $($_.Exception.Message)",
                $operationError.Exception)
        }
    }
}
if ($null -ne $operationError) { throw $operationError }

Write-Output "Provisioned the DPAPI CurrentUser credential for $VariableName under LocalService and restarted $serviceName."
Write-Output 'The one-time ciphertext, certificate, and transfer private key were checked as removed. No plaintext was written or logged.'