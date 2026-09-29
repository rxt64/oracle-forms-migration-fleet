param(
    [ValidateSet('Launch', 'LaunchDesktop', 'Install', 'Validate')][string]$Action = 'Launch',
    [string]$OracleHome = 'C:\orant',
    [string]$ModulePath = 'C:\OracleForms6iSourceLab\app\generated\MRD_ORDER_ENTRY.fmx',
    [string]$CredentialPath = 'C:\OracleForms6iSourceLab\connection\meridian.dpapi'
)
$ErrorActionPreference = 'Stop'
if ($Action -eq 'Validate') { 'Launcher parameters valid.'; return }
if ($env:COMPUTERNAME -ne 'OFMFORMS6I') { throw 'Wrong Forms host.' }
if (!(Test-Path -LiteralPath $ModulePath)) { throw 'Compiled Meridian form is missing.' }
if ($Action -eq 'LaunchDesktop') {
    $destination = Join-Path (Split-Path $CredentialPath) 'Start-MeridianForms6i.ps1'
    if (!(Test-Path -LiteralPath $destination)) { throw 'Install the launcher first.' }
    $taskAction = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -WindowStyle Hidden -File "' + $destination + '" -OracleHome "' + $OracleHome + '" -ModulePath "' + $ModulePath + '" -CredentialPath "' + $CredentialPath + '"')
    $principal = New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME + '\ofmlabadmin') -LogonType Interactive
    Register-ScheduledTask -TaskName OFM-MeridianForms6i -Action $taskAction -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName OFM-MeridianForms6i
    'Meridian launch requested in the existing desktop session.'
    return
}
if ($Action -eq 'Install') {
    $destination = Join-Path (Split-Path $CredentialPath) 'Start-MeridianForms6i.ps1'
    if ($PSCommandPath -ne $destination) { Copy-Item -LiteralPath $PSCommandPath -Destination $destination -Force }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Meridian Order Entry.lnk'))
    $shortcut.TargetPath = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
    $shortcut.Arguments = '-NoProfile -WindowStyle Hidden -File "' + $destination + '" -OracleHome "' + $OracleHome + '" -ModulePath "' + $ModulePath + '" -CredentialPath "' + $CredentialPath + '"'
    $shortcut.WorkingDirectory = Split-Path $ModulePath
    $shortcut.IconLocation = Join-Path $OracleHome 'bin\ifrun60.exe'
    $shortcut.Save()
    'Meridian desktop shortcut installed without embedded credentials.'
    return
}
Add-Type -AssemblyName System.Security
$plain = $null
$credential = $null
try {
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($CredentialPath), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $credential = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
    if ($credential.User -cne 'MERIDIAN' -or [string]::IsNullOrEmpty($credential.Password) -or $credential.Password -match '[\s"@/\\]') { throw 'Unsupported credential format.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $OracleHome 'bin\ifrun60.exe'
    $info.WorkingDirectory = Split-Path $ModulePath
    $info.UseShellExecute = $false
    $info.Arguments = 'module="' + $ModulePath + '" userid=MERIDIAN/' + $credential.Password + '@OFM9I'
    $info.EnvironmentVariables['ORACLE_HOME'] = $OracleHome
    $info.EnvironmentVariables['PATH'] = (Join-Path $OracleHome 'bin') + ';' + $env:PATH
    $info.EnvironmentVariables['TNS_ADMIN'] = Split-Path $CredentialPath
    $runtime = [Diagnostics.Process]::Start($info)
    'RuntimeProcessId=' + $runtime.Id
} finally {
    if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
    if ($info) { $info.Arguments = '' }
    $credential = $null
}