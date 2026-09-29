param([ValidateSet('Start', 'Install', 'Validate')][string]$Action = 'Start')
$ErrorActionPreference = 'Stop'
if ($Action -eq 'Validate') { 'Oracle startup script parameters valid.'; return }
if ($env:COMPUTERNAME -ne 'OFMORADB') { throw 'Wrong database host.' }
$root = 'C:\OracleLab\Operations'
$destination = Join-Path $root 'Start-Oracle9iSourceLab.ps1'
if ($Action -eq 'Install') {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier $sid
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $root -AclObject $acl
    if ($PSCommandPath -ne $destination) { Copy-Item -LiteralPath $PSCommandPath -Destination $destination -Force }
    $taskAction = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -File "' + $destination + '"')
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User ($env:COMPUTERNAME + '\ofmlabadmin')
    $principal = New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME + '\ofmlabadmin') -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName OFM-StartOracle9i -Action $taskAction -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName OFM-StartOracle9i
    'Oracle startup task installed for the existing administrator desktop session.'
    return
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
Start-Transcript -Path (Join-Path $root 'startup.log') -Force | Out-Null
try {
    $oracleHome = 'C:\oracle\ora90'
    $env:ORACLE_HOME = $oracleHome
    $env:ORACLE_SID = 'orcl'
    $env:TNS_ADMIN = Join-Path $oracleHome 'network\admin'
    $env:PATH = (Join-Path $oracleHome 'bin') + ';' + $env:PATH
    $hash = (Get-FileHash (Join-Path $oracleHome 'bin\oranipc9.dll')).Hash
    if ($hash -ne 'A7C0E87DFFEBDBF3EEEB7DD1CC723993D75306761894A444C82C5087020FF95E') { throw 'Listener compatibility DLL does not match the verified lab patch.' }
    function Invoke-LocalSql([string]$Statement) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = Join-Path $oracleHome 'bin\sqlplus.exe'
        $info.Arguments = '/nolog'
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $client = New-Object Diagnostics.Process
        $client.StartInfo = $info
        [void]$client.Start()
        $output = $client.StandardOutput.ReadToEndAsync()
        $errors = $client.StandardError.ReadToEndAsync()
        $client.StandardInput.WriteLine('whenever sqlerror exit failure')
        $client.StandardInput.WriteLine('connect / as sysdba')
        $client.StandardInput.WriteLine($Statement)
        $client.StandardInput.WriteLine('exit')
        $client.StandardInput.Close()
        if (!$client.WaitForExit(60000)) { $client.Kill(); $client.WaitForExit(); throw 'Oracle startup SQL timed out.' }
        return [pscustomobject]@{ Code = $client.ExitCode; Text = $output.Result + $errors.Result }
    }
    $state = Invoke-LocalSql 'select status from v$instance;'
    $state.Text
    if ($state.Code -ne 0 -and $state.Text -match 'ORA-01034') {
        $startup = Invoke-LocalSql "startup pfile='C:\oracle\ora90\database\initorcl.ora';"
        $startup.Text
        if ($startup.Code -ne 0) { throw 'Database startup failed; no force or recovery attempted.' }
        $state = Invoke-LocalSql 'select status from v$instance;'
        $state.Text
    }
    if ($state.Code -ne 0 -or $state.Text -notmatch '(?m)^OPEN\s*$') { throw 'Database is not OPEN.' }
    $listenerPort = Get-NetTCPConnection -LocalPort 1521 -State Listen -ErrorAction SilentlyContinue
    if ($listenerPort) {
        $listener = Get-Process -Id $listenerPort[0].OwningProcess
        if ($listener.Path -ne (Join-Path $oracleHome 'bin\tnslsnr.exe')) { throw 'Port 1521 belongs to an unexpected process.' }
        if ($listener.SessionId -ne (Get-Process -Id $PID).SessionId) {
            Stop-Process -Id $listener.Id -Force
            $listener.WaitForExit()
            $listenerPort = $null
        }
    }
    if (!$listenerPort) {
        $listener = Start-Process (Join-Path $oracleHome 'bin\tnslsnr.exe') -ArgumentList LISTENER -WindowStyle Hidden -PassThru
        'StartedListenerPid=' + $listener.Id
    } else {
        $listener = Get-Process -Id $listenerPort[0].OwningProcess
        if ($listener.ProcessName -ne 'tnslsnr' -or $listener.SessionId -ne (Get-Process -Id $PID).SessionId) { throw 'Listener is in another session or the port belongs to another process.' }
        'ExistingListenerPid=' + $listener.Id
    }
    'ORACLE9I-STARTUP-VERIFIED-OPEN'
} finally { Stop-Transcript | Out-Null }