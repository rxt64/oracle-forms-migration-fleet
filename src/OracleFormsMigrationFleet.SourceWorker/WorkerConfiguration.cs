namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The worker's fixed configuration. Every directory and library the worker is willing to touch is named
/// here by the operator who provisioned the host, never by the caller. A request supplies an alias and a
/// hash; it cannot widen this.
///
/// The default is unconfigured, and unconfigured means the native provider is off. Nothing falls back to
/// <c>PATH</c>, <c>ORACLE_HOME</c> or a probed install, because a worker that discovers its own Forms home
/// cannot state which binaries an operator approved.
/// </summary>
public sealed record WorkerConfiguration(
    string? TrustedInputRoot,
    string? ArtifactOutputRoot,
    string? FormsHome,
    string? ApprovedLibrarySha256,
    string? ApprovedLibraryFileVersion,
    TimeSpan Timeout)
{
    public const string InputRootVariable = "OFM_WORKER_INPUT_ROOT";
    public const string OutputRootVariable = "OFM_WORKER_OUTPUT_ROOT";
    public const string FormsHomeVariable = "OFM_WORKER_FORMS_HOME";
    public const string LibraryHashVariable = "OFM_WORKER_FORMS_LIBRARY_SHA256";
    public const string LibraryVersionVariable = "OFM_WORKER_FORMS_LIBRARY_VERSION";
    public const string TimeoutVariable = "OFM_WORKER_TIMEOUT_SECONDS";

    /// <summary>The single library alias this worker will load, relative to <see cref="FormsHome"/>.</summary>
    public const string LibraryAlias = "bin/ifd2f60.dll";

    /// <summary>The installed API definition header the object and property codes are read from.</summary>
    public const string DefinitionHeaderAlias = "FORMS60/API/D2FDEF.H";

    /// <summary>
    /// The one non-numeric value <see cref="ApprovedLibraryFileVersion"/> accepts, for a binary that carries
    /// no file version resource at all — which is what the <c>ifd2f60.dll</c> verified on this estate does.
    ///
    /// It is an explicit operator opt-in, not a default: an unset variable still leaves the provider off, and
    /// the sentinel is satisfied only by a version that is genuinely absent. It is never recorded as an
    /// observed version, and it is only usable alongside <see cref="ApprovedLibrarySha256"/>, which is what
    /// actually pins the binary in that case.
    /// </summary>
    public const string UnversionedSentinel = "unversioned";

    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_maximumTimeout = TimeSpan.FromMinutes(15);

    public static WorkerConfiguration FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new WorkerConfiguration(
            Directory(read(InputRootVariable)),
            Directory(read(OutputRootVariable)),
            Directory(read(FormsHomeVariable)),
            Hash(read(LibraryHashVariable)),
            Version(read(LibraryVersionVariable)),
            TimeBudget(read(TimeoutVariable)));
    }

    /// <summary>The approved library path, or null when the provider is not fully configured.</summary>
    public string? LibraryPath => FormsHome is null ? null : Path.Combine(FormsHome, "bin", "ifd2f60.dll");

    /// <summary>The installed definition header path, or null when the provider is not fully configured.</summary>
    public string? DefinitionHeaderPath => FormsHome is null ? null : Path.Combine(FormsHome, "FORMS60", "API", "D2FDEF.H");

    /// <summary>
    /// The native provider is usable only when the operator named a home <em>and</em> pinned the binary.
    /// A home with no approved hash is deliberately not enough: it would let a replaced DLL pass as approved.
    /// </summary>
    public bool NativeProviderConfigured =>
        FormsHome is not null && ApprovedLibrarySha256 is not null && ApprovedLibraryFileVersion is not null;

    /// <summary>True when the operator declared that the approved binary carries no file version at all.</summary>
    public bool RequiresUnversionedLibrary =>
        string.Equals(ApprovedLibraryFileVersion, UnversionedSentinel, StringComparison.Ordinal);

    public bool ExtractionConfigured =>
        NativeProviderConfigured && TrustedInputRoot is not null && ArtifactOutputRoot is not null;

    /// <summary>Names the first configuration variable that is missing, for a typed remediation.</summary>
    public string? FirstMissingSetting =>
        TrustedInputRoot is null ? InputRootVariable
        : ArtifactOutputRoot is null ? OutputRootVariable
        : FormsHome is null ? FormsHomeVariable
        : ApprovedLibrarySha256 is null ? LibraryHashVariable
        : ApprovedLibraryFileVersion is null ? LibraryVersionVariable
        : null;

    private static string? Directory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (!Path.IsPathFullyQualified(trimmed) || trimmed.IndexOfAny(['\0', '*', '?']) >= 0)
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? Hash(string? value) =>
        value is not null && ContentHash.IsSha256(value.Trim()) ? value.Trim().ToLowerInvariant() : null;

    private static string? Version(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (string.Equals(trimmed, UnversionedSentinel, StringComparison.OrdinalIgnoreCase))
        {
            return UnversionedSentinel;
        }

        return trimmed.All(character => char.IsAsciiDigit(character) || character == '.') ? trimmed : null;
    }

    private static TimeSpan TimeBudget(string? value)
    {
        if (!int.TryParse(value, out int seconds) || seconds <= 0)
        {
            return s_defaultTimeout;
        }

        TimeSpan requested = TimeSpan.FromSeconds(seconds);
        return requested > s_maximumTimeout ? s_maximumTimeout : requested;
    }
}
