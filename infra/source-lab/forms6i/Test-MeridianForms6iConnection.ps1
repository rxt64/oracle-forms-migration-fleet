param(
    [string]$CredentialPath = 'C:\OracleForms6iSourceLab\connection\meridian.dpapi',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if ($ValidateOnly) { 'Connection probe parameters valid.'; return }
if ($env:COMPUTERNAME -ne 'OFMFORMS6I') { throw 'Wrong Forms host.' }
Add-Type -AssemblyName System.Security
$plain = $null
$credential = $null
try {
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($CredentialPath), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $credential = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
    if ($credential.User -cne 'MERIDIAN' -or [string]::IsNullOrEmpty($credential.Password) -or $credential.Password -match '[\s"@/\\]') { throw 'Unsupported credential format.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = 'C:\orant\bin\plus80.exe'
    $info.Arguments = '-s /nolog'
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['TNS_ADMIN'] = Split-Path $CredentialPath
    $info.EnvironmentVariables['PATH'] = 'C:\orant\bin;' + $env:PATH
    $client = New-Object Diagnostics.Process
    $client.StartInfo = $info
    [void]$client.Start()
    $output = $client.StandardOutput.ReadToEndAsync()
    $errors = $client.StandardError.ReadToEndAsync()
    $client.StandardInput.WriteLine('whenever oserror exit failure')
    $client.StandardInput.WriteLine('whenever sqlerror exit failure')
    $client.StandardInput.WriteLine('connect MERIDIAN/' + $credential.Password + '@OFM9I')
    $client.StandardInput.WriteLine('set linesize 200 pagesize 100')
    $client.StandardInput.WriteLine('select user from dual;')
    $client.StandardInput.WriteLine('select count(*) customers from customers;')
    $client.StandardInput.WriteLine('select product_id,product_code,unit_price from products order by product_id;')
    $client.StandardInput.WriteLine('select order_id,customer_id,status,total_amount from orders order by order_id;')
    $client.StandardInput.WriteLine('select order_id,line_no,product_id,quantity,unit_price from order_items order by order_id,line_no;')
    $client.StandardInput.WriteLine('exit')
    $client.StandardInput.Close()
    if (!$client.WaitForExit(30000)) { $client.Kill(); $client.WaitForExit(); throw 'Net8 connection probe timed out.' }
    $report = (Get-Date).ToUniversalTime().ToString('o') + "`r`n" + $output.Result.Replace($credential.Password, '[redacted]') + $errors.Result.Replace($credential.Password, '[redacted]') + "`r`nNet8Exit=" + $client.ExitCode
    $evidenceRoot = 'C:\OracleForms6iSourceLab\evidence\runtime'
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    $report | Set-Content (Join-Path $evidenceRoot ('net8-' + (Get-Date -Format yyyyMMdd-HHmmssfff) + '.txt'))
    $report
    if ($client.ExitCode -ne 0) { throw 'Net8 SQL probe failed.' }
} finally {
    if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
    $credential = $null
}