Set-StrictMode -Version Latest

function Invoke-ScChecked([string[]] $Arguments, [string] $FailureMessage) {
    & sc.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (sc.exe exit code $LASTEXITCODE)."
    }
}

function Invoke-IcaclsChecked([string[]] $Arguments, [string] $FailureMessage) {
    & icacls.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (icacls.exe exit code $LASTEXITCODE)."
    }
}

function Set-RestrictedPathAcl(
    [string] $Path,
    [string[]] $Grants,
    [string] $TrustedOwnerSid = 'S-1-5-32-544'
) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($TrustedOwnerSid))
    $acl.SetAccessRuleProtection($true, $false)

    foreach ($grant in $Grants) {
        if ($grant -notmatch '^(?<identity>.+):\((?<inheritance>OI)\)\((?<container>CI)\)\((?<rights>F|M|RX)\)$') {
            throw "Unsupported restricted-directory grant '$grant'."
        }
        $identityText = $Matches.identity
        $rightsText = $Matches.rights
        $rights = switch ($rightsText) {
            'F' { [Security.AccessControl.FileSystemRights]::FullControl }
            'M' { [Security.AccessControl.FileSystemRights]::Modify }
            'RX' { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
        }
        $identity = if ($identityText -match '^S-1-') {
            [Security.Principal.SecurityIdentifier]::new($identityText)
        } else {
            [Security.Principal.NTAccount]::new($identityText)
        }
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            $rights,
            [Security.AccessControl.InheritanceFlags]'ObjectInherit, ContainerInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }

    try {
        Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
    }
    catch {
        throw "Could not establish the exact restricted ACL on $Path. $($_.Exception.Message)"
    }
}

function Set-RestrictedFileAcl(
    [string] $Path,
    [string[]] $Grants,
    [string] $TrustedOwnerSid = 'S-1-5-32-544'
) {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new($TrustedOwnerSid))
    $acl.SetAccessRuleProtection($true, $false)

    foreach ($grant in $Grants) {
        if ($grant -notmatch '^(?<identity>.+):\((?<rights>F|RX|R,D)\)$') {
            throw "Unsupported restricted-file grant '$grant'."
        }
        $identityText = $Matches.identity
        $rightsText = $Matches.rights
        $rights = switch ($rightsText) {
            'F' { [Security.AccessControl.FileSystemRights]::FullControl }
            'RX' { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
            'R,D' { [Security.AccessControl.FileSystemRights]::Read -bor [Security.AccessControl.FileSystemRights]::Delete }
        }
        $identity = if ($identityText -match '^S-1-') {
            [Security.Principal.SecurityIdentifier]::new($identityText)
        } else {
            [Security.Principal.NTAccount]::new($identityText)
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            $rights,
            [Security.AccessControl.AccessControlType]::Allow))
    }

    try {
        Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
    }
    catch {
        throw "Could not establish the exact restricted ACL on $Path. $($_.Exception.Message)"
    }
}

function Remove-TransferCertificateAndKey(
    [string] $CertificatePath,
    [AllowNull()]
    [string] $PrivateKeyPath
) {
    $removeError = $null
    try {
        Remove-Item -LiteralPath $CertificatePath -DeleteKey -Force -ErrorAction Stop
    }
    catch {
        $removeError = $_
    }

    $certificateRemains = Test-Path -LiteralPath $CertificatePath
    $privateKeyUnverifiable = [string]::IsNullOrWhiteSpace($PrivateKeyPath)
    $privateKeyRemains = -not $privateKeyUnverifiable -and
        (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)
    if ($null -ne $removeError -or $certificateRemains -or $privateKeyUnverifiable -or $privateKeyRemains) {
        $reason = if ($null -ne $removeError) { $removeError.Exception.Message }
            elseif ($privateKeyUnverifiable) { 'the private-key path was unavailable for deletion verification' }
            else { 'residual material was detected' }
        throw "The one-time transfer certificate and private key were not fully removed: $reason."
    }
}

function Grant-PathAccess([string] $Path, [string] $Identity, [string] $Rights) {
    Invoke-IcaclsChecked -Arguments @($Path, '/grant:r', "${Identity}:$Rights") `
        -FailureMessage "Could not grant $Identity access to $Path"
}