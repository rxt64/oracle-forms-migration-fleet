using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The object and property codes the Forms API expects, read from the installed
/// <c>FORMS60/API/D2FDEF.H</c> at run time.
///
/// The codes are never compiled in. <c>Build-MeridianForms6i.ps1</c> already establishes that the
/// installed header is the authority for them on this estate, and a build that hard-coded them would be
/// asserting a release's numbering it never read. A name this file needs and the header does not define is
/// reported as a missing prerequisite rather than guessed at, which is what keeps an unsupported release
/// from being silently half-read.
///
/// Each logical field resolves through an ordered candidate list because the header's spelling for a given
/// property is release-specific. Whichever candidate the installed header actually defines is the one used,
/// and its name is recorded in the extraction evidence.
/// </summary>
public sealed partial class FormsApiDefinitions
{
    public const long MaxHeaderBytes = 8L * 1024 * 1024;

    private readonly IReadOnlyDictionary<string, uint> _codes;
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

    private FormsApiDefinitions(IReadOnlyDictionary<string, uint> codes, string headerSha256)
    {
        _codes = codes;
        HeaderSha256 = headerSha256;
    }

    public string HeaderSha256 { get; }

    /// <summary>The header names that were actually used, in resolution order, for the run record.</summary>
    public IReadOnlyList<string> ResolvedNames => [.. _resolved.Values.Order(StringComparer.Ordinal)];

    // The installed header is a Windows file, so the trailing carriage return has to be part of the line
    // terminator here: anchoring on '$' alone silently matched nothing and resolved every code to missing.
    [GeneratedRegex(@"^[ \t]*#define[ \t]+(D2F[A-Za-z0-9_]+)[ \t]+\(?([0-9]+)\)?[ \t\r]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DefinePattern { get; }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentPattern { get; }

    public static FormsApiDefinitions? Load(string headerPath, string headerSha256)
    {
        FileInfo header = new(headerPath);
        if (!header.Exists || header.Length == 0 || header.Length > MaxHeaderBytes)
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(headerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Dictionary<string, uint> codes = new(StringComparer.Ordinal);
        foreach (Match match in DefinePattern.Matches(CommentPattern.Replace(text, " ")))
        {
            if (uint.TryParse(match.Groups[2].ValueSpan, out uint code))
            {
                codes[match.Groups[1].Value] = code;
            }
        }

        return codes.Count == 0 ? null : new FormsApiDefinitions(codes, headerSha256);
    }

    /// <summary>The first candidate the installed header defines, or null when it defines none of them.</summary>
    public uint? Resolve(string logicalName, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (_codes.TryGetValue(candidate, out uint code))
            {
                _resolved[logicalName] = candidate;
                return code;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a numeric code back to the header constant that defines it, within one prefix family — how an
    /// item type code becomes a portable name without this file carrying a table of its own invention.
    /// </summary>
    public string? NameOfCode(string prefix, uint code)
    {
        string? match = null;
        foreach ((string name, uint value) in _codes)
        {
            if (value != code || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // An ambiguous family cannot be reported as one name, so it is reported as none.
            if (match is not null)
            {
                return null;
            }

            match = name;
        }

        return match is null ? null : match[prefix.Length..];
    }
}
