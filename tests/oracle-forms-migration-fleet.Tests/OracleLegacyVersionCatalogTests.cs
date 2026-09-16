// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The catalog is the single interpretation every adapter reads, so these tests pin the canonical family
/// for each release the fleet accepts, the aliases operators actually type, and the refusal of anything it
/// cannot interpret. A recognized family is an intake route, never a compatibility claim.
/// </summary>
public class OracleLegacyVersionCatalogTests
{
    [Theory]
    [InlineData("6i", "6i")]
    [InlineData("6.0.8", "6i")]
    [InlineData("6.0.8.28", "6i")]
    [InlineData("6.0.8.x", "6i")]
    [InlineData("Oracle Forms 6i", "6i")]
    [InlineData("forms 6i", "6i")]
    [InlineData("9i", "9i")]
    [InlineData("9", "9i")]
    [InlineData("9.0.2", "9i")]
    [InlineData("9.x", "9i")]
    [InlineData("10g", "10g")]
    [InlineData("10.1.2", "10g")]
    [InlineData("10.1.2.3", "10g")]
    [InlineData("9.0.4", "10g")]
    [InlineData("11g", "11g")]
    [InlineData("11.1.2", "11g")]
    [InlineData("11.x", "11g")]
    [InlineData("12c", "12c")]
    [InlineData("12c Release 2", "12c")]
    [InlineData("12.1", "12c")]
    [InlineData("12.2", "12c")]
    [InlineData("12.2.1", "12c")]
    [InlineData("12.2.1.4", "12c")]
    [InlineData("12.2.1.x", "12c")]
    [InlineData("Oracle Forms 12.2.1.4", "12c")]
    public void Forms_releases_canonicalise_to_their_family(string supplied, string family)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(supplied);

        Assert.Equal(family, assessment.Family);
        Assert.True(assessment.IsRecognized);
        Assert.True(assessment.IsInLegacyRange);
    }

    [Theory]
    [InlineData("6i")]
    [InlineData("9i")]
    [InlineData("10g")]
    [InlineData("11g")]
    [InlineData("12c")]
    public void Every_legacy_forms_release_still_requires_an_operator_produced_text_export(string supplied)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(supplied);

        // Recognising the release must never imply the binaries became readable.
        Assert.Equal(OracleConversionReadiness.NormalizedTextRequired, assessment.Readiness);
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains("frmf2xml", StringComparison.Ordinal));
        Assert.Contains(assessment.Warnings, warning => warning.Contains("No Oracle tooling runs inside this fleet", StringComparison.Ordinal));
    }

    [Fact]
    public void Forms_6i_records_the_documented_bridge_and_dependency_order()
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms("6i");

        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains("10.1.2", StringComparison.Ordinal));
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains("FRM-18130", StringComparison.Ordinal));
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains(".olb, then .pll, then .mmb, then .fmb", StringComparison.Ordinal));
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains(".fmt", StringComparison.Ordinal) && step.Contains("6i tooling", StringComparison.Ordinal));
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains("Migration Assistant", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("4.5")]
    [InlineData("5.0")]
    public void Pre_6i_forms_is_recognised_but_outside_the_implemented_range(string supplied)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(supplied);

        Assert.Equal("pre-6i", assessment.Family);
        Assert.True(assessment.IsRecognized);
        Assert.False(assessment.IsInLegacyRange);
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains("10.1.2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("14c")]
    [InlineData("14.1.2")]
    public void Newer_forms_releases_stay_recognised_as_assessment_only(string supplied)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(supplied);

        Assert.Equal("14c", assessment.Family);
        Assert.True(assessment.IsRecognized);
        Assert.False(assessment.IsInLegacyRange);
        Assert.Equal(OracleConversionReadiness.AssessmentOnly, assessment.Readiness);
    }

    [Theory]
    [InlineData("12c", "12c")]
    [InlineData("12c Release 2", "12c")]
    [InlineData("12.1", "12c")]
    [InlineData("12.2", "12c")]
    [InlineData("Oracle Database 12c", "12c")]
    [InlineData("6", "6")]
    [InlineData("7", "7")]
    [InlineData("8", "8")]
    [InlineData("8i", "8i")]
    [InlineData("8.1", "8i")]
    [InlineData("8.1.7", "8i")]
    [InlineData("9i", "9i")]
    [InlineData("9.2.0.8", "9i")]
    [InlineData("10g", "10g")]
    [InlineData("10.2.0.5", "10g")]
    [InlineData("11g", "11g")]
    [InlineData("11.2.0.4", "11g")]
    public void Legacy_database_releases_canonicalise_to_their_family(string supplied, string family)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Database(supplied);

        Assert.Equal(family, assessment.Family);
        Assert.True(assessment.IsRecognized);
        Assert.True(assessment.IsInLegacyRange);
        Assert.Equal(OracleConversionReadiness.TextEvidenceReady, assessment.Readiness);
    }

    [Theory]
    [InlineData("18c", "18c")]
    [InlineData("19c", "19c")]
    [InlineData("21c", "21c")]
    [InlineData("23", "23ai")]
    [InlineData("23ai", "23ai")]
    [InlineData("23c", "23ai")]
    [InlineData("Oracle Database Free 23", "23ai")]
    public void Current_database_releases_stay_recognised_so_the_demonstrated_path_does_not_regress(string supplied, string family)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Database(supplied);

        Assert.Equal(family, assessment.Family);
        Assert.True(assessment.IsRecognized);
        Assert.False(assessment.IsInLegacyRange);
        Assert.Equal(OracleConversionReadiness.TextEvidenceReady, assessment.Readiness);
    }

    [Fact]
    public void A_recognised_database_release_still_says_the_evidence_is_only_the_supplied_text()
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Database("6");

        Assert.Contains(assessment.Warnings, warning => warning.Contains("no live Oracle extraction adapter", StringComparison.Ordinal));
        Assert.Contains(assessment.Warnings, warning => warning.Contains("not release-wide support", StringComparison.Ordinal));
        Assert.Contains(assessment.NormalizationGuidance, step => step.Contains("textual DDL and PL/SQL export", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    [InlineData("not supplied")]
    [InlineData("n/a")]
    [InlineData(null)]
    public void An_unsupplied_release_is_unknown_rather_than_rejected(string? supplied)
    {
        foreach (OracleProductLine product in (OracleProductLine[])[OracleProductLine.Forms, OracleProductLine.Database])
        {
            OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Assess(product, supplied);

            Assert.Equal(OracleConversionReadiness.Unknown, assessment.Readiness);
            Assert.Equal(OracleLegacyVersionCatalog.UnknownFamily, assessment.Family);
            Assert.True(assessment.IsUnknown);
            Assert.False(assessment.IsRecognized);
        }
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("99")]
    [InlineData("123")]
    [InlineData("forms")]
    [InlineData("latest")]
    [InlineData("12i")]
    [InlineData("6g")]
    [InlineData("drop table users")]
    public void A_string_that_names_no_release_is_rejected_rather_than_guessed(string supplied)
    {
        foreach (OracleProductLine product in (OracleProductLine[])[OracleProductLine.Forms, OracleProductLine.Database])
        {
            OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Assess(product, supplied);

            Assert.Equal(OracleConversionReadiness.Rejected, assessment.Readiness);
            Assert.False(assessment.IsRecognized);
            Assert.False(assessment.IsUnknown);
            Assert.Equal(OracleLegacyVersionCatalog.UnknownFamily, assessment.Family);
        }
    }

    [Fact]
    public void Forms_and_database_number_series_are_read_separately()
    {
        // "9" is Forms 9i and Oracle9i, but "6" is Oracle 6 only - the families must not be shared.
        Assert.Equal("9i", OracleLegacyVersionCatalog.Forms("9").Family);
        Assert.Equal("6", OracleLegacyVersionCatalog.Database("6").Family);
        Assert.Equal("10g", OracleLegacyVersionCatalog.Forms("9.0.4").Family);
        Assert.Equal("9i", OracleLegacyVersionCatalog.Database("9.0.4").Family);
    }

    /// <summary>
    /// A wildcard is one trailing segment that is exactly 'x' or '*'. The earlier grammar allowed 'x+'
    /// anywhere in the dotted tail and then silently dropped everything from the first wildcard onward, so
    /// every string below was accepted and read back as a release the operator never typed: '12.2.xx' and
    /// '12.2.x.999' both became '12.2', and '12.*.1' became '12'.
    /// </summary>
    [Theory]
    [InlineData("12.2.xx")]
    [InlineData("12.2.x.999")]
    [InlineData("12.*.1")]
    [InlineData("12.2.*.*")]
    [InlineData("12.2.x.x")]
    [InlineData("12.x.1")]
    [InlineData("6.0.8.xx")]
    [InlineData("6.0.8.x.1")]
    [InlineData("*.1")]
    [InlineData("*")]
    [InlineData("x")]
    [InlineData("12.2.x.")]
    [InlineData("12.2.1.xc")]
    public void A_malformed_wildcard_is_rejected_rather_than_read_as_a_shorter_release(string supplied)
    {
        foreach (OracleProductLine product in (OracleProductLine[])[OracleProductLine.Forms, OracleProductLine.Database])
        {
            OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Assess(product, supplied);

            Assert.Equal(OracleConversionReadiness.Rejected, assessment.Readiness);
            Assert.False(assessment.IsRecognized);
            Assert.Null(assessment.Release);
            Assert.Equal(OracleLegacyVersionCatalog.UnknownFamily, assessment.Family);
        }
    }

    [Theory]
    [InlineData("12.2.1.x", "12c", "12.2.1")]
    [InlineData("12.2.*", "12c", "12.2")]
    [InlineData("6.0.8.x", "6i", "6.0.8")]
    public void A_single_terminal_wildcard_names_the_release_line_it_qualifies(string supplied, string family, string release)
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(supplied);

        Assert.True(assessment.IsRecognized);
        Assert.Equal(family, assessment.Family);
        Assert.Equal(release, assessment.Release);
        Assert.Equal(VersionSpecificity.Wildcard, assessment.Specificity);
    }

    [Fact]
    public void A_database_wildcard_release_is_read_on_the_same_grammar()
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Database("19.3.*");

        Assert.True(assessment.IsRecognized);
        Assert.Equal("19c", assessment.Family);
        Assert.Equal("19.3", assessment.Release);
        Assert.Equal(VersionSpecificity.Wildcard, assessment.Specificity);
    }

    [Fact]
    public void A_major_line_wildcard_remains_wildcard_evidence()
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms("12.x");

        Assert.True(assessment.IsRecognized);
        Assert.Equal("12c", assessment.Family);
        Assert.Equal(VersionSpecificity.Wildcard, assessment.Specificity);
    }

    [Theory]
    [InlineData("12.2.*-")]
    [InlineData("12c_")]
    [InlineData("12.2/")]
    [InlineData("12c,")]
    public void A_dangling_separator_is_rejected(string supplied)
    {
        Assert.Equal(OracleConversionReadiness.Rejected, OracleLegacyVersionCatalog.Forms(supplied).Readiness);
        Assert.Equal(OracleConversionReadiness.Rejected, OracleLegacyVersionCatalog.Database(supplied).Readiness);
    }

    /// <summary>
    /// The parser used to scrub words it did not recognise and then read the first digit-shaped token that
    /// survived, so 'banana 12c' and '12.2.1.4 Java 8' both returned Forms 12c. A release has to be the
    /// whole string, not something found inside it.
    /// </summary>
    [Theory]
    [InlineData("banana 12c")]
    [InlineData("12c banana")]
    [InlineData("12c 19c")]
    [InlineData("12.2.1.4 Java 8")]
    [InlineData("upgrade from 6i to 12c")]
    [InlineData("see ticket 12345")]
    [InlineData("12c; drop table users")]
    public void A_release_buried_in_unrelated_text_is_rejected_rather_than_extracted(string supplied)
    {
        foreach (OracleProductLine product in (OracleProductLine[])[OracleProductLine.Forms, OracleProductLine.Database])
        {
            OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Assess(product, supplied);

            Assert.Equal(OracleConversionReadiness.Rejected, assessment.Readiness);
            Assert.Equal(OracleLegacyVersionCatalog.UnknownFamily, assessment.Family);
        }
    }

    [Fact]
    public void A_bare_forms_6_is_rejected_because_6_0_and_6i_are_different_releases()
    {
        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms("6");

        Assert.Equal(OracleConversionReadiness.Rejected, assessment.Readiness);
        Assert.Contains("ambiguous", assessment.Disposition, StringComparison.Ordinal);
        Assert.Contains("6.0.8.28", assessment.Disposition, StringComparison.Ordinal);

        // The database series still reads "6" as Oracle 6; only the Forms series is ambiguous here.
        Assert.Equal("6", OracleLegacyVersionCatalog.Database("6").Family);
    }

    [Theory]
    [InlineData("Oracle Database Free 23", "23ai")]
    [InlineData("Oracle Database 12c Release 2", "12c")]
    [InlineData("oracle rdbms 19c", "19c")]
    [InlineData("Oracle Database 19c Release 3", "19c")]
    public void Documented_product_wrappers_are_allowed_around_one_release(string supplied, string family) =>
        Assert.Equal(family, OracleLegacyVersionCatalog.Database(supplied).Family);

    [Fact]
    public void The_label_carries_the_release_when_one_was_supplied()
    {
        Assert.Equal("12c (12.2.1.4)", OracleLegacyVersionCatalog.Forms("12.2.1.4").Label);

        // A bare family alias names no patch level, and inventing one would report a release nobody stated.
        Assert.Equal("6i", OracleLegacyVersionCatalog.Forms("6i").Label);
        Assert.Null(OracleLegacyVersionCatalog.Forms("6i").Release);
        Assert.Equal("unknown", OracleLegacyVersionCatalog.Forms("unknown").Label);
    }
}
