// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class SecretRejectingChatClientTests
{
    [Fact]
    public async Task Credential_material_is_rejected_without_calling_the_inner_client()
    {
        RecordingChatClient innerClient = new();
        SecretRejectingChatClient client = new(innerClient);

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "password=REDACTED_EXAMPLE_NOT_A_REAL_SECRET")]);

        Assert.Equal(0, innerClient.ResponseCalls);
        Assert.Contains("credential material", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REDACTED_EXAMPLE", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_material_is_rejected_without_starting_a_stream()
    {
        RecordingChatClient innerClient = new();
        SecretRejectingChatClient client = new(innerClient);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "api_key: REDACTED_EXAMPLE_NOT_A_REAL_SECRET")]))
        {
            updates.Add(update);
        }

        Assert.Equal(0, innerClient.StreamingCalls);
        ChatResponseUpdate refusal = Assert.Single(updates);
        Assert.Contains("credential material", refusal.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Safe_input_is_delegated_unchanged()
    {
        RecordingChatClient innerClient = new();
        SecretRejectingChatClient client = new(innerClient);
        ChatMessage message = new(ChatRole.User, "The form contains a password field.");

        ChatResponse response = await client.GetResponseAsync([message]);

        Assert.Equal(1, innerClient.ResponseCalls);
        Assert.Same(message, Assert.Single(innerClient.LastMessages));
        Assert.Equal("inner response", response.Text);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int ResponseCalls { get; private set; }

        public int StreamingCalls { get; private set; }

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ResponseCalls++;
            LastMessages = [.. messages];
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "inner response")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            LastMessages = [.. messages];
            yield return new ChatResponseUpdate(ChatRole.Assistant, "inner response");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}