// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Which Oracle product a version string describes. The two number series overlap, so they parse separately.</summary>
public enum OracleProductLine
{
    Forms,
    Database,
}

/// <summary>
/// What the fleet can honestly do with source at a recognized release. This is a statement about the
/// evidence the fleet needs, never a claim that a release has been proven end to end.
/// </summary>
public enum OracleConversionReadiness
{
    /// <summary>No version was supplied. Planning continues; artifact generation reports the gap.</summary>
    Unknown,

    /// <summary>Recognized but outside the implemented legacy range. Recorded for assessment only.</summary>
    AssessmentOnly,

    /// <summary>
    /// Forms source at this release is a proprietary binary. Operator-provided Oracle tooling has to
    /// produce a textual export before this fleet can generate an application from it.
    /// </summary>
    NormalizedTextRequired,

    /// <summary>
    /// The fleet's evidence for this release is supplied text. It proves the constructs present in that
    /// export and nothing about a live instance it never contacted.
    /// </summary>
    TextEvidenceReady,

    /// <summary>The supplied string matched no known release. Nothing downstream may assume a version.</summary>
    Rejected,
}

/// <summary>
/// How precisely a supplied version string names a release. Two strings can describe the same estate at
/// different precisions, so comparing them as text produces false contradictions: '12c' and '12.2.1.4'
/// agree, and '12.2.1.4' and '12.2.1.5' do not.
/// </summary>
public enum VersionSpecificity
{
    /// <summary>Nothing usable was supplied.</summary>
    None,

    /// <summary>A family name only, such as '6i' or '12c'. It carries no release number at all.</summary>
    FamilyAlias,

    /// <summary>A two-segment release such as '12.2'. It names a release line, not a patch.</summary>
    Broad,

    /// <summary>A release with an explicit wildcard segment, such as '12.2.1.x'.</summary>
    Wildcard,

    /// <summary>A fully qualified release such as '12.2.1.4'.</summary>
    Exact,
}

/// <summary>
/// Deterministic interpretation of one operator-supplied Oracle version string. Offline and allocation-only:
/// it contacts no Oracle instance and reads no file, so it can never confirm what a source estate really runs.
/// </summary>
public sealed record OracleVersionAssessment(
    OracleProductLine Product,
    string Supplied,
    string Family,
    string? Release,
    bool IsRecognized,
    bool IsInLegacyRange,
    OracleConversionReadiness Readiness,
    string Disposition,
    IReadOnlyList<string> NormalizationGuidance,
    IReadOnlyList<string> Warnings)
{
    /// <summary>How precisely <see cref="Supplied"/> named the release. Never inferred from the family.</summary>
    public VersionSpecificity Specificity { get; init; } = VersionSpecificity.None;

    /// <summary>True when the operator supplied nothing usable, as distinct from supplying something wrong.</summary>
    public bool IsUnknown => Readiness == OracleConversionReadiness.Unknown;

    /// <summary>Family plus release when the string carried one, for reports and manifests.</summary>
    public string Label => Release is { Length: > 0 } release && !string.Equals(release, Family, StringComparison.Ordinal)
        ? $"{Family} ({release})"
        : Family;

    /// <summary>
    /// Whether two assessments can describe the same estate.
    ///
    /// Families have to match. Releases agree when one is a dotted prefix of the other, so a broad or
    /// wildcard declaration is compatible with the precise release inside it, and two precise releases
    /// that differ are a genuine contradiction. An assessment carrying no release constrains only family.
    /// </summary>
    public bool IsCompatibleWith(OracleVersionAssessment? other)
    {
        if (other is null || !IsRecognized || !other.IsRecognized)
        {
            return false;
        }

        if (!string.Equals(Family, other.Family, StringComparison.Ordinal))
        {
            return false;
        }

        return Release is not { Length: > 0 } mine || other.Release is not { Length: > 0 } theirs || SharesLineage(mine, theirs);
    }

    /// <summary>True when one dotted release is the other, or a segment-aligned prefix of it.</summary>
    public static bool SharesLineage(string left, string right)
    {
        string[] a = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] b = right.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            if (!string.Equals(a[index], b[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Canonicalizes the Oracle Forms and Oracle Database version strings an operator types at intake.
///
/// The catalog exists so every adapter reads the same interpretation of "6i" or "12.2.1.4" instead of
/// matching substrings of its own. It answers three separate questions and never conflates them: what the
/// release is, whether it falls in the range this fleet has intake and normalization rules for, and what
/// evidence would have to arrive before an artifact could be generated from it.
///
/// It is not a compatibility matrix. A recognized release means the intake path is defined, not that a
/// generated application from that release has ever been compiled, deployed, or behaviourally tested.
/// </summary>
public static class OracleLegacyVersionCatalog
{
    public const string UnknownFamily = "unknown";

    /// <summary>Oracle recommends bridging older modules through this release; FRM-18130 proves it is mandatory.</summary>
    public const string BridgeRelease = "10.1.2";

    private static readonly string[] s_unknownTokens =
    [
        "", "unknown", "unspecified", "not supplied", "notsupplied", "none", "n/a", "na", "tbd", "?",
    ];

    /// <summary>
    /// The only words allowed to precede the version token. The list is an allowlist rather than a
    /// substring scrub because scrubbing let 'banana 12c' through: whatever it failed to recognise simply
    /// disappeared and the first digit-shaped token left behind was read as the release.
    /// </summary>
    private static readonly string[] s_leadingNoise =
    [
        "oracle", "fusion", "middleware", "forms", "builder", "developer", "services", "runtime",
        "database", "db", "rdbms", "server", "express", "personal", "standard", "enterprise",
        "edition", "xe", "free", "version", "ver", "release", "rel",
    ];

    /// <summary>
    /// One version-shaped token, anchored end to end.
    ///
    /// A wildcard is one trailing segment that is exactly 'x' or '*', and it excludes a release suffix.
    /// The earlier pattern allowed 'x+' anywhere in the dotted tail, and the reader stopped at the first
    /// wildcard segment and dropped the rest, so '12.2.xx', '12.2.x.999', '12.*.1', and '12.2.*.*' were all
    /// accepted and every one of them read back as the release '12.2' the operator never typed.
    /// </summary>
    private static readonly Regex s_versionToken = new(
        @"^(?<major>\d{1,2})(?<rest>(?:\.\d+)*)(?:(?<wild>\.(?:x|\*))|(?<suffix>ai|i|g|c))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// The only phrases allowed after the version token. A release or patch-set number here is read rather
    /// than discarded, because 'Oracle Forms 12c Release 2' names 12.2 and dropping the qualifier made it
    /// indistinguishable from bare '12c'.
    /// </summary>
    private static readonly Regex s_trailingQualifier = new(
        @"^(?:release\s+(?<number>\d{1,2})|r(?<number>\d{1,2})|patch\s*set\s*(?<number>\d{1,3})|ps(?<number>\d{1,3})|update\s+(?<number>\d{1,2}))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static readonly char[] s_separators = [' ', '\t', ',', '/', '(', ')', '-', '_'];

    /// <summary>Forms releases this fleet has defined intake and normalization rules for.</summary>
    public static IReadOnlyList<string> FormsLegacyRange { get; } = ["6i", "9i", "10g", "11g", "12c"];

    /// <summary>Database releases the requested legacy range covers. Newer releases stay recognized as well.</summary>
    public static IReadOnlyList<string> DatabaseLegacyRange { get; } = ["6", "7", "8", "8i", "9i", "10g", "11g", "12c"];

    public static OracleVersionAssessment Assess(OracleProductLine product, string? supplied) =>
        product == OracleProductLine.Forms ? Forms(supplied) : Database(supplied);

    public static OracleVersionAssessment Forms(string? supplied)
    {
        string raw = (supplied ?? string.Empty).Trim();

        if (IsUnknownToken(raw))
        {
            return new OracleVersionAssessment(
                OracleProductLine.Forms, raw, UnknownFamily, null,
                IsRecognized: false, IsInLegacyRange: false, OracleConversionReadiness.Unknown,
                "No Oracle Forms release was supplied. Planning and assessment continue, but nothing downstream may assume a release, and artifact generation reports the gap rather than guessing.",
                [],
                ["The Forms release is unknown, so no normalization route, bridge requirement, or obsolete-construct list can be selected for this estate."]);
        }

        VersionToken token = Parse(raw);
        (int major, string? suffix, string? release) = (token.Major, token.Suffix, token.Release);

        // Oracle shipped Forms 6.0 and Forms 6i as different releases with different upgrade routes, so a
        // bare '6' names neither of them. Guessing one would select the wrong normalization route.
        if (major == 6 && suffix is null && release is null)
        {
            return Rejected(
                OracleProductLine.Forms,
                raw,
                "'6' is ambiguous: Oracle shipped Forms 6.0 and Forms 6i as separate releases. Supply '6i' or the exact release such as '6.0.8.28'.");
        }

        // Forms 6i is the 6.0.8 patch series. '6.0' and any other 6.x is Forms 6.0 or something Oracle never
        // shipped, and reading either as 6i would select the 6i bridge route for source that does not need it.
        if (major == 6 && release is { Length: > 0 } sixRelease && !sixRelease.StartsWith("6.0.8", StringComparison.Ordinal))
        {
            return Rejected(
                OracleProductLine.Forms,
                raw,
                $"'{raw}' is not an Oracle Forms 6i release. 6i shipped as the 6.0.8 patch series, so a 6i release reads as '6.0.8.x'; " +
                "'6.0' names Forms 6.0, which is a separate release.");
        }

        // 9.0.4 shipped as Forms 10g; treating it as a 9i release would select the wrong normalization route.
        string? family = major switch
        {
            <= 5 when major > 0 => "pre-6i",
            6 => "6i",
            9 when release is not null && release.StartsWith("9.0.4", StringComparison.Ordinal) => "10g",
            9 => "9i",
            10 => "10g",
            11 => "11g",
            12 => "12c",
            14 => "14c",
            _ => null,
        };

        if (family is null || (suffix is not null && !SuffixAgrees(family, suffix)))
        {
            return Rejected(OracleProductLine.Forms, raw);
        }

        OracleVersionAssessment assessment = family switch
        {
            "pre-6i" => new OracleVersionAssessment(
                OracleProductLine.Forms, raw, family, release,
                IsRecognized: true, IsInLegacyRange: false, OracleConversionReadiness.NormalizedTextRequired,
                $"Oracle documents Forms 3.x, 4.x, 4.5, and 5.x as pre-6i. They have to reach Forms {BridgeRelease} first, so this release is outside the 6i-to-12c intake range this fleet implements and is recorded for assessment.",
                [
                    $"Upgrade every module and library to Forms {BridgeRelease} and recompile before any later release is attempted.",
                    "Save modules held in the database to the file system first; a database-resident module is not an input here.",
                    "Convert client-side PL/SQL v1 or v2 before normalization.",
                    ..SharedFormsGuidance(),
                ],
                [
                    "Pre-6i source is outside the requested 6i-to-12c range. Nothing about it has been exercised by this fleet.",
                    "No Oracle tooling runs inside this fleet. Every step above is performed by the operator under their own Oracle licence and support terms.",
                ]),

            // The release stays exactly what the operator supplied. An earlier version defaulted it to
            // '6.0.8', so a bare '6i' read back as a precise patch level nobody had stated.
            "6i" => new OracleVersionAssessment(
                OracleProductLine.Forms, raw, family, release,
                IsRecognized: true, IsInLegacyRange: true, OracleConversionReadiness.NormalizedTextRequired,
                "Oracle Forms 6i is accepted at intake. Its modules are a proprietary binary, so an operator-provided Oracle toolchain has to produce a textual export before this fleet can generate an application tier from them.",
                [
                    $"Oracle recommends upgrading 6i modules through Forms {BridgeRelease} in most cases before a current release. Omitting that bridge raises FRM-18130, which proves it is mandatory for the affected modules.",
                    "Upgrade in dependency order: .olb, then .pll, then .mmb, then .fmb, with shared dependencies on FORMS_PATH.",
                    "Convert 6i .fmt and .mmt text modules to 6i .fmb and .mmb with 6i tooling first. A current Builder cannot take them directly because obsolete properties may be present.",
                    "Run the Oracle Forms Migration Assistant and keep every per-module log. It automates selected substitutions and warns about obsolete constructs; it does not establish behavioural equivalence, and its search can match names inside comments.",
                    ..SharedFormsGuidance(),
                ],
                [
                    "A successful compile is not equivalence. Some obsolete calls still compile, do nothing, and fail only at runtime.",
                    "An upgraded module cannot be reopened in an earlier Forms Developer release, so normalization must run on a disposable copy of the original tree.",
                    "No Oracle tooling runs inside this fleet. Every step above is performed by the operator under their own Oracle licence and support terms.",
                ]),

            "14c" => new OracleVersionAssessment(
                OracleProductLine.Forms, raw, family, release,
                IsRecognized: true, IsInLegacyRange: false, OracleConversionReadiness.AssessmentOnly,
                "Oracle Forms 14c is newer than the 6i-to-12c intake range this fleet implements. It is recorded for assessment and carries no conversion claim.",
                SharedFormsGuidance(),
                ["14c is outside the implemented legacy range. Nothing generated from a 14c estate has been exercised by this fleet."]),

            _ => new OracleVersionAssessment(
                OracleProductLine.Forms, raw, family, release,
                IsRecognized: true, IsInLegacyRange: true, OracleConversionReadiness.NormalizedTextRequired,
                $"Oracle Forms {family} is accepted at intake. Its modules are a proprietary binary, so an operator-provided Oracle toolchain has to produce a textual export before this fleet can generate an application tier from them.",
                [
                    "Open, save, and compile the modules with a Builder or Compiler the operator is licensed to run, preserving an untouched copy of the original tree.",
                    "Upgrade in dependency order: .olb, then .pll, then .mmb, then .fmb, with shared dependencies on FORMS_PATH.",
                    ..SharedFormsGuidance(),
                ],
                [
                    "Recognizing the release defines the intake route only. No application generated from this release has been compiled, deployed, or behaviourally tested by this fleet.",
                    "No Oracle tooling runs inside this fleet. Every step above is performed by the operator under their own Oracle licence and support terms.",
                ]),
        };

        return assessment with { Specificity = token.Specificity };
    }

    public static OracleVersionAssessment Database(string? supplied)
    {
        string raw = (supplied ?? string.Empty).Trim();

        if (IsUnknownToken(raw))
        {
            return new OracleVersionAssessment(
                OracleProductLine.Database, raw, UnknownFamily, null,
                IsRecognized: false, IsInLegacyRange: false, OracleConversionReadiness.Unknown,
                "No Oracle Database release was supplied. Conversion still runs against supplied SQL text, and the report states that the release behind that text was never established.",
                DatabaseGuidance(),
                ["The Oracle Database release is unknown. Release-specific constructs in the supplied export cannot be checked against the release that produced it."]);
        }

        VersionToken token = Parse(raw);
        (int major, string? suffix, string? release) = (token.Major, token.Suffix, token.Release);

        // "8" is Oracle8 and "8i" is Oracle8i; they are different releases and are not folded together.
        string? family = major switch
        {
            6 => "6",
            7 => "7",
            8 when string.Equals(suffix, "i", StringComparison.OrdinalIgnoreCase) => "8i",
            8 when release is not null && release.StartsWith("8.1", StringComparison.Ordinal) => "8i",
            8 => "8",
            9 => "9i",
            10 => "10g",
            11 => "11g",
            12 => "12c",
            18 => "18c",
            19 => "19c",
            21 => "21c",
            23 => "23ai",
            _ => null,
        };

        if (family is null || (suffix is not null && !SuffixAgrees(family, suffix)))
        {
            return Rejected(OracleProductLine.Database, raw);
        }

        bool inRange = DatabaseLegacyRange.Contains(family, StringComparer.Ordinal);

        return new OracleVersionAssessment(
            OracleProductLine.Database, raw, family, release,
            IsRecognized: true, IsInLegacyRange: inRange, OracleConversionReadiness.TextEvidenceReady,
            inRange
                ? $"Oracle Database {family} is accepted at intake. Conversion reads the supplied DDL and PL/SQL text; it proves the constructs present in that export and nothing about a {family} instance this fleet never contacted."
                : $"Oracle Database {family} is recognized and newer than the 6-to-12c range this work targeted. Conversion reads the supplied DDL and PL/SQL text and proves only the constructs present in that export.",
            DatabaseGuidance(),
            [
                "This fleet has no live Oracle extraction adapter. Every database fact it reports came from a text file an operator supplied.",
                $"A clean conversion of this export is not release-wide support for Oracle Database {family}.",
            ])
        {
            Specificity = token.Specificity,
        };
    }

    private static IReadOnlyList<string> SharedFormsGuidance() =>
    [
        "Export the normalized modules to Forms XML with frmf2xml, or to another stable textual representation, and supply that text to this fleet.",
        "Supply the original source hashes, compiler logs, migration logs, database source, runtime configuration, and a behavioural baseline alongside the text.",
    ];

    private static IReadOnlyList<string> DatabaseGuidance() =>
    [
        "Supply a verified textual DDL and PL/SQL export produced against the source database by the operator.",
        "Supply the data as a textual export as well. This fleet connects to no Oracle instance, so it cannot extract either one itself.",
    ];

    private static OracleVersionAssessment Rejected(OracleProductLine product, string raw, string? reason = null)
    {
        bool forms = product == OracleProductLine.Forms;
        IReadOnlyList<string> known = forms
            ? ["pre-6i", .. FormsLegacyRange, "14c"]
            : ["6", "7", "8", "8i", "9i", "10g", "11g", "12c", "18c", "19c", "21c", "23ai"];

        return new OracleVersionAssessment(
            product, raw, UnknownFamily, null,
            IsRecognized: false, IsInLegacyRange: false, OracleConversionReadiness.Rejected,
            reason is null
                ? $"'{raw}' matches no Oracle {(forms ? "Forms" : "Database")} release this catalog knows. It was not interpreted, and nothing downstream may treat it as a version."
                : $"'{raw}' was not interpreted as an Oracle {(forms ? "Forms" : "Database")} release. {reason} Nothing downstream may treat it as a version.",
            [],
            [$"Supply a recognized release or 'unknown'. Recognized {(forms ? "Forms" : "Database")} families: {string.Join(", ", known)}."]);
    }

    private static bool IsUnknownToken(string raw) =>
        s_unknownTokens.Contains(raw, StringComparer.OrdinalIgnoreCase);

    /// <summary>What one supplied string resolved to before any product-specific rule is applied.</summary>
    private sealed record VersionToken(int Major, string? Suffix, string? Release, VersionSpecificity Specificity)
    {
        public static VersionToken NoMatch { get; } = new(-1, null, null, VersionSpecificity.None);
    }

    /// <summary>
    /// Splits a cleaned string into its major number, release suffix, dotted release, and how precisely it
    /// named that release.
    ///
    /// The whole token has to match. An earlier version trimmed trailing '.' and '+' before matching, which
    /// turned '12c+' and '12c.' into '12c' and accepted strings that name no release Oracle ever shipped.
    /// </summary>
    private static VersionToken Parse(string raw)
    {
        if (raw.EndsWith('-') || raw.EndsWith('_') || raw.EndsWith('/') || raw.EndsWith(','))
        {
            return VersionToken.NoMatch;
        }

        string[] tokens = raw.ToLowerInvariant().Split(s_separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int index = 0;
        while (index < tokens.Length && s_leadingNoise.Contains(tokens[index], StringComparer.Ordinal))
        {
            index++;
        }

        if (index >= tokens.Length)
        {
            return VersionToken.NoMatch;
        }

        Match match;
        string? qualifier = null;

        try
        {
            match = s_versionToken.Match(tokens[index]);

            if (match.Success && index + 1 < tokens.Length)
            {
                // Anything after the version has to be a release qualifier. A second version-shaped token,
                // an unrelated product, or stray prose means the string names more than one thing.
                Match trailing = s_trailingQualifier.Match(string.Join(' ', tokens[(index + 1)..]));
                if (!trailing.Success)
                {
                    return VersionToken.NoMatch;
                }

                qualifier = trailing.Groups["number"].Value;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return VersionToken.NoMatch;
        }

        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out int major))
        {
            return VersionToken.NoMatch;
        }

        string suffix = match.Groups["suffix"].Value;
        string rest = match.Groups["rest"].Value;

        // '6.0.8.x' names a patch family, not a patch. The wildcard segment itself is never reported as a
        // release number, so no release is stated that the operator did not supply.
        bool wildcard = match.Groups["wild"].Success;
        List<string> parts = [.. rest.Split('.', StringSplitOptions.RemoveEmptyEntries)];

        // 'Release 2' after a family alias is the release number: '12c Release 2' is 12.2.
        if (parts.Count == 0 && qualifier is { Length: > 0 } && int.TryParse(qualifier, out int release))
        {
            parts.Add(release.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        string? dotted = parts.Count > 0
            ? $"{major.ToString(System.Globalization.CultureInfo.InvariantCulture)}.{string.Join('.', parts)}"
            : null;

        return new VersionToken(
            major,
            suffix.Length > 0 ? suffix.ToLowerInvariant() : null,
            dotted,
            Precision(dotted, wildcard));
    }

    /// <summary>How precisely a supplied string named its release. Segment count, never the family.</summary>
    private static VersionSpecificity Precision(string? release, bool wildcard) => release switch
    {
        _ when wildcard => VersionSpecificity.Wildcard,
        null => VersionSpecificity.FamilyAlias,
        _ when release.Count(character => character == '.') <= 1 => VersionSpecificity.Broad,
        _ => VersionSpecificity.Exact,
    };

    /// <summary>Rejects a suffix that contradicts the release it was attached to, such as '12i' or '6g'.</summary>
    private static bool SuffixAgrees(string family, string suffix) => family switch
    {
        "pre-6i" or "6i" or "9i" or "8i" or "8" => suffix is "i",
        "10g" or "11g" => suffix is "g",
        "12c" or "14c" or "18c" or "19c" or "21c" => suffix is "c",
        "23ai" => suffix is "ai" or "c",
        _ => false,
    };
}
