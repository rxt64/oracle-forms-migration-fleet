// Copyright (c) Microsoft. All rights reserved.

using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record BehaviourStep(string Name, string Action);

public sealed record BehaviourScenario(
    string Name,
    string? Description,
    string? Module,
    string? LegacyPackage,
    string? ModernEndpoint,
    IReadOnlyList<BehaviourStep> Steps);

public sealed record ScenarioCoverage(
    BehaviourScenario Scenario,
    IReadOnlyList<string> BlockedBy);

/// <summary>
/// Reads the scenario files a test harness uses as its executable specification.
///
/// This is not a test runner and does not execute anything. It reads what a scenario says it depends on
/// so the conversion can report which documented behaviours rest on code that did not migrate. Saying
/// "eleven routines were refused" tells an engineer where to look; saying which business scenarios those
/// routines carry tells everyone else whether it matters.
/// </summary>
public static class ScenarioReader
{
    private static readonly string[] s_scalarKeys =
        ["name", "description", "module", "legacy_package", "modern_endpoint"];

    public static BehaviourScenario? Read(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        Dictionary<string, string> scalars = new(StringComparer.OrdinalIgnoreCase);
        List<BehaviourStep> steps = [];
        bool inSteps = false;
        string? stepName = null;
        string? stepAction = null;

        foreach (string raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            string trimmed = line.Trim();

            if (indent == 0 && trimmed.StartsWith("steps:", StringComparison.OrdinalIgnoreCase))
            {
                inSteps = true;
                continue;
            }

            if (indent == 0 && !trimmed.StartsWith('-'))
            {
                // A new top-level key ends the step list.
                inSteps = false;
                Flush(steps, ref stepName, ref stepAction);

                if (TryScalar(trimmed, out string key, out string value) && s_scalarKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    scalars[key] = value;
                }

                continue;
            }

            if (!inSteps)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush(steps, ref stepName, ref stepAction);
                trimmed = trimmed[2..].Trim();
            }

            if (TryScalar(trimmed, out string stepKey, out string stepValue))
            {
                if (stepKey.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    stepName = stepValue;
                }
                else if (stepKey.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    stepAction = stepValue;
                }
            }
        }

        Flush(steps, ref stepName, ref stepAction);

        return scalars.TryGetValue("name", out string? name) && name.Length > 0
            ? new BehaviourScenario(
                name,
                scalars.GetValueOrDefault("description"),
                scalars.GetValueOrDefault("module"),
                scalars.GetValueOrDefault("legacy_package"),
                scalars.GetValueOrDefault("modern_endpoint"),
                steps)
            : null;
    }

    /// <summary>Pairs each scenario with the refused program units its legacy package owns.</summary>
    public static IReadOnlyList<ScenarioCoverage> Cover(
        IReadOnlyList<BehaviourScenario> scenarios,
        IReadOnlyList<ConversionFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(findings);

        List<ScenarioCoverage> coverage = [];

        foreach (BehaviourScenario scenario in scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.LegacyPackage))
            {
                coverage.Add(new ScenarioCoverage(scenario, []));
                continue;
            }

            string prefix = scenario.LegacyPackage.Trim();

            string[] blocked =
                [.. findings
                    .Where(finding => finding.Severity == ConversionSeverity.Unsupported
                        && finding.Category == "Program unit"
                        && finding.Construct.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                    .Select(finding => finding.Construct)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)];

            coverage.Add(new ScenarioCoverage(scenario, blocked));
        }

        return coverage;
    }

    public static string Render(string applicationName, IReadOnlyList<ScenarioCoverage> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        StringBuilder builder = new();
        builder.AppendLine("# Behaviour coverage").AppendLine();
        builder.Append("Application: ").AppendLine(applicationName);
        builder.AppendLine();
        builder.AppendLine("Nothing here was executed. These are the behaviours a supplied test baseline documents,");
        builder.AppendLine("matched against the program units this conversion refused to translate. A scenario with");
        builder.AppendLine("no blockers is not a passing test; it means nothing it depends on is known to be missing.");
        builder.AppendLine();

        int blockedCount = coverage.Count(entry => entry.BlockedBy.Count > 0);
        builder.Append(blockedCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append(" of ")
               .Append(coverage.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .AppendLine(" documented scenarios depend on logic that did not migrate.")
               .AppendLine();

        foreach (ScenarioCoverage entry in coverage)
        {
            builder.Append("## ").AppendLine(entry.Scenario.Name);
            builder.AppendLine();

            if (entry.Scenario.Description is { Length: > 0 } description)
            {
                builder.AppendLine(description).AppendLine();
            }

            builder.Append("- Legacy package: ").AppendLine(entry.Scenario.LegacyPackage ?? "not stated");
            builder.Append("- Modern endpoint: ").AppendLine(entry.Scenario.ModernEndpoint ?? "not stated");
            builder.Append("- Steps: ").AppendLine(entry.Scenario.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.AppendLine();

            if (entry.BlockedBy.Count == 0)
            {
                builder.AppendLine("No refused program unit belongs to this package.").AppendLine();
                continue;
            }

            builder.AppendLine("Depends on program units that were not translated:").AppendLine();
            foreach (string blocker in entry.BlockedBy)
            {
                builder.Append("- ").AppendLine(blocker);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void Flush(List<BehaviourStep> steps, ref string? name, ref string? action)
    {
        if (name is { Length: > 0 } || action is { Length: > 0 })
        {
            steps.Add(new BehaviourStep(name ?? "unnamed", action ?? "unstated"));
        }

        name = null;
        action = null;
    }

    private static bool TryScalar(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        key = line[..colon].Trim();
        value = line[(colon + 1)..].Trim().Trim('"');
        return key.Length > 0 && !key.Contains(' ', StringComparison.Ordinal);
    }
}
