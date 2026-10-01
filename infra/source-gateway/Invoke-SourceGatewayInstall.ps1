[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^https://github\.com/rxt64/oracle-forms-migration-fleet/releases/download/source-gateway-install-[0-9]+-[0-9]+-[0-9a-f]{40}/source-gateway-install-[0-9]+-[0-9]+-[0-9a-f]{40}\.zip$')]
    [string] $BundleUrl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $BundleSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedMainCommitSha,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $TrustedCiRunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$maximumBundleBytes = 157286400
$maximumExpandedBytes = 209715200
$gatewayApplicationClientId = [guid]'b16b4127-9ef6-44a1-9f07-bbfe92053baf'
$expectedNames = @(
    'OracleFormsMigrationFleet.SourceWorker.exe',
    'manifest.json',
    'ci-proof.json',
    'Install-SourceGateway.ps1',
    'SourceGatewayInstaller.Common.ps1',
    'source-registry.json'
)
$stagingRoot = Join-Path $env:ProgramData "OracleFormsMigrationFleet\SourceGateway\transport-$([guid]::NewGuid().ToString('N'))"
$bundlePath = Join-Path $stagingRoot 'source-gateway-install.zip'
$extractRoot = Join-Path $stagingRoot 'payload'
$result = [ordered]@{
    status = 'failed'
    commit = $ExpectedMainCommitSha
    ciRunId = $TrustedCiRunId
    bundleSha256 = $BundleSha256
    serviceName = 'OFMSourceGateway'
    serviceStatus = $null
    publicRootCertificateBase64 = $null
    publicRootCertificateSha256 = $null
}
$exitCode = 1

function Test-AllowedDownloadUri([Uri] $Uri, [bool] $Initial) {
    if ($Uri.Scheme -cne 'https' -or -not $Uri.IsDefaultPort -or -not [string]::IsNullOrEmpty($Uri.UserInfo)) {
        return $false
    }
    if ($Initial) {
        return $Uri.AbsoluteUri -ceq $BundleUrl
    }
    return $Uri.Host -ceq 'release-assets.githubusercontent.com'
}

try {
    if ($BundleUrl -notmatch [regex]::Escape($ExpectedMainCommitSha)) {
        throw 'The release URL is not bound to the expected main commit.'
    }

    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    try {
        $current = [Uri]$BundleUrl
        for ($redirects = 0; $redirects -le 5; $redirects++) {
            if (-not (Test-AllowedDownloadUri -Uri $current -Initial ($redirects -eq 0))) {
                throw 'The bundle download attempted to leave the approved GitHub release asset hosts.'
            }
            $response = $client.GetAsync($current, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
                    if ($null -eq $response.Headers.Location -or $redirects -eq 5) {
                        throw 'The bundle download returned an invalid or excessive redirect chain.'
                    }
                    $current = [Uri]::new($current, $response.Headers.Location)
                    continue
                }
                if (-not $response.IsSuccessStatusCode) {
                    throw "The public release asset returned HTTP $([int]$response.StatusCode)."
                }
                if ($null -ne $response.Content.Headers.ContentLength -and
                    $response.Content.Headers.ContentLength -gt $maximumBundleBytes) {
                    throw 'The public release asset exceeds the transport size limit.'
                }

                $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $output = [IO.File]::Create($bundlePath)
                try {
                    $buffer = New-Object byte[] 1048576
                    [long]$written = 0
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $written += $read
                        if ($written -gt $maximumBundleBytes) {
                            throw 'The public release asset exceeded the transport size limit while downloading.'
                        }
                        $output.Write($buffer, 0, $read)
                    }
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
                break
            }
            finally {
                $response.Dispose()
            }
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $actualBundleSha = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualBundleSha -cne $BundleSha256) {
        throw 'The downloaded release asset does not match the authenticated outer SHA-256.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($bundlePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        $unexpected = @($archive.Entries | Where-Object {
                $_.FullName -match '[/\\:]' -or $expectedNames -cnotcontains $_.FullName
            })
        if ($archive.Entries.Count -ne $expectedNames.Count -or $unexpected.Count -ne 0 -or
            @($entryNames | Select-Object -Unique).Count -ne $expectedNames.Count) {
            throw 'The release asset does not contain exactly the approved flat payload entries.'
        }
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $expandedBytes += $entry.Length
            if ($entry.Length -gt $maximumBundleBytes -or $expandedBytes -gt $maximumExpandedBytes) {
                throw 'The release asset exceeds the expanded payload size limit.'
            }
            $destination = Join-Path $extractRoot $entry.FullName
            $sourceStream = $entry.Open()
            $destinationStream = [IO.File]::Create($destination)
            try {
                $buffer = New-Object byte[] 1048576
                [long]$entryBytes = 0
                while (($read = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $entryBytes += $read
                    if ($entryBytes -gt $entry.Length -or $entryBytes -gt $maximumBundleBytes) {
                        throw 'A release asset entry exceeded its declared or allowed expanded size.'
                    }
                    $destinationStream.Write($buffer, 0, $read)
                }
                if ($entryBytes -ne $entry.Length) {
                    throw 'A release asset entry did not match its declared expanded size.'
                }
            }
            finally {
                $destinationStream.Dispose()
                $sourceStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $installerPath = Join-Path $extractRoot 'Install-SourceGateway.ps1'
    $null = & $installerPath -ExpectedMainCommitSha $ExpectedMainCommitSha -TrustedCiRunId $TrustedCiRunId `
        -GatewayApplicationClientId $gatewayApplicationClientId -OfflineBundleRoot $extractRoot -Confirm:$false

    $service = Get-Service -Name 'OFMSourceGateway' -ErrorAction Stop
    if ($service.Status -ne 'Running') {
        throw 'The source-gateway service did not reach Running state.'
    }
    $rootCerPath = 'C:\ProgramData\OracleFormsMigrationFleet\SourceGateway\trust\ofm-source-gateway-dev-root-2026.cer'
    $rootBytes = [IO.File]::ReadAllBytes($rootCerPath)
    $result.status = 'installed'
    $result.serviceStatus = $service.Status.ToString()
    $result.publicRootCertificateBase64 = [Convert]::ToBase64String($rootBytes)
    $result.publicRootCertificateSha256 = (Get-FileHash -LiteralPath $rootCerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $exitCode = 0
}
catch {
    $exitCode = 1
}
finally {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $stagingRoot) {
        $result.status = 'failed'
        $result.serviceStatus = $null
        $result.publicRootCertificateBase64 = $null
        $result.publicRootCertificateSha256 = $null
        $exitCode = 1
    }
    Write-Output ($result | ConvertTo-Json -Compress)
}

exit $exitCode
