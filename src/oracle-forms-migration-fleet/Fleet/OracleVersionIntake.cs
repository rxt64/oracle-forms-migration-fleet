// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// The one place the Oracle release fields on an inbound request are checked.
///
/// Assessment and run requests used to validate these differently: the run planner checked neither field
/// for credential material and neither request rejected a release the catalog could not interpret, so a
/// value like 'banana' travelled as far as an adapter before anything refused it and 'unknown' and
/// 'nonsense' were indistinguishable at the boundary. Both contracts now share this check, and a release
/// the catalog rejects stops the request before a phase runs or an artifact is written.
///
/// 'unknown' remains allowed: not having established the release is a real state, and forcing a guess is
/// what this product exists to prevent.
/// </summary>
public static class OracleVersionIntake
{
    public static IReadOnlyList<string> Validate(string? formsVersion, string? databaseVersion)
    {
        List<string> errors = [];

        if (FleetGuardrails.ContainsPotentialSecret(formsVersion) ||
            FleetGuardrails.ContainsPotentialSecret(databaseVersion))
        {
            errors.Add("Oracle release fields appear to contain credential material and were rejected.");
            return errors;
        }

        foreach ((OracleProductLine product, string? supplied) in
                 ((OracleProductLine, string?)[])[(OracleProductLine.Forms, formsVersion), (OracleProductLine.Database, databaseVersion)])
        {
            OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Assess(product, supplied);

            if (assessment.Readiness == OracleConversionReadiness.Rejected)
            {
                errors.Add(
                    $"{(product == OracleProductLine.Forms ? "OracleFormsVersion" : "OracleDatabaseVersion")}: {assessment.Disposition} " +
                    string.Join(" ", assessment.Warnings));
            }
        }

        return errors;
    }
}
