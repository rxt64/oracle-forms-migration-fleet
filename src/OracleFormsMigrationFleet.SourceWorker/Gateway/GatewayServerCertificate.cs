using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>
/// The certificate this gateway will present, or the reason it will present none.
///
/// A failure is always a refusal to listen. There is no "best effort" TLS here: a gateway that could not
/// prove which certificate an operator approved must not fall back to a self-signed one, a development
/// certificate, or cleartext.
/// </summary>
public sealed record GatewayCertificateSelection(X509Certificate2? Certificate, string? Failure)
{
    public static GatewayCertificateSelection Refused(string failure) => new(null, failure);
}

/// <summary>
/// Chooses the server certificate out of <c>LocalMachine\My</c>.
///
/// This exists because an unattended Windows service has no operator to hand a PFX and no safe place to
/// keep its password. The private key stays non-exportable in the machine store, the service account is
/// granted read access to it by the installer, and this code only names which certificate to load.
///
/// Selection is deliberately narrow and fails closed:
///
/// * A pinned thumbprint wins. If one is configured and no certificate in the store matches it, the
///   gateway does not listen — it does not "fall back" to a hostname search.
/// * Without a pin, a candidate must carry the listener's host as its subject common name or as a DNS
///   entry in its subject alternative name extension. Matching is exact and case-insensitive; no
///   wildcard is honoured, because a wildcard certificate is a broader grant than this listener needs.
/// * Every candidate must additionally hold a private key, be inside its validity window, and carry an
///   Enhanced Key Usage extension that includes Server Authentication. A certificate with no EKU
///   extension at all is unconstrained and is refused rather than treated as permissive.
/// * Exactly one candidate must survive. Two surviving candidates are an ambiguity an operator has to
///   resolve — picking the newest would silently change which key is in use during a rotation.
/// </summary>
public static class GatewayServerCertificate
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string EnhancedKeyUsageOid = "2.5.29.37";
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>Opens <c>LocalMachine\My</c> read-only and selects from it.</summary>
    public static GatewayCertificateSelection SelectFromLocalMachineStore(
        GatewayTlsOptions tls,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tls);

        if (!OperatingSystem.IsWindows())
        {
            return GatewayCertificateSelection.Refused(
                "An https listener selects its certificate from the Windows LocalMachine\\My store, which this host does " +
                "not have. Configure a loopback development listener instead, or run the gateway on the approved Windows host.");
        }

        X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
        List<X509Certificate2> loaded = [];
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            loaded.AddRange(store.Certificates.OfType<X509Certificate2>());
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            return GatewayCertificateSelection.Refused(
                "The LocalMachine\\My certificate store could not be opened for reading by the account this gateway runs as.");
        }
        finally
        {
            store.Close();
        }

        GatewayCertificateSelection selection = Select(tls, now, loaded);
        foreach (X509Certificate2 candidate in loaded)
        {
            if (!ReferenceEquals(candidate, selection.Certificate))
            {
                candidate.Dispose();
            }
        }

        return selection;
    }

    /// <summary>
    /// The store-independent selection, exposed so the rules can be tested against constructed
    /// certificates without installing anything on the test host.
    /// </summary>
    public static GatewayCertificateSelection Select(
        GatewayTlsOptions tls,
        DateTimeOffset now,
        IReadOnlyList<X509Certificate2> candidates)
    {
        ArgumentNullException.ThrowIfNull(tls);
        ArgumentNullException.ThrowIfNull(candidates);

        string criterion = tls.PinnedThumbprint is { Length: > 0 } pin
            ? $"thumbprint {pin}"
            : $"host name '{tls.SubjectHost}'";

        List<X509Certificate2> matched = [.. candidates.Where(candidate => Matches(tls, candidate))];
        if (matched.Count is 0)
        {
            return GatewayCertificateSelection.Refused(
                $"No certificate in LocalMachine\\My matches the configured {criterion}, so the gateway did not listen.");
        }

        List<string> rejections = [];
        List<X509Certificate2> usable = [.. matched.Where(candidate => Usable(candidate, now, rejections))];

        if (usable.Count is 0)
        {
            return GatewayCertificateSelection.Refused(
                $"A certificate matching {criterion} was found, but none is usable as this listener's server " +
                $"certificate: {string.Join("; ", rejections.Distinct(StringComparer.Ordinal))}.");
        }

        if (usable.Count > 1)
        {
            return GatewayCertificateSelection.Refused(
                $"{usable.Count} certificates in LocalMachine\\My match {criterion} and are all usable. Pin the exact " +
                $"{GatewayTlsOptions.ThumbprintVariable} an operator approved; the gateway will not choose between them.");
        }

        return new GatewayCertificateSelection(usable[0], null);
    }

    private static bool Matches(GatewayTlsOptions tls, X509Certificate2 candidate)
    {
        bool nameMatches = NamesOf(candidate).Contains(tls.SubjectHost, StringComparer.OrdinalIgnoreCase);
        return tls.PinnedThumbprint is { Length: > 0 } pin
            ? string.Equals(Normalize(candidate.Thumbprint), pin, StringComparison.OrdinalIgnoreCase) && nameMatches
            : nameMatches;
    }

    private static bool Usable(X509Certificate2 candidate, DateTimeOffset now, List<string> rejections)
    {
        bool usable = true;

        if (!CanUsePrivateKey(candidate))
        {
            rejections.Add("its private key is not available to this account");
            usable = false;
        }

        if (now < candidate.NotBefore.ToUniversalTime() || now > candidate.NotAfter.ToUniversalTime())
        {
            rejections.Add("it is outside its validity window");
            usable = false;
        }

        if (!HasServerAuthentication(candidate))
        {
            rejections.Add("it carries no Server Authentication enhanced key usage");
            usable = false;
        }

        return usable;
    }

    private static bool CanUsePrivateKey(X509Certificate2 candidate)
    {
        if (!candidate.HasPrivateKey)
        {
            return false;
        }

        byte[] digest = SHA256.HashData("OFMSourceGateway private-key access check"u8);
        try
        {
            using RSA? rsa = candidate.GetRSAPrivateKey();
            if (rsa is not null)
            {
                byte[] signature = rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                CryptographicOperations.ZeroMemory(signature);
                return true;
            }

            using ECDsa? ecdsa = candidate.GetECDsaPrivateKey();
            if (ecdsa is not null)
            {
                byte[] signature = ecdsa.SignHash(digest);
                CryptographicOperations.ZeroMemory(signature);
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is CryptographicException or NotSupportedException or
            ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool HasServerAuthentication(X509Certificate2 candidate)
    {
        foreach (X509Extension extension in candidate.Extensions)
        {
            if (!string.Equals(extension.Oid?.Value, EnhancedKeyUsageOid, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                // Decoded from raw bytes rather than trusting the collection to hand back a typed
                // instance, so the check behaves the same however the certificate was loaded.
                X509EnhancedKeyUsageExtension usage = new(
                    new AsnEncodedData(extension.Oid!, extension.RawData), extension.Critical);

                return usage.EnhancedKeyUsages.OfType<System.Security.Cryptography.Oid>()
                    .Any(oid => string.Equals(oid.Value, ServerAuthenticationOid, StringComparison.Ordinal));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The subject common name plus every DNS subject alternative name.</summary>
    private static IReadOnlyList<string> NamesOf(X509Certificate2 candidate)
    {
        List<string> names = [];

        string common = candidate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (common is { Length: > 0 })
        {
            names.Add(common);
        }

        foreach (X509Extension extension in candidate.Extensions)
        {
            if (!string.Equals(extension.Oid?.Value, SubjectAlternativeNameOid, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                names.AddRange(new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical).EnumerateDnsNames());
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // An unparseable SAN contributes no names; it never widens what this listener will answer for.
            }
        }

        return names;
    }

    internal static string Normalize(string? thumbprint) =>
        new((thumbprint ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray());
}
