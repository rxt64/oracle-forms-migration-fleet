param(
    [ValidateSet('Schedule', 'Inspect', 'Read', 'Validate')][string]$Action = 'Schedule',
    [ValidateSet('Inspect', 'Create', 'Save', 'Cancel', 'FocusQuantity', 'Maximize')][string]$Operation = 'Inspect'
)
$ErrorActionPreference = 'Stop'
if ($Action -eq 'Validate') { 'Runtime probe parameters valid.'; return }
if ($env:COMPUTERNAME -ne 'OFMFORMS6I') { throw 'Wrong Forms host.' }
$root = 'C:\OracleForms6iSourceLab\evidence\runtime'
$resultPath = Join-Path $root 'controls.json'
if ($Action -eq 'Read') {
    if (!(Test-Path $resultPath)) { throw 'No completed runtime inspection.' }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    $result | Select-Object Timestamp, Error, Operation | ConvertTo-Json -Compress
    $result.Controls | Select-Object Name, Class, Handle, Bounds | ConvertTo-Json -Compress
    if ($result.Error) { throw $result.Error }
    return
}
if ($Action -eq 'Schedule') {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $destination = Join-Path $root 'Test-MeridianForms6iRuntime.ps1'
    if ($PSCommandPath -ne $destination) { Copy-Item $PSCommandPath $destination -Force }
    if (Test-Path $resultPath) { Copy-Item $resultPath (Join-Path $root 'previous-controls.json') -Force }
    Remove-Item $resultPath -ErrorAction SilentlyContinue
    $taskAction = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -WindowStyle Hidden -File "' + $destination + '" -Action Inspect -Operation ' + $Operation)
    $principal = New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME + '\ofmlabadmin') -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName OFM-InspectMeridianForms6i -Action $taskAction -Principal $principal -Force | Out-Null
    Start-ScheduledTask OFM-InspectMeridianForms6i
    'Runtime inspection scheduled.'
    return
}
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MeridianRuntimeWindow
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter, IntPtr data, uint flags, uint timeout, out IntPtr result);
}
'@
    $runtime = @(Get-Process ifrun60 -ErrorAction Stop)
    if ($runtime.Count -ne 1) { throw 'Expected exactly one Forms runtime.' }
    if ($Operation -eq 'Maximize') {
        [void][MeridianRuntimeWindow]::ShowWindow($runtime[0].MainWindowHandle, 3)
    } elseif ($Operation -ne 'Inspect') {
        $labels = @{ Create = 'Create Draft'; Save = 'Save'; Cancel = 'Cancel Draft' }
        $prior = Get-Content (Join-Path $root 'previous-controls.json') -Raw | ConvertFrom-Json
        $label = if ($Operation -eq 'FocusQuantity') { 'Create Draft' } else { $labels[$Operation] }
        $button = @($prior.Controls | Where-Object { $_.Class -eq 'Button' -and $_.Name -eq $label -and $_.ProcessId -eq $runtime[0].Id })
        if ($button.Count -ne 1) { throw 'Expected exactly one matching runtime button.' }
        $messageResult = [IntPtr]::Zero
        if ($Operation -eq 'FocusQuantity') {
            [void][MeridianRuntimeWindow]::ShowWindow($runtime[0].MainWindowHandle, 3)
            [void][MeridianRuntimeWindow]::SetForegroundWindow($runtime[0].MainWindowHandle)
            $canvas = [MeridianRuntimeWindow]::GetParent([IntPtr]$button[0].Handle)
            $scale = [double]($button[0].Bounds -split ',')[2] / 130
            $location = (([int](171 * $scale)) -shl 16) -bor ([int](210 * $scale))
            [void][MeridianRuntimeWindow]::SendMessageTimeout($canvas, 0x201, [IntPtr]1, [IntPtr]$location, 2, 5000, [ref]$messageResult)
            $sent = [MeridianRuntimeWindow]::SendMessageTimeout($canvas, 0x202, [IntPtr]::Zero, [IntPtr]$location, 2, 5000, [ref]$messageResult)
        } else {
            $sent = [MeridianRuntimeWindow]::SendMessageTimeout([IntPtr]$button[0].Handle, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero, 2, 5000, [ref]$messageResult)
        }
        if ($sent -eq [IntPtr]::Zero) { throw 'Runtime button did not respond.' }
    }
    $controls = @()
    foreach ($process in $runtime) {
        $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
        foreach ($window in $windows) {
            $elements = @($window) + @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition))
            foreach ($element in $elements) {
                if ($element.Current.IsPassword) { continue }
                $value = $null
                $pattern = $null
                if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $value = $pattern.Current.Value }
                $controls += [pscustomobject]@{
                    ProcessId = $process.Id
                    SessionId = $process.SessionId
                    Name = $element.Current.Name
                    Class = $element.Current.ClassName
                    Type = $element.Current.ControlType.ProgrammaticName
                    AutomationId = $element.Current.AutomationId
                    Handle = $element.Current.NativeWindowHandle
                    Enabled = $element.Current.IsEnabled
                    Value = $value
                    Bounds = $element.Current.BoundingRectangle.ToString()
                    Patterns = @($element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName })
                }
            }
        }
    }
    $result = [pscustomobject]@{ Timestamp = (Get-Date).ToString('o'); Controls = $controls; Error = $null; Operation = $Operation }
} catch {
    $result = [pscustomobject]@{ Timestamp = (Get-Date).ToString('o'); Controls = @(); Error = $_.Exception.Message; Operation = $Operation }
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding UTF8
Copy-Item $resultPath (Join-Path $root ('controls-' + (Get-Date -Format yyyyMMdd-HHmmssfff) + '-' + $Operation + '.json'))
if ($result.Error) { throw $result.Error }