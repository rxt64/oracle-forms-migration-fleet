// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;
using OracleFormsMigrationFleet.Fleet.Agents;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record ProgramUnitRepairOutcome(
    IReadOnlyList<string> AcceptedStatements,
    IReadOnlyList<string> AttemptedStatements,
    IReadOnlyList<string> OutstandingFailures,
    int Attempts)
{
    public bool Succeeded => OutstandingFailures.Count == 0;
}

/// <summary>
/// Gives rejected generated routines to the repair model and lets PostgreSQL, not the model, decide
/// whether each revision is acceptable. The execution-approved sandbox phase owns this loop.
/// </summary>
public sealed class ProgramUnitRepairLoop(IFleetAgent repairer, int maxAttempts = 2)
{
    private static readonly Regex s_routine = new(
        @"^\s*create\s+or\s+replace\s+(?:function|procedure)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_dollarQuote = new(
        @"\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$",
        RegexOptions.CultureInvariant);

    private readonly int _maxAttempts = Math.Max(1, maxAttempts);

    public async Task<ProgramUnitRepairOutcome> RunAsync(
        string applicationName,
        IDataMigrationGateway gateway,
        SchemaDeploymentOutcome rejected,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(rejected);

        IReadOnlyList<SchemaStatementFailure> current = rejected.StatementFailures;
        if (current.Count == 0)
        {
            return new ProgramUnitRepairOutcome([], [], rejected.Failures, 0);
        }

        List<string> accepted = [];
        List<string> attempted = [];
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string artifact = string.Join(";\n\n", current.Select(failure => failure.Statement));
            FleetAgentResult proposal;
            try
            {
                proposal = await repairer.RunAsync(
                    new FleetAgentRequest(
                        applicationName,
                        DatabaseTarget.PostgreSql,
                        artifact,
                        [.. current.Select(failure => failure.Diagnostic)]),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ProgramUnitRepairOutcome(
                    accepted,
                    attempted,
                    [$"The program-unit repair agent failed: {FailureText.Describe(exception)}"],
                    attempt);
            }

            progress?.Invoke($"Program-unit repair attempt {attempt}: {proposal.Summary}");
            if (!proposal.Succeeded || proposal.Artifact is not { Length: > 0 } sql)
            {
                if (attempt < _maxAttempts && IsTransient(proposal.Summary))
                {
                    continue;
                }

                return new ProgramUnitRepairOutcome(accepted, attempted, [proposal.Summary], attempt);
            }

            IReadOnlyList<string> candidates = DataMigrationTranslator.SplitSchema(sql);
            if (!ChangesBodiesOnly(current.Select(failure => failure.Statement), candidates))
            {
                return new ProgramUnitRepairOutcome(
                    accepted,
                    attempted,
                    ["The proposed repair changed routine identities or included a non-routine statement."],
                    attempt);
            }

            attempted.AddRange(candidates);
            SchemaDeploymentOutcome compiled;
            try
            {
                compiled = await gateway.PrepareAsync(candidates, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ProgramUnitRepairOutcome(
                    accepted,
                    attempted,
                    [$"The target could not verify the proposed repair: {FailureText.Describe(exception)}"],
                    attempt);
            }

            if (compiled.Failures.Count != compiled.StatementFailures.Count)
            {
                return new ProgramUnitRepairOutcome(accepted, attempted, compiled.Failures, attempt);
            }

            accepted.AddRange(candidates.Except(
                compiled.StatementFailures.Select(failure => failure.Statement),
                StringComparer.Ordinal));

            if (compiled.Failures.Count == 0)
            {
                return new ProgramUnitRepairOutcome(accepted, attempted, [], attempt);
            }

            current = compiled.StatementFailures;
        }

        return new ProgramUnitRepairOutcome(
            accepted,
            attempted,
            [.. current.Select(failure => failure.Diagnostic)],
            _maxAttempts);
    }

    internal static bool ChangesBodiesOnly(
        IEnumerable<string> original,
        IReadOnlyList<string> candidates)
    {
        string[] expected = [.. original.Select(RoutineEnvelope).Order(StringComparer.Ordinal)];
        string[] proposed = [.. candidates.Select(RoutineEnvelope).Order(StringComparer.Ordinal)];

        return expected.Length > 0
            && expected.Length == proposed.Length
            && expected.All(envelope => envelope.Length > 0)
            && proposed.All(envelope => envelope.Length > 0)
            && expected.SequenceEqual(proposed, StringComparer.Ordinal);
    }

    internal static bool IsBodyOnlySubset(
        IEnumerable<string> original,
        IReadOnlyList<string> candidates,
        out IReadOnlySet<string> replacedEnvelopes)
    {
        HashSet<string> expected = [.. original.Select(RoutineEnvelope).Where(envelope => envelope.Length > 0)];
        HashSet<string> proposed = [.. candidates.Select(RoutineEnvelope).Where(envelope => envelope.Length > 0)];
        replacedEnvelopes = proposed;

        return candidates.Count > 0
            && proposed.Count == candidates.Count
            && proposed.IsSubsetOf(expected);
    }

    internal static string RoutineEnvelope(string statement)
    {
        if (!s_routine.IsMatch(statement))
        {
            return string.Empty;
        }

        Match? opening = FindBodyOpening(statement);
        if (opening is null)
        {
            return string.Empty;
        }

        int closing = statement.IndexOf(opening.Value, opening.Index + opening.Length, StringComparison.Ordinal);
        return closing < 0
            ? string.Empty
            : string.Concat(
                statement.AsSpan(0, opening.Index).Trim(),
                opening.Value,
                "\0",
                statement.AsSpan(closing).Trim());
    }

    private static Match? FindBodyOpening(string statement)
    {
        string lastToken = string.Empty;
        for (int index = 0; index < statement.Length; index++)
        {
            char current = statement[index];
            if (current == '\'' || current == '"')
            {
                char delimiter = current;
                while (++index < statement.Length)
                {
                    if (statement[index] != delimiter)
                    {
                        continue;
                    }

                    if (index + 1 < statement.Length && statement[index + 1] == delimiter)
                    {
                        index++;
                        continue;
                    }

                    break;
                }

                continue;
            }

            if (current == '-' && index + 1 < statement.Length && statement[index + 1] == '-')
            {
                index = statement.IndexOf('\n', index + 2);
                if (index < 0)
                {
                    return null;
                }

                continue;
            }

            if (current == '/' && index + 1 < statement.Length && statement[index + 1] == '*')
            {
                int end = statement.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                index = end + 1;
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                int start = index++;
                while (index < statement.Length && (char.IsLetterOrDigit(statement[index]) || statement[index] is '_' or '$'))
                {
                    index++;
                }

                lastToken = statement[start..index];
                index--;
                continue;
            }

            if (current == '$')
            {
                Match dollarQuote = s_dollarQuote.Match(statement, index);
                if (!dollarQuote.Success || dollarQuote.Index != index)
                {
                    continue;
                }

                if (string.Equals(lastToken, "AS", StringComparison.OrdinalIgnoreCase))
                {
                    return dollarQuote;
                }

                int closing = statement.IndexOf(
                    dollarQuote.Value,
                    dollarQuote.Index + dollarQuote.Length,
                    StringComparison.Ordinal);
                if (closing < 0)
                {
                    return null;
                }

                index = closing + dollarQuote.Length - 1;
            }
        }

        return null;
    }

    private static bool IsTransient(string summary) =>
        summary.Contains("429", StringComparison.OrdinalIgnoreCase)
        || summary.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || summary.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
}