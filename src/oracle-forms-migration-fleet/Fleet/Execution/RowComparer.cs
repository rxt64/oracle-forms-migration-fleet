// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public enum RowDifferenceKind
{
    MissingInTarget,
    ValueDiffers,
}

public sealed record RowDifference(
    RowDifferenceKind Kind,
    string Table,
    string Key,
    string? Column,
    string? Expected,
    string? Actual);

public sealed record TableComparison(
    string Table,
    int Compared,
    int NotComparable,
    IReadOnlyList<RowDifference> Differences);

/// <summary>
/// Compares the rows an export supplies against the rows the target holds, keyed by primary key.
///
/// Counting rows only says how many arrived. This says whether they are the same, which is the check that
/// catches a row that loaded with the wrong values. It follows the shape AWS DMS validation uses: match on
/// the key, then report either a missing row or the specific columns that differ.
///
/// It compares only literals it can compare safely. An Oracle expression such as SYSDATE or a sequence
/// call produces a different value on each side by definition, so those columns are counted as not
/// comparable and reported rather than being declared a difference.
/// </summary>
public static class RowComparer
{
    public static TableComparison Compare(
        string table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<(IReadOnlyList<string> Columns, IReadOnlyList<string> Values)> expected,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<IReadOnlyList<string?>> targetRows)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(targetColumns);
        ArgumentNullException.ThrowIfNull(targetRows);

        Dictionary<string, IReadOnlyList<string?>> byKey = new(StringComparer.Ordinal);
        int[] keyIndexes = [.. keyColumns.Select(key => IndexOf(targetColumns, key))];

        if (keyIndexes.Any(index => index < 0) || targetRows.Count == 0)
        {
            // Reporting every row as missing when the target was never read would be a false finding.
            return new TableComparison(table, 0, expected.Count, []);
        }

        foreach (IReadOnlyList<string?> row in targetRows)
        {
            byKey[string.Join('\u001f', keyIndexes.Select(index => Normalise(row[index])))] = row;
        }

        List<RowDifference> differences = [];
        int compared = 0;
        int notComparable = 0;

        foreach ((IReadOnlyList<string> columns, IReadOnlyList<string> values) in expected)
        {
            if (!TryKey(keyColumns, columns, values, out string key))
            {
                notComparable++;
                continue;
            }

            if (!byKey.TryGetValue(key, out IReadOnlyList<string?>? actual))
            {
                differences.Add(new RowDifference(RowDifferenceKind.MissingInTarget, table, key, null, null, null));
                continue;
            }

            compared++;

            for (int index = 0; index < columns.Count; index++)
            {
                string column = columns[index];
                if (keyColumns.Any(keyColumn => string.Equals(keyColumn, column, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int target = IndexOf(targetColumns, column);
                if (target < 0)
                {
                    continue;
                }

                if (!TryLiteral(values[index], out string? want))
                {
                    notComparable++;
                    continue;
                }

                string? have = actual[target];
                if (!Same(want, have))
                {
                    differences.Add(new RowDifference(RowDifferenceKind.ValueDiffers, table, key, column, want, have));
                }
            }
        }

        return new TableComparison(table, compared, notComparable, differences);
    }

    private static bool TryKey(
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values,
        out string key)
    {
        List<string> parts = [];

        foreach (string keyColumn in keyColumns)
        {
            int index = IndexOf(columns, keyColumn);
            if (index < 0 || !TryLiteral(values[index], out string? literal))
            {
                key = string.Empty;
                return false;
            }

            parts.Add(Normalise(literal));
        }

        key = string.Join('\u001f', parts);
        return parts.Count > 0;
    }

    /// <summary>A literal whose value is the same on both sides. An expression is not one.</summary>
    private static bool TryLiteral(string value, out string? literal)
    {
        string trimmed = value.Trim();
        literal = null;

        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            literal = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return true;
        }

        if (decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
        {
            literal = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool Same(string? expected, string? actual)
    {
        if (expected is null)
        {
            return actual is null;
        }

        if (actual is null)
        {
            return false;
        }

        // The target returns everything as text, so a number has to be compared as a number.
        if (decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal want)
            && decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal have))
        {
            return want == have;
        }

        return string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal);
    }

    private static string Normalise(string? value) => value?.Trim() ?? "\u0000";

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
