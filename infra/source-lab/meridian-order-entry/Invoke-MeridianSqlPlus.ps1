[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SubscriptionId,

    [Parameter(Mandatory)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory)]
    [string]$ContainerAppName,

    [Parameter(Mandatory)]
    [string]$SqlPath,

    [string]$ContainerName = 'oracle-free',

    [ValidateSet('SqlPlus', 'Raw')]
    [string]$InputMode = 'SqlPlus',

    [string]$ExecCommand,

    [string]$TranscriptPath,

    [ValidateRange(1, 30)]
    [int]$TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$azCommand = Get-Command az -ErrorAction Stop
$azExecutable = $azCommand.Source
$azPrefix = @()
if ([System.IO.Path]::GetExtension($azExecutable) -eq '.cmd') {
    $azCliRoot = Split-Path (Split-Path $azExecutable -Parent) -Parent
    $azPython = Join-Path $azCliRoot 'python.exe'
    if (Test-Path -LiteralPath $azPython -PathType Leaf) {
        $azExecutable = $azPython
        $azPrefix = @('-IBm', 'azure.cli')
    }
}

if (-not (Test-Path -LiteralPath $SqlPath -PathType Leaf)) {
    throw "SQL input does not exist: $SqlPath"
}
if ([string]::IsNullOrWhiteSpace($ExecCommand)) {
    $ExecCommand = if ($InputMode -eq 'SqlPlus') { 'sqlplus / as sysdba' } else { 'bash -s' }
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $stderrPath = Join-Path ([System.IO.Path]::GetTempPath()) ('ofm-az-stderr-' + [guid]::NewGuid().ToString('N') + '.log')
    try {
        $raw = & $azExecutable @azPrefix @Arguments -o json 2>$stderrPath
        if ($LASTEXITCODE -ne 0) {
            throw (Get-Content -LiteralPath $stderrPath -Raw)
        }

        return ($raw | Out-String | ConvertFrom-Json)
    }
    finally {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Send-WebSocketBytes {
    param(
        [Parameter(Mandatory)][System.Net.WebSockets.ClientWebSocket]$Socket,
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][System.Threading.CancellationToken]$CancellationToken
    )

    $segment = [ArraySegment[byte]]::new($Bytes)
    [void]$Socket.SendAsync(
        $segment,
        [System.Net.WebSockets.WebSocketMessageType]::Binary,
        $true,
        $CancellationToken).GetAwaiter().GetResult()
}

function Receive-ExecText {
    param(
        [Parameter(Mandatory)][System.Net.WebSockets.ClientWebSocket]$Socket,
        [Parameter(Mandatory)][byte[]]$Buffer,
        [Parameter(Mandatory)][System.Threading.CancellationToken]$CancellationToken,
        [Parameter(Mandatory)][System.Text.StringBuilder]$Capture,
        [string]$OutputPath
    )

    $message = [System.IO.MemoryStream]::new()
    try {
        do {
            $segment = [ArraySegment[byte]]::new($Buffer)
            $result = $Socket.ReceiveAsync($segment, $CancellationToken).GetAwaiter().GetResult()
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                return [pscustomobject]@{ Closed = $true; Text = '' }
            }

            $message.Write($Buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)

        $bytes = $message.ToArray()
        if ($bytes.Length -lt 2) {
            return [pscustomobject]@{ Closed = $false; Text = '' }
        }

        if ($bytes[0] -eq 1) {
            return [pscustomobject]@{ Closed = $false; Text = '' }
        }
        if ($bytes[0] -eq 2) {
            throw [System.Text.Encoding]::UTF8.GetString($bytes, 1, $bytes.Length - 1)
        }
        if ($bytes[0] -ne 0 -or ($bytes[1] -ne 1 -and $bytes[1] -ne 2)) {
            throw "Unexpected Container Apps exec frame: proxy=$($bytes[0]), stream=$($bytes[1])."
        }

        $text = [System.Text.Encoding]::UTF8.GetString($bytes, 2, $bytes.Length - 2)
        [void]$Capture.Append($text)
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            Add-Content -LiteralPath $OutputPath -Value $text -Encoding utf8NoBOM -NoNewline
        }
        Write-Output -NoEnumerate ([pscustomobject]@{ Closed = $false; Text = $text })
    }
    finally {
        $message.Dispose()
    }
}

function Wait-ForSqlPlusPrompt {
    param(
        [Parameter(Mandatory)][System.Net.WebSockets.ClientWebSocket]$Socket,
        [Parameter(Mandatory)][byte[]]$Buffer,
        [Parameter(Mandatory)][System.Threading.CancellationToken]$CancellationToken,
        [Parameter(Mandatory)][System.Text.StringBuilder]$Capture,
        [string]$OutputPath
    )

    $observed = ''
    while ($true) {
        $received = Receive-ExecText `
            -Socket $Socket `
            -Buffer $Buffer `
            -CancellationToken $CancellationToken `
            -Capture $Capture `
            -OutputPath $OutputPath
        if ($received.Closed) {
            throw 'Container Apps exec closed before SQL*Plus acknowledged the input line.'
        }

        $observed += $received.Text
        if ($observed.Length -gt 1MB) {
            $observed = $observed.Substring($observed.Length - 1MB)
        }

        $normalized = $observed.Replace("`r`n", "`n")
        $promptObserved = $normalized -match '(?:SQL> | {0,2}\d+ {2})$'
        if ($promptObserved) {
            return
        }
    }
}

$app = Invoke-AzJson -Arguments @(
    'containerapp', 'show',
    '--subscription', $SubscriptionId,
    '--resource-group', $ResourceGroupName,
    '--name', $ContainerAppName)
$revision = [string]$app.properties.latestReadyRevisionName

$replicas = Invoke-AzJson -Arguments @(
    'containerapp', 'replica', 'list',
    '--subscription', $SubscriptionId,
    '--resource-group', $ResourceGroupName,
    '--name', $ContainerAppName,
    '--revision', $revision)
$replica = @($replicas) |
    Where-Object { $_.properties.runningState -eq 'Running' } |
    Select-Object -First 1
if ($null -eq $replica) {
    throw "Revision '$revision' has no running replica."
}

$replicaUrl = "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.App/containerApps/$ContainerAppName/revisions/$revision/replicas/$($replica.name)?api-version=2023-11-02-preview"
$replicaDetail = Invoke-AzJson -Arguments @('rest', '--method', 'get', '--url', $replicaUrl)
$container = @($replicaDetail.properties.containers) | Where-Object name -EQ $ContainerName
if ($container.Count -ne 1 -or [string]::IsNullOrWhiteSpace($container[0].logStreamEndpoint)) {
    throw "Container '$ContainerName' has no usable log stream endpoint."
}

$tokenUrl = "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.App/containerApps/$ContainerAppName/getAuthToken?api-version=2023-11-02-preview"
$auth = Invoke-AzJson -Arguments @('rest', '--method', 'post', '--url', $tokenUrl)
if ([string]::IsNullOrWhiteSpace($auth.properties.token)) {
    throw 'Azure Container Apps did not return an exec token.'
}

$endpoint = [uri]$container[0].logStreamEndpoint
$command = [uri]::EscapeDataString($ExecCommand)
$webSocketUri = [uri](
    "wss://$($endpoint.Host)/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName" +
    "/containerApps/$ContainerAppName/revisions/$revision/replicas/$($replica.name)" +
    "/containers/$ContainerName/exec?command=$command")

$cancellation = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes($TimeoutMinutes))
$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$socket.Options.SetRequestHeader('Authorization', "Bearer $($auth.properties.token)")

try {
    [void]$socket.ConnectAsync($webSocketUri, $cancellation.Token).GetAwaiter().GetResult()

    $receiveBuffer = [byte[]]::new(65536)
    $capturedText = [System.Text.StringBuilder]::new()
    if ($InputMode -eq 'SqlPlus') {
        Wait-ForSqlPlusPrompt `
            -Socket $socket `
            -Buffer $receiveBuffer `
            -CancellationToken $cancellation.Token `
            -Capture $capturedText `
            -OutputPath $TranscriptPath
    }

    $sqlLines = @(Get-Content -LiteralPath $SqlPath)
    if ($InputMode -eq 'SqlPlus') {
        $lastContentLine = $sqlLines |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Last 1
        if ($null -eq $lastContentLine -or $lastContentLine -notmatch '^\s*EXIT(?:\s|;|$)') {
            $sqlLines += 'EXIT SUCCESS'
        }
    }
    for ($lineIndex = 0; $lineIndex -lt $sqlLines.Count; $lineIndex++) {
        $line = [string]$sqlLines[$lineIndex]
        $lineBytes = [System.Text.Encoding]::UTF8.GetBytes($line + "`n")
        if ($lineBytes.Length -gt 1024) {
            throw "SQL input line $($lineIndex + 1) exceeds the 1024-byte transport limit."
        }

        $stdinBytes = [byte[]]::new($lineBytes.Length + 2)
        $stdinBytes[0] = 0
        $stdinBytes[1] = 0
        [Array]::Copy($lineBytes, 0, $stdinBytes, 2, $lineBytes.Length)
        Send-WebSocketBytes -Socket $socket -Bytes $stdinBytes -CancellationToken $cancellation.Token

        if ($InputMode -eq 'SqlPlus' -and $lineIndex -lt $sqlLines.Count - 1) {
            Wait-ForSqlPlusPrompt `
                -Socket $socket `
                -Buffer $receiveBuffer `
                -CancellationToken $cancellation.Token `
                -Capture $capturedText `
                -OutputPath $TranscriptPath
        }
    }

    while ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $received = Receive-ExecText `
            -Socket $socket `
            -Buffer $receiveBuffer `
            -CancellationToken $cancellation.Token `
            -Capture $capturedText `
            -OutputPath $TranscriptPath
        if ($received.Closed) {
            break
        }
    }

    Write-Output $capturedText.ToString()
}
finally {
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        [void]$socket.CloseAsync(
            [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
            'SQL completed',
            [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    $socket.Dispose()
    $cancellation.Dispose()
}
