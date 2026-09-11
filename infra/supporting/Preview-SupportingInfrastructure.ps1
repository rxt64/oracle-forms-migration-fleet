[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName
)

$ErrorActionPreference = 'Stop'
$requiredEnvironmentVariables = @(
    'SUPPORT_SQL_ENTRA_ADMIN_OBJECT_ID',
    'SUPPORT_SQL_ENTRA_ADMIN_LOGIN'
)

foreach ($variableName in $requiredEnvironmentVariables) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($variableName))) {
        throw "Required environment variable '$variableName' is not set."
    }
}

$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$parameterFile = Join-Path $PSScriptRoot 'dev.bicepparam'

& az bicep build --file $templateFile --stdout *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Local Bicep compilation failed.'
}

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = [System.Diagnostics.ProcessStartInfo]@{
    FileName = 'az'
    UseShellExecute = $false
    RedirectStandardOutput = $true
    RedirectStandardError = $true
    CreateNoWindow = $true
}

@(
    'deployment', 'group', 'what-if',
    '--resource-group', $ResourceGroupName,
    '--template-file', $templateFile,
    '--parameters', $parameterFile,
    '--result-format', 'ResourceIdOnly',
    '--output', 'json',
    '--only-show-errors'
) | ForEach-Object { [void] $process.StartInfo.ArgumentList.Add($_) }

[void] $process.Start()
$standardOutput = $process.StandardOutput.ReadToEnd()
[void] $process.StandardError.ReadToEnd()
$process.WaitForExit()

if ($process.ExitCode -ne 0) {
    throw "Azure Resource Manager what-if failed with exit code $($process.ExitCode). Details were suppressed to avoid exposing environment values."
}

$whatIf = $standardOutput | ConvertFrom-Json -Depth 100
$changes = @($whatIf.changes)
if ($changes.Count -eq 0) {
    Write-Output 'What-if completed: no resource changes.'
    exit 0
}

Write-Output 'What-if completed. Sanitized resource changes:'
foreach ($change in $changes) {
    $segments = $change.resourceId -split '/'
    $providerIndex = [Array]::IndexOf($segments, 'providers')
    $resourceType = if ($providerIndex -ge 0 -and $segments.Length -gt ($providerIndex + 2)) {
        "$($segments[$providerIndex + 1])/$($segments[$providerIndex + 2])"
    } else {
        'Unknown'
    }
    $resourceName = if ($segments.Length -gt 0) { $segments[-1] } else { 'Unknown' }
    Write-Output "[$($change.changeType)] $resourceType/$resourceName"
}