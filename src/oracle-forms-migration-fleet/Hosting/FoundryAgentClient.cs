// Copyright (c) Microsoft. All rights reserved.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;

namespace OracleFormsMigrationFleet.Hosting;

internal sealed record WorkbenchAgentRequest(string Message);

internal sealed record WorkbenchCloneRequest(string? RepositoryUrl, string? Branch);

internal sealed record WorkbenchAgentResponse(string? ResponseId, string Text);

internal sealed class FoundryAgentClient(HttpClient httpClient, TokenCredential credential, Uri endpoint)
{
    private static readonly string[] s_scopes = ["https://ai.azure.com/.default"];

    public static bool TryParseEndpoint(string? value, out Uri? endpoint)
    {
        bool valid = Uri.TryCreate(value, UriKind.Absolute, out endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps &&
            endpoint.IsDefaultPort &&
            string.IsNullOrEmpty(endpoint.UserInfo) &&
            string.IsNullOrEmpty(endpoint.Fragment) &&
            IsFoundryAccountHost(endpoint.Host) &&
            HasCanonicalAgentPath(endpoint.AbsolutePath) &&
            (string.IsNullOrEmpty(endpoint.Query) || endpoint.Query == "?api-version=v1");

        if (!valid)
        {
            endpoint = null;
        }

        return valid;
    }

    /// <summary>The account label must be a single DNS label, so a lookalike sub-domain cannot satisfy the suffix test.</summary>
    private static bool IsFoundryAccountHost(string host)
    {
        const string suffix = ".services.ai.azure.com";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string account = host[..^suffix.Length];
        return account.Length > 0 && !account.Contains('.');
    }

    /// <summary>
    /// Requires the exact hosted-agent shape rather than a substring match, and rejects percent-encoded
    /// separators and relative segments that <see cref="Uri"/> preserves in <see cref="Uri.AbsolutePath"/>.
    /// </summary>
    private static bool HasCanonicalAgentPath(string absolutePath)
    {
        string[] segments = absolutePath.Split('/');
        if (segments.Length != 10 || segments[0].Length != 0)
        {
            return false;
        }

        for (int index = 1; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (segment.Length == 0 || segment.Contains('%') || segment is "." or "..")
            {
                return false;
            }
        }

        return segments[1] == "api" &&
            segments[2] == "projects" &&
            segments[4] == "agents" &&
            segments[6] == "endpoint" &&
            segments[7] == "protocols" &&
            segments[8] == "openai" &&
            segments[9] == "responses";
    }

    public async Task<WorkbenchAgentResponse> AskAsync(string message, CancellationToken cancellationToken)
    {
        AccessToken token = await credential.GetTokenAsync(
            new TokenRequestContext(s_scopes), cancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new { input = message, stream = false });

        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();
        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        JsonElement root = document.RootElement;
        string? responseId = root.TryGetProperty("id", out JsonElement id) ? id.GetString() : null;
        List<string> text = [];

        if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    {
                        text.Add(value.GetString()!);
                    }
                }
            }
        }

        return new WorkbenchAgentResponse(responseId, string.Join(Environment.NewLine, text));
    }
}