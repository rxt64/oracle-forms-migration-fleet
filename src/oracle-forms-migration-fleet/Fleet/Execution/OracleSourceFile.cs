// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Which files hold Oracle SQL or PL/SQL text.
///
/// A real estate does not keep everything in .sql. Package specifications and bodies ship as .pks and
/// .pkb, standalone routines as .prc and .fnc, triggers as .trg. Reading only .sql meant an estate could
/// have its packages classified as evidence and then never parsed, which is the worst kind of gap: the
/// checklist says the source is present and the converter silently sees none of it.
/// </summary>
public static class OracleSourceFile
{
    private static readonly HashSet<string> s_sqlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql", ".pks", ".pkb", ".plb", ".prc", ".fnc", ".trg", ".vw", ".tab", ".seq", ".ddl", ".pls",
    };

    public static bool IsSqlText(string path) =>
        s_sqlExtensions.Contains(Path.GetExtension(path));

    public static bool IsFormsXml(string path) =>
        Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);
}
