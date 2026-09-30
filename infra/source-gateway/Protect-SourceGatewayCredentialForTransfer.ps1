[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublicCertificatePath,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublicCertificatePath -PathType Leaf)) {
    throw 'The one-time public certificate was not found.'
}
if (Test-Path -LiteralPath $OutputPath) {
    throw 'Refusing to overwrite an existing encrypted credential payload.'
}

$certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($PublicCertificatePath)
try {
    if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw 'The one-time public certificate has expired.'
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
    try {
        $maximumPlaintextBytes = ($rsa.KeySize / 8) - (2 * 32) - 2
        $input = [Console]::OpenStandardInput()
        $buffer = [byte[]]::new($maximumPlaintextBytes + 3)
        $count = 0
        while (($read = $input.Read($buffer, $count, $buffer.Length - $count)) -gt 0) {
            $count += $read
            if ($count -eq $buffer.Length) { break }
        }
        while ($count -gt 0 -and $buffer[$count - 1] -in 10, 13) { $count-- }
        if ($count -le 0 -or $count -gt $maximumPlaintextBytes) {
            throw "Standard input must contain one nonempty credential of at most $maximumPlaintextBytes UTF-8 bytes."
        }
        $plaintext = [byte[]]::new($count)
        [Array]::Copy($buffer, $plaintext, $count)
        try {
            $ciphertext = $rsa.Encrypt($plaintext, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
            [IO.File]::WriteAllBytes($OutputPath, $ciphertext)
        }
        finally {
            [Array]::Clear($plaintext, 0, $plaintext.Length)
            [Array]::Clear($buffer, 0, $buffer.Length)
        }
    }
    finally {
        $rsa.Dispose()
    }
}
finally {
    $certificate.Dispose()
}

Write-Output "Encrypted credential payload written to $OutputPath. Its plaintext was read only from standard input and was not logged."