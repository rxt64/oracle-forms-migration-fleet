// Copyright (c) Microsoft. All rights reserved.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class ProcessSourceEnvironmentProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-worker-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Worker_configuration_requires_an_existing_absolute_path_and_sha256()
    {
        Assert.False(SourceWorkerOptions.TryRead(name => name == SourceWorkerOptions.PathVariable ? "worker.exe" : "bad", out _));

        Directory.CreateDirectory(_root);
        string worker = Path.Combine(_root, "worker.exe");
        File.WriteAllText(worker, "not executable");
        Assert.True(SourceWorkerOptions.TryRead(name => name switch
        {
            SourceWorkerOptions.PathVariable => worker,
            SourceWorkerOptions.ShaVariable => new string('a', 64),
            _ => null,
        }, out SourceWorkerOptions? options));
        Assert.Equal(Path.GetFullPath(worker), options!.ExecutablePath);
    }

    [Fact]
    public async Task Hash_mismatch_rejects_worker_before_process_start()
    {
        Directory.CreateDirectory(_root);
        string worker = Path.Combine(_root, "worker.exe");
        await File.WriteAllTextAsync(worker, "not executable");
        ProcessSourceEnvironmentProbe probe = new(new(worker, new string('0', 64)));

        SourceEnvironmentProbeResult result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains(result.Capabilities, capability => capability.Id == "forms.worker.integrity");
        Assert.Contains("refused", result.Contradictions.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_verified_image_that_is_not_a_windows_x86_binary_is_refused_before_launch()
    {
        Directory.CreateDirectory(_root);
        string worker = Path.Combine(_root, "worker.exe");
        byte[] content = Encoding.UTF8.GetBytes("hash matches but this is not a PE image");
        await File.WriteAllBytesAsync(worker, content);
        ProcessSourceEnvironmentProbe probe = new(new(worker, Convert.ToHexStringLower(SHA256.HashData(content))));

        SourceEnvironmentProbeResult result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains(result.Capabilities, capability => capability.Id == "forms.worker.image");
    }

    [Fact]
    public async Task A_cancelled_caller_is_never_converted_into_a_probe_verdict()
    {
        Directory.CreateDirectory(_root);
        string worker = Path.Combine(_root, "worker.exe");
        await File.WriteAllTextAsync(worker, "not executable");
        ProcessSourceEnvironmentProbe probe = new(new(worker, new string('0', 64)));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeAsync(Profile(), cancelled.Token));
    }

    [Fact]
    [Trait("Category", "SourceWorkerIntegration")]
    public async Task Published_windows_x86_worker_round_trips_through_the_host_gateway()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Environment.GetEnvironmentVariable("RUN_SOURCE_WORKER_INTEGRATION"), "true", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SourceWorkerOptions.PathVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOURCE_WORKER_INTEGRATION_SENTINEL")))
        {
            return;
        }

        string worker = Assert.IsType<string>(Environment.GetEnvironmentVariable(SourceWorkerOptions.PathVariable));
        await using FileStream workerStream = File.OpenRead(worker);
        byte[] workerHash = await SHA256.HashDataAsync(workerStream);
        ProcessSourceEnvironmentProbe probe = new(new(worker, Convert.ToHexStringLower(workerHash)));

        SourceEnvironmentProbeResult result = await probe.ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.BlockedPrerequisite, result.Status);
        Assert.Contains(result.Capabilities, capability =>
            capability.Id == "forms.installation" &&
            capability.Prerequisite == SourcePrerequisite.OracleFormsInstallation);
        Assert.Contains(result.Capabilities, capability =>
            capability.Id == "forms.openapi.load" &&
            capability.Prerequisite == SourcePrerequisite.OracleFormsOpenApiLibraries);
        string sentinel = Assert.IsType<string>(Environment.GetEnvironmentVariable("SOURCE_WORKER_INTEGRATION_SENTINEL"));
        await File.WriteAllTextAsync(sentinel, "passed");
    }

    [Fact]
    public void Worker_output_is_accepted_only_on_the_exact_contract()
    {
        Assert.True(SourceWorkerProtocol.TryParse(
            Payload(Capability("forms.installation")), "legacy-order-entry", 2,
            out IReadOnlyList<SourceCapabilityResult> capabilities, out DateTimeOffset probedUtc));
        Assert.Equal(
            SourcePrerequisite.OracleFormsInstallation,
            capabilities.Single(capability => capability.Id == "forms.installation").Prerequisite);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), probedUtc);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void Absent_capabilities_are_refused(string capabilities) =>
        Assert.False(Parse(Payload(capabilities)));

    [Fact]
    public void A_partial_or_extra_capability_manifest_is_refused()
    {
        Assert.False(Parse(Payload($"[{Capability("forms.installation")}]")));
        Assert.False(Parse(Payload(
            $"[{RequiredCapabilities()},{Capability("forms.extra", prerequisite: "WorkerHostArchitecture")}]")));
    }

    [Fact]
    public void A_required_capability_with_the_wrong_mapping_is_refused() =>
        Assert.False(Parse(Payload(
            $"[{Capability("forms.installation", prerequisite: "OracleClientConnectivity")}," +
            $"{Capability("forms.openapi.load", prerequisite: "OracleFormsOpenApiLibraries")}," +
            $"{Capability("forms.module.extract", prerequisite: "OperatorSuppliedExport")}]")));

    [Fact]
    public void More_than_thirty_two_capabilities_are_refused_rather_than_truncated() =>
        Assert.False(Parse(Payload(
            "[" + string.Join(",", Enumerable.Range(0, 33).Select(index => Capability($"forms.slot.{index}"))) + "]")));

    [Fact]
    public void Duplicate_capability_identifiers_are_refused() =>
        Assert.False(Parse(Payload(
            $"[{Capability("forms.installation")},{Capability("forms.installation")}]")));

    [Theory]
    [InlineData("2")]
    [InlineData("1,2")]
    [InlineData("blockedprerequisite")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Only_exact_enum_names_are_accepted(string state) =>
        Assert.False(Parse(Payload(Capability("forms.installation", state))));

    [Fact]
    public void An_unnamed_prerequisite_is_refused() =>
        Assert.False(Parse(Payload(Capability("forms.installation", prerequisite: "0"))));

    [Fact]
    public void An_unbounded_capability_identifier_is_refused() =>
        Assert.False(Parse(Payload(Capability(new string('x', 101)))));

    [Fact]
    public void A_non_x86_worker_architecture_is_refused() =>
        Assert.False(Parse(Payload(Capability("forms.installation"), architecture: "X64")));

    [Theory]
    [InlineData("Verified")]
    [InlineData("Contradicted")]
    public void A_worker_cannot_claim_an_observed_capability(string state) =>
        Assert.False(Parse(Payload(Capability("forms.installation", state))));

    [Theory]
    [InlineData("Forms_Installation", "operator.install.forms.6i.worker")]
    [InlineData("forms.installation", "Operator_Install")]
    public void Capability_tokens_must_match_the_persistence_identifier_contract(string id, string remediation) =>
        Assert.False(Parse(Payload(Capability(id, remediation: remediation))));

    [Fact]
    public void A_worker_that_does_not_echo_the_expected_release_is_refused() =>
        Assert.False(Parse(Payload(Capability("forms.installation"), release: "6.0.8.22.2")));

    [Fact]
    public void An_unexpected_worker_status_is_refused() =>
        Assert.False(Parse(Payload(Capability("forms.installation"), status: "Verified")));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    public void An_unexpected_exit_code_is_refused(int exitCode) =>
        Assert.False(SourceWorkerProtocol.TryParse(
            Payload(Capability("forms.installation")), "legacy-order-entry", exitCode, out _, out _));

    [Fact]
    public void A_foreign_source_environment_identifier_is_refused() =>
        Assert.False(SourceWorkerProtocol.TryParse(
            Payload(Capability("forms.installation")), "other-environment", 2, out _, out _));

    [Fact]
    public void Malformed_worker_output_is_refused()
    {
        Assert.False(Parse("{\"schemaVersion\":"));
        Assert.False(Parse(string.Empty));
    }

    [Fact]
    public void Only_a_thirty_two_bit_windows_image_is_accepted()
    {
        Assert.True(SourceWorkerImage.IsWindowsX86(Image(0x014C)));
        Assert.False(SourceWorkerImage.IsWindowsX86(Image(0x8664)));
        Assert.False(SourceWorkerImage.IsWindowsX86(Image(0xAA64)));
        Assert.False(SourceWorkerImage.IsWindowsX86(Image(0x014C, dos: 0x4D5A)));
        Assert.False(SourceWorkerImage.IsWindowsX86(Image(0x014C).AsSpan(0, 0x20)));
        Assert.False(SourceWorkerImage.IsWindowsX86(Image(0x014C, peOffset: 0x200)));
    }

    [Fact]
    public void The_worker_image_size_budget_is_closed_at_both_ends()
    {
        Assert.False(SourceWorkerImage.IsAcceptableLength(0));
        Assert.False(SourceWorkerImage.IsAcceptableLength(SourceWorkerImage.MaxImageBytes + 1));
        Assert.True(SourceWorkerImage.IsAcceptableLength(1));
        Assert.True(SourceWorkerImage.IsAcceptableLength(SourceWorkerImage.MaxImageBytes));
    }

    private static bool Parse(string payload) =>
        SourceWorkerProtocol.TryParse(payload, "legacy-order-entry", 2, out _, out _);

    private static SourceEnvironmentProfile Profile() => SourceEnvironmentProfiles.Create(
        "tenant", "project", "legacy-order-entry", 1, "Legacy Order Entry",
        SourceConnector.FormsBuilderWorker, "6i", "9i", "legacy-order-entry", ["LEGACY_LAB"], [],
        DateTimeOffset.UtcNow);

    private static string Payload(
        string capabilities,
        string status = "BlockedPrerequisite",
        string architecture = "X86",
        string release = "6.0.8.22.1") =>
        $$"""
        {"schemaVersion":1,"sourceEnvironmentId":"legacy-order-entry","status":"{{status}}",
         "probedUtc":"2026-01-01T00:00:00+00:00","operatingSystem":"Windows",
         "processArchitecture":"{{architecture}}","expectedFormsRelease":"{{release}}",
                 "capabilities":{{(capabilities.StartsWith('[') || capabilities == "null" ? capabilities : $"[{capabilities},{RemainingRequiredCapabilities(capabilities)}]")}}}
        """;

        private static string RequiredCapabilities() =>
                $"{Capability("forms.installation")}," +
                $"{Capability("forms.openapi.load", prerequisite: "OracleFormsOpenApiLibraries", remediation: "operator.supply.authorized.forms.libraries")}," +
                Capability("forms.module.extract", prerequisite: "OperatorSuppliedExport", remediation: "operator.supply.authorized.source.export");

        private static string RemainingRequiredCapabilities(string provided) =>
                provided.Contains("forms.installation", StringComparison.Ordinal)
                        ? $"{Capability("forms.openapi.load", prerequisite: "OracleFormsOpenApiLibraries", remediation: "operator.supply.authorized.forms.libraries")}," +
                            Capability("forms.module.extract", prerequisite: "OperatorSuppliedExport", remediation: "operator.supply.authorized.source.export")
                        : RequiredCapabilities();

    private static string Capability(
        string id,
        string state = "BlockedPrerequisite",
        string prerequisite = "OracleFormsInstallation",
        string remediation = "operator.install.forms.6i.worker") =>
        $$"""
        {"id":"{{id}}","state":"{{state}}","prerequisite":"{{prerequisite}}","requiredRelease":"6.0.8.22.1",
         "requiredArchitecture":"x86","requiredHost":"WindowsWorker","remediation":"{{remediation}}"}
        """;

    private static byte[] Image(ushort machine, ushort dos = 0x5A4D, int peOffset = 0x40)
    {
        byte[] image = new byte[0x80];
        BinaryPrimitives.WriteUInt16LittleEndian(image, dos);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
        if (peOffset <= image.Length - 6)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(peOffset), 0x0000_4550);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), machine);
        }
        return image;
    }
}