using System.Data.Common;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>How a provider spells a bind variable. ODBC binds positionally; most Oracle clients bind by name.</summary>
public enum OracleParameterStyle
{
    Named,
    Positional,
}

/// <summary>
/// The only way the extraction service reaches an Oracle instance. The service never sees a connect
/// string, a user, a password or a provider assembly: it asks for an open connection and is told which
/// schemas the operator approved.
/// </summary>
public interface IOracleConnectionFactory
{
    bool Configured { get; }

    /// <summary>Names the first unset setting, for a typed refusal that does not quote the setting's value.</summary>
    string? FirstMissingSetting { get; }

    /// <summary>A non-secret label for the client actually used, reported as evidence.</summary>
    string ProviderAlias { get; }

    OracleParameterStyle ParameterStyle { get; }

    /// <summary>The schemas the operator approved. A request may narrow this and may never widen it.</summary>
    IReadOnlyList<string> SchemaAllowlist { get; }

    TimeSpan Timeout { get; }

    Task<DbConnection> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The worker's own Oracle settings, read from its environment by the operator who provisioned the host.
///
/// The connect string is held in a private field, is never a property, and is handed only to the opener
/// delegate supplied at construction. It is therefore absent from every protocol object, every finding and
/// every <c>ToString</c> this assembly can produce, which is why this is a class and not a record.
/// </summary>
public sealed class OracleSourceConfiguration
{
    public const string ConnectionStringVariable = "OFM_WORKER_ORACLE_CONNECTION_STRING";
    public const string ProviderVariable = "OFM_WORKER_ORACLE_PROVIDER";
    public const string AllowlistVariable = "OFM_WORKER_ORACLE_SCHEMA_ALLOWLIST";
    public const string TimeoutVariable = "OFM_WORKER_ORACLE_TIMEOUT_SECONDS";

    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_maximumTimeout = TimeSpan.FromMinutes(15);

    private readonly string? _connectionString;

    public OracleSourceConfiguration(
        string? connectionString,
        string? providerAlias,
        IReadOnlyList<string> schemaAllowlist,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(schemaAllowlist);
        _connectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString.Trim();
        ProviderAlias = string.IsNullOrWhiteSpace(providerAlias) ? "unspecified" : providerAlias.Trim();
        SchemaAllowlist = schemaAllowlist;
        Timeout = timeout;
    }

    public string ProviderAlias { get; }

    public IReadOnlyList<string> SchemaAllowlist { get; }

    public TimeSpan Timeout { get; }

    public bool Configured => _connectionString is not null && SchemaAllowlist.Count > 0;

    public string? FirstMissingSetting =>
        _connectionString is null ? ConnectionStringVariable
        : SchemaAllowlist.Count == 0 ? AllowlistVariable
        : null;

    public static OracleSourceConfiguration FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new OracleSourceConfiguration(
            read(ConnectionStringVariable),
            read(ProviderVariable),
            ParseAllowlist(read(AllowlistVariable)),
            ParseTimeout(read(TimeoutVariable)));
    }

    /// <summary>Hands the connect string to the opener and to nothing else.</summary>
    internal DbConnection Create(Func<string, DbConnection> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        ObjectDisposedException.ThrowIf(_connectionString is null, this);
        return open(_connectionString);
    }

    /// <summary>
    /// Accepts only unquoted Oracle schema names in their stored (upper) form, so an allowlist entry can
    /// never carry a quote, a dot, a wildcard or a comment introducer into a catalog predicate.
    /// </summary>
    public static bool IsSchemaName(string? value) =>
        value is { Length: > 0 and <= 30 } &&
        (char.IsAsciiLetterUpper(value[0]) || value[0] is '_') &&
        value.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character is '_' or '$' or '#');

    private static IReadOnlyList<string> ParseAllowlist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
        {
            return [];
        }

        List<string> accepted = [];
        foreach (string candidate in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = candidate.ToUpperInvariant();
            if (!IsSchemaName(normalized))
            {
                return [];
            }

            if (!accepted.Contains(normalized, StringComparer.Ordinal))
            {
                accepted.Add(normalized);
            }
        }

        accepted.Sort(StringComparer.Ordinal);
        return accepted;
    }

    private static TimeSpan ParseTimeout(string? value)
    {
        if (!int.TryParse(value, out int seconds) || seconds <= 0)
        {
            return s_defaultTimeout;
        }

        TimeSpan requested = TimeSpan.FromSeconds(seconds);
        return requested > s_maximumTimeout ? s_maximumTimeout : requested;
    }
}

/// <summary>
/// The production factory. The concrete client type is supplied by the composing host as a delegate, so
/// this assembly takes no dependency on a provider package and cannot be forced onto a client that does
/// not speak to the configured release.
/// </summary>
public sealed class ConfiguredOracleConnectionFactory(
    OracleSourceConfiguration configuration,
    Func<string, DbConnection> open,
    OracleParameterStyle parameterStyle) : IOracleConnectionFactory
{
    private readonly OracleSourceConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly Func<string, DbConnection> _open = open ?? throw new ArgumentNullException(nameof(open));

    public bool Configured => _configuration.Configured;

    public string? FirstMissingSetting => _configuration.FirstMissingSetting;

    public string ProviderAlias => _configuration.ProviderAlias;

    public OracleParameterStyle ParameterStyle { get; } = parameterStyle;

    public IReadOnlyList<string> SchemaAllowlist => _configuration.SchemaAllowlist;

    public TimeSpan Timeout => _configuration.Timeout;

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = _configuration.Create(_open);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
