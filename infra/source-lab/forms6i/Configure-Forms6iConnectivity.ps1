param(
    [ValidateSet('PrepareCredential', 'StoreCredential', 'ConfigureServer')][string]$Action,
    [string]$EncryptedCredential
)
$ErrorActionPreference = 'Stop'
if ($Action -eq 'ConfigureServer') {
    if ($env:COMPUTERNAME -ne 'OFMORADB') { throw 'Wrong database host.' }
    $name = 'OFM-Forms6i-Oracle9i-Redirect'
    if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -Name $name
    }
    New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Action Allow -Protocol TCP -LocalAddress 10.246.0.37 -LocalPort '49152-65535' -RemoteAddress 10.246.0.4 -Program 'C:\oracle\ora90\bin\oracle.exe' -Profile Any | Select-Object Name, Enabled, Action
    return
}
if ($env:COMPUTERNAME -ne 'OFMFORMS6I') { throw 'Wrong Forms host.' }
Add-Type -AssemblyName System.Security
$root = 'C:\OracleForms6iSourceLab\connection'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $identity = New-Object Security.Principal.SecurityIdentifier $sid
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
$operator = (New-Object Security.Principal.NTAccount('ofmlabadmin')).Translate([Security.Principal.SecurityIdentifier])
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($operator, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -LiteralPath $root -AclObject $acl
$keyPath = Join-Path $root 'transfer-key.dpapi'
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider 2048
$rsa.PersistKeyInCsp = $false
try {
    if ($Action -eq 'PrepareCredential') {
        if (Test-Path $keyPath) { throw 'Transfer key already exists.' }
        $private = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        [IO.File]::WriteAllBytes($keyPath, [Security.Cryptography.ProtectedData]::Protect($private, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
        $rsa.ToXmlString($false)
    } else {
        $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($private))
        $plain = $rsa.Decrypt([Convert]::FromBase64String($EncryptedCredential), $true)
        $record = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
        if ($record.User -cne 'MERIDIAN' -or [string]::IsNullOrEmpty($record.Password)) { throw 'Unexpected credential record.' }
        [IO.File]::WriteAllBytes((Join-Path $root 'meridian.dpapi'), [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
        Set-Content -LiteralPath (Join-Path $root 'tnsnames.ora') -Encoding ASCII -Value 'OFM9I=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=10.246.0.37)(PORT=1521))(CONNECT_DATA=(SID=orcl)))'
        Remove-Item -LiteralPath $keyPath
        'Protected MERIDIAN credential and OFM9I alias stored; transfer key removed.'
    }
} finally {
    if ($private) { [Array]::Clear($private, 0, $private.Length) }
    if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
    $record = $null
    $rsa.Dispose()
}