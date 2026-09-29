// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.InteropServices;
using System.Text.Json;
using OracleFormsMigrationFleet.SourceWorker;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

public sealed class SourceWorkerExtractionTests
{
    private static readonly WorkerHost s_qualifiedHost = new(true, Architecture.X86, "Windows (test)");

    private static WorkerExtractionRequest Request(string alias, string contentHash) =>
        new(WorkerProtocol.SchemaVersion, "source-lab-forms6i", "6.0.8.11.3", 3, new string('c', 64), alias, contentHash);

    /// <summary>
    /// Builds a module with two blocks, items of differing types, and triggers at module, block and item
    /// scope, so the traversal has sibling chains and nested owners to walk rather than a single node.
    ///
    /// It declares no program unit and no list of values, because this worker cannot read their
    /// definitions and refuses a module that carries them. This is the shape of the modules it can read:
    /// blocks, items, and trigger bodies, including a button trigger that calls the database directly.
    /// </summary>
    private static (FakeFormsObjectGraph Graph, nint Module) Estate()
    {
        FakeFormsObjectGraph graph = new();
        FormsPropertyCodes codes = FakeFormsObjectGraph.Codes;

        nint module = graph.Create();
        graph.At(module).Texts[codes.Name] = "MRD_ORDER_ENTRY";
        graph.At(module).Texts[codes.Title] = "Meridian Order Entry";

        nint moduleTrigger = graph.Append(module, codes.FirstTrigger);
        graph.At(moduleTrigger).Texts[codes.Name] = "WHEN-NEW-FORM-INSTANCE";
        graph.At(moduleTrigger).Texts[codes.TriggerText] = "BEGIN\r\n  GO_BLOCK('ORDER_ENTRY');\r\nEND;";

        nint orders = graph.Append(module, codes.FirstBlock);
        graph.At(orders).Texts[codes.Name] = "ORDER_ENTRY";
        graph.At(orders).Texts[codes.BaseTable] = "MRD_ORDERS";
        graph.At(orders).Numbers[codes.RecordsDisplayed] = 5;

        nint customer = graph.Append(orders, codes.FirstItem);
        graph.At(customer).Texts[codes.Name] = "CUSTOMER_ID";
        graph.At(customer).Texts[codes.Prompt] = "Customer";
        graph.At(customer).Texts[codes.ColumnName] = "CUSTOMER_ID";
        graph.At(customer).Numbers[codes.ItemType] = 3;
        graph.At(customer).Numbers[codes.DataType] = 2;
        graph.At(customer).Numbers[codes.MaxLength] = 240;
        graph.At(customer).Booleans[codes.Required] = true;
        graph.At(customer).Booleans[codes.Visible] = true;

        nint itemTrigger = graph.Append(customer, codes.FirstTrigger);
        graph.At(itemTrigger).Texts[codes.Name] = "WHEN-VALIDATE-ITEM";
        graph.At(itemTrigger).Texts[codes.TriggerText] = "BEGIN\n  NULL;\nEND;";

        nint quantity = graph.Append(orders, codes.FirstItem);
        graph.At(quantity).Texts[codes.Name] = "QUANTITY";
        graph.At(quantity).Texts[codes.Prompt] = "Quantity";
        graph.At(quantity).Numbers[codes.ItemType] = 1;
        graph.At(quantity).Numbers[codes.DataType] = 2;
        graph.At(quantity).Booleans[codes.Visible] = true;

        nint blockTrigger = graph.Append(orders, codes.FirstTrigger);
        graph.At(blockTrigger).Texts[codes.Name] = "WHEN-NEW-RECORD-INSTANCE";

        nint post = graph.Append(orders, codes.FirstItem);
        graph.At(post).Texts[codes.Name] = "POST_ORDER";
        graph.At(post).Numbers[codes.ItemType] = 4;
        graph.At(post).Booleans[codes.Visible] = true;

        nint postTrigger = graph.Append(post, codes.FirstTrigger);
        graph.At(postTrigger).Texts[codes.Name] = "WHEN-BUTTON-PRESSED";
        graph.At(postTrigger).Texts[codes.TriggerText] = "BEGIN\n  MRD_ORDERS_PKG.POST(:ORDER_ENTRY.CUSTOMER_ID);\nEND;";

        nint audit = graph.Append(module, codes.FirstBlock);
        graph.At(audit).Texts[codes.Name] = "AUDIT_TRAIL";
        graph.At(audit).Numbers[codes.RecordsDisplayed] = 1;

        nint stamp = graph.Append(audit, codes.FirstItem);
        graph.At(stamp).Texts[codes.Name] = "CHANGED_AT";
        graph.At(stamp).Numbers[codes.ItemType] = 2;

        return (graph, module);
    }

    [Fact]
    public async Task Extraction_walks_the_whole_module_and_writes_a_hashed_neutral_representation()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        ExtractionService service = new(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost);
        WorkerExtractionResult result = await service.ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal("Extracted", result.Status);
        Assert.Equal("MRD_ORDER_ENTRY", result.ModuleIdentity);
        Assert.Equal(hash, result.ObservedContentSha256);
        Assert.NotNull(result.IntermediateRepresentationPath);

        // The reported digest is of the bytes on disk, not of a re-serialization that could drift from them.
        byte[] written = await File.ReadAllBytesAsync(result.IntermediateRepresentationPath!, CancellationToken.None);
        Assert.Equal(ContentHash.OfBytes(written), result.IntermediateRepresentationSha256);

        using JsonDocument document = JsonDocument.Parse(written);
        JsonElement emitted = document.RootElement.GetProperty("modules")[0];
        Assert.Equal("MRD_ORDER_ENTRY", emitted.GetProperty("name").GetString());
        Assert.Equal("Meridian Order Entry", emitted.GetProperty("title").GetString());
        Assert.Empty(emitted.GetProperty("programUnits").EnumerateArray());
        Assert.Empty(emitted.GetProperty("lovs").EnumerateArray());

        JsonElement blocks = emitted.GetProperty("blocks");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("MRD_ORDERS", blocks[0].GetProperty("baseTable").GetString());
        Assert.Equal(5, blocks[0].GetProperty("recordsDisplayed").GetInt32());

        JsonElement customer = blocks[0].GetProperty("items")[0];
        Assert.Equal("CUSTOMER_ID", customer.GetProperty("name").GetString());
        Assert.Equal("LS", customer.GetProperty("itemType").GetString());
        Assert.Equal("NUMBER", customer.GetProperty("dataType").GetString());
        Assert.Equal(240, customer.GetProperty("maxLength").GetInt32());
        Assert.True(customer.GetProperty("required").GetBoolean());

        // An item with no explicit visibility is visible, matching the Forms default rather than silently false.
        Assert.True(blocks[0].GetProperty("items")[1].GetProperty("visible").GetBoolean());

        // Block triggers carry the block scope; item triggers are attached to the block under the qualified scope.
        string[] scopes = [.. blocks[0].GetProperty("triggers").EnumerateArray().Select(trigger => trigger.GetProperty("scope").GetString()!)];
        Assert.Contains("ORDER_ENTRY", scopes);
        Assert.Contains("ORDER_ENTRY.CUSTOMER_ID", scopes);

        // Trigger bodies are retained verbatim with line endings normalized, never translated.
        JsonElement moduleTrigger = emitted.GetProperty("triggers")[0];
        Assert.Equal("WHEN-NEW-FORM-INSTANCE", moduleTrigger.GetProperty("name").GetString());
        Assert.Equal("BEGIN\n  GO_BLOCK('ORDER_ENTRY');\nEND;", moduleTrigger.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_module_carrying_features_this_worker_cannot_read_writes_no_artifact_and_verifies_nothing(
        bool programUnit,
        bool lov)
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        if (programUnit)
        {
            nint unit = graph.Append(module, FakeFormsObjectGraph.Codes.FirstProgramUnit);
            graph.At(unit).Texts[FakeFormsObjectGraph.Codes.Name] = "RECALCULATE_TOTAL";
        }

        if (lov)
        {
            nint values = graph.Append(module, FakeFormsObjectGraph.Codes.FirstLov);
            graph.At(values).Texts[FakeFormsObjectGraph.Codes.Name] = "CUSTOMER_LOV";
        }

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.IntermediateRepresentationPath);
        Assert.Empty(Directory.GetFiles(workspace.OutputRoot));
        Assert.DoesNotContain(result.Capabilities, capability => capability.State == CapabilityState.Verified);
        Assert.Contains(
            result.Findings,
            finding => finding.Contains(programUnit ? "RECALCULATE_TOTAL" : "CUSTOMER_LOV", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extraction_of_the_same_module_twice_produces_the_same_artifact_digest()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");

        (FakeFormsObjectGraph first, nint firstModule) = Estate();
        (FakeFormsObjectGraph second, nint secondModule) = Estate();

        WorkerExtractionResult one = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(first, firstModule), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);
        WorkerExtractionResult two = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(second, secondModule), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal(one.IntermediateRepresentationSha256, two.IntermediateRepresentationSha256);
        Assert.Equal(one.IntermediateRepresentationPath, two.IntermediateRepresentationPath);
    }

    [Fact]
    public async Task Extraction_reports_only_capabilities_it_exercised()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.All(result.Capabilities, capability => Assert.Equal(CapabilityState.Verified, capability.State));
        Assert.All(result.Capabilities, capability => Assert.False(string.IsNullOrWhiteSpace(capability.Observed)));
        Assert.Equal(FakeNativeFormsProvider.Evidence.FileVersion, result.Native?.FileVersion);
    }

    [Fact]
    public async Task Extraction_refuses_a_module_whose_content_does_not_match_the_pinned_hash()
    {
        using WorkerWorkspace workspace = new();
        workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", new string('d', 64)), CancellationToken.None);

        Assert.Equal(CapabilityState.Rejected, result.Status);
        Assert.Null(result.IntermediateRepresentationPath);
        Assert.Null(result.Native);
        Assert.Empty(Directory.GetFiles(workspace.OutputRoot));
    }

    [Theory]
    [InlineData("../escape.fmb")]
    [InlineData("..\\escape.fmb")]
    [InlineData("nested/MRD_ORDER_ENTRY.fmb")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts.fmb")]
    [InlineData("MRD_ORDER_ENTRY.fmb:stream")]
    [InlineData("MRD_ORDER_ENTRY.exe")]
    public async Task Extraction_refuses_an_alias_that_is_not_a_bare_supported_module_name(string alias)
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request(alias, hash), CancellationToken.None);

        Assert.Equal(CapabilityState.Rejected, result.Status);
        Assert.Null(result.IntermediateRepresentationPath);
        Assert.Empty(Directory.GetFiles(workspace.OutputRoot));
    }

    [Fact]
    public async Task Extraction_fails_closed_when_no_provider_is_configured()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerConfiguration unconfigured = workspace.Configuration() with { ApprovedLibrarySha256 = null };
        WorkerExtractionResult result = await new ExtractionService(unconfigured, new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal(CapabilityState.BlockedPrerequisite, result.Status);
        Assert.Contains(result.Capabilities, capability => capability.Id == "forms.module.extract");
        Assert.Null(result.IntermediateRepresentationPath);
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public async Task Extraction_refuses_a_host_that_is_not_x86(Architecture architecture)
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerExtractionResult result = await new ExtractionService(
                workspace.Configuration(),
                new FakeNativeFormsProvider(graph, module),
                new WorkerHost(true, architecture, "Windows (test)"))
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal(CapabilityState.BlockedPrerequisite, result.Status);
        Assert.Contains(result.Capabilities, capability => capability.Id == "forms.worker.architecture");
    }

    [Fact]
    public async Task Extraction_refuses_a_host_that_is_not_windows()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerExtractionResult result = await new ExtractionService(
                workspace.Configuration(),
                new FakeNativeFormsProvider(graph, module),
                new WorkerHost(false, Architecture.X86, "Linux (test)"))
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal(CapabilityState.BlockedPrerequisite, result.Status);
        Assert.Contains(result.Capabilities, capability => capability.Id == "forms.worker.os");
    }

    [Fact]
    public async Task Extraction_reports_a_blocked_provider_without_claiming_native_evidence()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");

        WorkerExtractionResult result = await new ExtractionService(
                workspace.Configuration(),
                new BlockedNativeFormsProvider("OracleFormsOpenApiLibraries", "operator.supply.authorized.forms.libraries"),
                s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal(CapabilityState.BlockedPrerequisite, result.Status);
        Assert.Null(result.Native);
        Assert.Null(result.IntermediateRepresentationPath);
        Assert.Equal(hash, result.ObservedContentSha256);
    }

    [Fact]
    public async Task A_module_the_api_returns_without_a_name_fails_the_read_rather_than_shrinking_the_estate()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();
        graph.At(module).Texts.Remove(FakeFormsObjectGraph.Codes.Name);

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.Null(result.IntermediateRepresentationPath);
        Assert.Empty(Directory.GetFiles(workspace.OutputRoot));
    }

    [Fact]
    public async Task A_block_the_api_returns_without_a_name_fails_the_read_rather_than_being_skipped()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();
        nint firstBlock = graph.Child(module, FakeFormsObjectGraph.Codes.FirstBlock);
        graph.At(firstBlock).Texts.Remove(FakeFormsObjectGraph.Codes.Name);

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal("ExtractionFailed", result.Status);
        Assert.NotEmpty(result.Findings);
        Assert.Empty(Directory.GetFiles(workspace.OutputRoot));
    }

    [Fact]
    public async Task An_item_type_the_header_cannot_name_is_reported_as_a_finding_and_keeps_its_raw_code()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();
        nint block = graph.Child(module, FakeFormsObjectGraph.Codes.FirstBlock);
        graph.At(graph.Child(block, FakeFormsObjectGraph.Codes.FirstItem)).Numbers[FakeFormsObjectGraph.Codes.ItemType] = 999;

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        Assert.Equal("Extracted", result.Status);
        Assert.Contains(result.Findings, finding => finding.Contains("does not name uniquely", StringComparison.Ordinal));

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(result.IntermediateRepresentationPath!, CancellationToken.None));
        Assert.Equal(
            "D2FC_ITTY_999",
            document.RootElement.GetProperty("modules")[0].GetProperty("blocks")[0].GetProperty("items")[0].GetProperty("itemType").GetString());
    }

    [Fact]
    public async Task Extraction_observes_cancellation_before_it_writes_anything()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        ExtractionService service = new(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), cancelled.Token));

        Assert.Empty(Directory.GetFiles(workspace.OutputRoot));
    }

    [Fact]
    public async Task The_representation_records_the_observed_library_version_as_its_authority()
    {
        using WorkerWorkspace workspace = new();
        string hash = workspace.WriteModule("MRD_ORDER_ENTRY.fmb", "native module bytes");
        (FakeFormsObjectGraph graph, nint module) = Estate();

        WorkerExtractionResult result = await new ExtractionService(workspace.Configuration(), new FakeNativeFormsProvider(graph, module), s_qualifiedHost)
            .ExtractAsync(Request("MRD_ORDER_ENTRY.fmb", hash), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(result.IntermediateRepresentationPath!, CancellationToken.None));
        JsonElement root = document.RootElement;

        Assert.Equal(WorkerProtocol.IrGenerator, root.GetProperty("generator").GetString());
        Assert.Equal("bin/ifd2f60.dll@6.0.8.7.3", root.GetProperty("versionAuthority").GetString());
        Assert.Equal(hash, root.GetProperty("contentSha256").GetString());

        // The worker never adjudicates normalization: that gate belongs to the host, against a session root
        // this process knows nothing about.
        Assert.False(root.TryGetProperty("normalized", out _));
        Assert.False(root.TryGetProperty("sourceRoot", out _));
    }
}
