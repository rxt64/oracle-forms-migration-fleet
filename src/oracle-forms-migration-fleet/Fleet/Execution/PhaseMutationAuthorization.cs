// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// What the executor is about to do, offered to an authorizer immediately before the adapter runs.
///
/// The request is the one the executor itself planned from, so an authorizer sees the same facts the
/// planner did. It carries no authority of its own: a caller cannot put a decision in here.
/// </summary>
public sealed record MutationAuthorizationRequest(
    MigrationPhase Phase,
    MutationClass Mutation,
    MigrationRunRequest Request,
    string OperatorIdentity);

/// <summary>An authorizer's answer. A denial always carries the reason the run report will show.</summary>
public sealed record MutationAuthorizationResult(bool IsAuthorized, string Reason)
{
    public static MutationAuthorizationResult Allow(string reason) => new(true, reason);

    public static MutationAuthorizationResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Last check before a phase that writes outside the session workspace.
///
/// This is deliberately not a gate the planner consults: the planner decides what a run may do from the
/// request, and this decides whether the side effect is still authorized at the moment it would happen.
/// A host that has no trustworthy way to answer must deny, because an unanswered question is not a yes.
/// </summary>
public interface IPhaseMutationAuthorizer
{
    Task<MutationAuthorizationResult> AuthorizeAsync(
        MutationAuthorizationRequest request,
        CancellationToken cancellationToken);
}
