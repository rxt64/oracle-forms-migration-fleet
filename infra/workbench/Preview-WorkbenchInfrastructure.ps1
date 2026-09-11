#requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName
)

$ErrorActionPreference = 'Stop'
$templateFile = Join-Path $PSScriptRoot 'main.bicep'
$parameterFile = Join-Path $PSScriptRoot 'dev.bicepparam'
$compiledParameterFile = Join-Path ([System.IO.Path]::GetTempPath()) "oracle-forms-migration-fleet-$([Guid]::NewGuid().ToString('N')).parameters.json"

& az bicep build --file $templateFile --stdout *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Local Bicep compilation failed.'
}

try {
    & az bicep build-params --file $parameterFile --outfile $compiledParameterFile *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Local Bicep parameter compilation failed.'
    }

    $raw = & az deployment group what-if `
        --name 'preview-migration-fleet-workbench' `
        --resource-group $ResourceGroupName `
        --template-file $templateFile `
        --parameters $compiledParameterFile deployWorkbench=false `
        --result-format ResourceIdOnly `
        --no-pretty-print `
        --output json `
        --only-show-errors 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw 'Azure Resource Manager what-if failed. Raw output was suppressed to avoid exposing environment details.'
    }

    $whatIf = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
    $changes = @($whatIf.changes)
    if ($changes.Count -eq 0) {
        Write-Output 'What-if completed: no resource changes.'
    }
    else {
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
    }
}
finally {
    Remove-Item -LiteralPath $compiledParameterFile -Force -ErrorAction SilentlyContinue
}