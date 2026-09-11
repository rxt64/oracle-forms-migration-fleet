// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Rejects credential-like chat content before delegating to model inference.</summary>
public sealed class SecretRejectingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public const string RejectionMessage =
        "Request rejected because it appears to contain credential material. " +
        "Remove secrets and reference artifacts by name only.";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> bufferedMessages = [.. messages];
        if (!ContainsPotentialSecret(bufferedMessages))
        {
            return base.GetResponseAsync(bufferedMessages, options, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, RejectionMessage)));
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> bufferedMessages = [.. messages];
        if (ContainsPotentialSecret(bufferedMessages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, RejectionMessage);
            yield break;
        }

        await foreach (ChatResponseUpdate update in
                       base.GetStreamingResponseAsync(bufferedMessages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private static bool ContainsPotentialSecret(IEnumerable<ChatMessage> messages) =>
        messages.Any(message => FleetGuardrails.ContainsPotentialSecret(message.Text));
}