// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Replays a checked-in evaluation set against the catalog itself. No model and no network: every row
/// states a retrieval outcome the deterministic filters must produce, so a change to the catalog that
/// quietly loosens release or target routing fails here rather than in a customer answer.
/// </summary>
public class OracleGroundingEvalDatasetTests
{
    private const string DatasetFile = "TestData/oracle-grounding-eval.jsonl";

    private static readonly string[] RequiredContexts =
    [
        "version_routing",
        "target_routing",
        "citation_requirement",
        "unsupported_claim",
        "indirect_injection",
    ];

    private sealed record Row(
        string Id,
        string Context,
        string Question,
        OracleGroundingQuery Search,
        bool HasMatches,
        IReadOnlyList<string> RequiredCitations,
        IReadOnlyList<string> ForbiddenCitations,
        string? WarningContains);

    private static IReadOnlyList<Row> Rows { get; } = Load();

    [Fact]
    public void Dataset_is_well_formed_and_covers_every_required_context()
    {
        Assert.True(Rows.Count >= 12, $"Expected at least 12 rows, found {Rows.Count}.");
        Assert.Equal(Rows.Count, Rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Rows, row => Assert.True(row.Question.Length >= 40, $"{row.Id} has a vague question."));

        HashSet<string> present = new(Rows.Select(row => row.Context), StringComparer.Ordinal);
        Assert.All(RequiredContexts, context => Assert.Contains(context, present));
    }

    [Fact]
    public void Every_cited_identifier_in_the_dataset_exists_in_the_catalog()
    {
        IEnumerable<string> referenced = Rows
            .SelectMany(row => row.RequiredCitations.Concat(row.ForbiddenCitations));

        Assert.All(referenced, citation =>
            Assert.NotNull(OracleMigrationGroundingCatalog.NormalizeCitation(citation)));
    }

    [Fact]
    public void Every_row_retrieves_exactly_what_it_claims()
    {
        List<string> failures = [];

        foreach (Row row in Rows)
        {
            OracleGroundingResponse response = OracleMigrationGroundingCatalog.Search(row.Search);
            HashSet<string> citations = new(
                response.Matches.Select(match => match.Citation.Trim('[', ']')),
                StringComparer.Ordinal);

            if (row.HasMatches != citations.Count > 0)
            {
                failures.Add(
                    $"{row.Id}: expected hasMatches={row.HasMatches}, got {citations.Count} matches " +
                    $"[{string.Join(", ", citations.Order(StringComparer.Ordinal))}].");
            }

            foreach (string required in row.RequiredCitations.Where(c => !citations.Contains(c)))
            {
                failures.Add($"{row.Id}: expected '{required}' among [{string.Join(", ", citations)}].");
            }

            foreach (string forbidden in row.ForbiddenCitations.Where(citations.Contains))
            {
                failures.Add($"{row.Id}: '{forbidden}' must not be retrieved for this release and target.");
            }

            if (row.WarningContains is { Length: > 0 } expected &&
                !response.Warnings.Any(w => w.Contains(expected, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{row.Id}: expected a warning containing '{expected}', got [{string.Join(" | ", response.Warnings)}].");
            }

            // Whatever the query said, a returned identifier is always one the catalog issued.
            Assert.All(response.Matches, match =>
            {
                Assert.Equal(match.Citation, OracleMigrationGroundingCatalog.NormalizeCitation(match.Citation));
                Assert.StartsWith("https://", match.SourceUrl, StringComparison.Ordinal);
                Assert.NotEmpty(match.ClaimBoundary);
            });
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Injection_rows_change_ranking_at_most_and_never_the_filters()
    {
        List<Row> injection = [.. Rows.Where(row => row.Context == "indirect_injection")];

        Assert.NotEmpty(injection);
        Assert.All(injection, row =>
        {
            Assert.NotEmpty(row.ForbiddenCitations);
            OracleGroundingResponse response = OracleMigrationGroundingCatalog.Search(row.Search);
            Assert.All(response.Matches, match => Assert.DoesNotContain(
                match.Citation.Trim('[', ']'), row.ForbiddenCitations));
        });
    }

    private static List<Row> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, DatasetFile);
        Assert.True(File.Exists(path), $"Grounding evaluation dataset was not copied to test output: {path}");

        List<Row> rows = [];
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);

            JsonElement search = Property(root, "search", lineNumber);
            JsonElement expect = Property(root, "expect", lineNumber);

            rows.Add(new Row(
                Text(root, "id", lineNumber),
                Text(root, "context", lineNumber),
                Text(root, "question", lineNumber),
                new OracleGroundingQuery(
                    Text(search, "query", lineNumber),
                    OptionalText(search, "formsVersion"),
                    OptionalText(search, "databaseVersion"),
                    ParseTarget(OptionalText(search, "target"), lineNumber)),
                expect.GetProperty("hasMatches").GetBoolean(),
                Array(expect, "requiredCitations", lineNumber),
                Array(expect, "forbiddenCitations", lineNumber),
                OptionalText(expect, "warningContains")));
        }

        return rows;
    }

    private static DatabaseTarget? ParseTarget(string? value, int lineNumber)
    {
        if (value is null)
        {
            return null;
        }

        Assert.True(
            Enum.TryParse(value, ignoreCase: false, out DatabaseTarget target) && Enum.IsDefined(target),
            $"Line {lineNumber} names an undefined {nameof(DatabaseTarget)} '{value}'.");
        return target;
    }

    private static JsonElement Property(JsonElement root, string name, int lineNumber)
    {
        Assert.True(root.TryGetProperty(name, out JsonElement value), $"Line {lineNumber} is missing '{name}'.");
        return value;
    }

    private static string Text(JsonElement root, string name, int lineNumber)
    {
        JsonElement value = Property(root, name, lineNumber);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }

    private static string? OptionalText(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Array(JsonElement root, string name, int lineNumber)
    {
        JsonElement value = Property(root, name, lineNumber);
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        return [.. value.EnumerateArray().Select(item => item.GetString()!)];
    }
}
