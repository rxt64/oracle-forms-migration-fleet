// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Agents;

/// <summary>Why an orchestrated exchange stopped. Every run ends on one of these, never by running out of road.</summary>
public enum TerminationReason
{
    /// <summary>The critic found nothing further to raise.</summary>
    Settled,

    /// <summary>The step budget was spent. The result so far still stands, and says so.</summary>
    BudgetExhausted,

    /// <summary>An agent failed and the exchange stopped rather than building on a broken step.</summary>
    AgentFailed,

    /// <summary>The caller cancelled.</summary>
    Cancelled,
}

/// <summary>What an agent was asked to do. Agents receive content and context, never a credential or an endpoint.</summary>
public sealed record FleetAgentRequest(
    string ApplicationName,
    DatabaseTarget Target,
    string Artifact,
    IReadOnlyList<string> Findings);

/// <summary>
/// A typed agent result. Free text is not a result: an agent that cannot produce a structured answer has
/// failed, and the orchestrator treats it that way rather than passing prose to the next step.
/// </summary>
public sealed record FleetAgentResult(
    bool Succeeded,
    string? Artifact,
    IReadOnlyList<string> Findings,
    string Summary)
{
    public static FleetAgentResult Failed(string summary) => new(false, null, [], summary);
}

/// <summary>
/// One participant in an orchestrated exchange.
///
/// <see cref="Role"/> is a real dispatch target here, unlike the labels on a <see cref="PhasePlan"/>: the
/// orchestrator selects agents by role and records which one produced each step.
/// </summary>
public interface IFleetAgent
{
    FleetRole Role { get; }

    string Name { get; }

    Task<FleetAgentResult> RunAsync(FleetAgentRequest request, CancellationToken cancellationToken);
}

public sealed record OrchestrationStep(int Index, FleetRole Role, string Agent, bool Succeeded, string Summary);

/// <summary>
/// Transcript of an exchange. The revised artifact is a <em>proposal</em>: the caller decides whether to
/// keep it, and the deterministic output it was derived from is never overwritten in place.
/// </summary>
public sealed record OrchestrationResult(
    string? ProposedArtifact,
    IReadOnlyList<string> OutstandingFindings,
    IReadOnlyList<OrchestrationStep> Steps,
    TerminationReason Termination)
{
    public bool ProducedRevision => ProposedArtifact is { Length: > 0 };
}

/// <summary>
/// Runs a critic-and-repair exchange over a generated artifact.
///
/// This is the one place the fleet is genuinely multi-agent, and it is deliberately a bounded workflow
/// rather than an open conversation: the work has a known shape, so agents alternate on a fixed rotation
/// under a step budget and an explicit termination condition. Nothing here decides whether a phase may
/// run, and nothing it produces replaces a deterministic artifact. Agents propose; the caller and the
/// planner dispose.
/// </summary>
public sealed class CritiqueRepairOrchestrator(
    IFleetAgent critic,
    IFleetAgent repairer,
    int maxRounds = 2)
{
    private readonly int _maxRounds = maxRounds > 0 ? maxRounds : 1;

    public async Task<OrchestrationResult> RunAsync(
        FleetAgentRequest request,
        Action<OrchestrationStep>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<OrchestrationStep> steps = [];
        string current = request.Artifact;
        string? revision = null;
        IReadOnlyList<string> outstanding = [];
        int index = 0;

        for (int round = 0; round < _maxRounds; round++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new OrchestrationResult(revision, outstanding, steps, TerminationReason.Cancelled);
            }

            FleetAgentResult critique = await Step(
                critic, request with { Artifact = current }, steps, progress, ++index, cancellationToken).ConfigureAwait(false);

            if (!critique.Succeeded)
            {
                return new OrchestrationResult(revision, outstanding, steps, TerminationReason.AgentFailed);
            }

            outstanding = critique.Findings;
            if (outstanding.Count == 0)
            {
                return new OrchestrationResult(revision, outstanding, steps, TerminationReason.Settled);
            }

            FleetAgentResult repair = await Step(
                repairer,
                request with { Artifact = current, Findings = outstanding },
                steps,
                progress,
                ++index,
                cancellationToken).ConfigureAwait(false);

            if (!repair.Succeeded)
            {
                return new OrchestrationResult(revision, outstanding, steps, TerminationReason.AgentFailed);
            }

            // A repairer that changed nothing means another round would repeat itself.
            if (repair.Artifact is not { Length: > 0 } proposed || string.Equals(proposed, current, StringComparison.Ordinal))
            {
                return new OrchestrationResult(revision, outstanding, steps, TerminationReason.Settled);
            }

            current = proposed;
            revision = proposed;
        }

        return new OrchestrationResult(revision, outstanding, steps, TerminationReason.BudgetExhausted);
    }

    private static async Task<FleetAgentResult> Step(
        IFleetAgent agent,
        FleetAgentRequest request,
        List<OrchestrationStep> steps,
        Action<OrchestrationStep>? progress,
        int index,
        CancellationToken cancellationToken)
    {
        FleetAgentResult result;
        try
        {
            result = await agent.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = FleetAgentResult.Failed($"{agent.Name} failed: {Execution.FailureText.Describe(exception)}");
        }

        OrchestrationStep step = new(index, agent.Role, agent.Name, result.Succeeded, result.Summary);
        steps.Add(step);
        progress?.Invoke(step);

        return result;
    }
}
