// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using OracleFormsMigrationFleet.SourceWorker;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// An in-memory Forms object graph. It is wired to the worker's real traversal, so a test using it
/// exercises the actual enumeration, scoping, bounding and serialization code. It is not, and must never
/// be reported as, evidence that the native binding works.
/// </summary>
public sealed class FakeFormsObjectGraph : IFormsObjectGraph
{
    private readonly Dictionary<nint, Node> _nodes = [];
    private nint _next = 1;

    public sealed class Node
    {
        public Dictionary<ushort, string> Texts { get; } = [];
        public Dictionary<ushort, uint> Numbers { get; } = [];
        public Dictionary<ushort, bool> Booleans { get; } = [];
        public Dictionary<ushort, nint> Children { get; } = [];
    }

    public static FormsPropertyCodes Codes { get; } = new()
    {
        Name = 1,
        Title = 2,
        NextObject = 3,
        FirstBlock = 4,
        FirstItem = 5,
        FirstTrigger = 6,
        FirstProgramUnit = 7,
        FirstLov = 8,
        TriggerText = 9,
        ItemType = 10,
        DataType = 11,
        ColumnName = 12,
        Prompt = 13,
        Required = 14,
        Visible = 15,
        MaxLength = 16,
        RecordsDisplayed = 17,
        BaseTable = 18,
    };

    public nint Create()
    {
        nint handle = _next++;
        _nodes[handle] = new Node();
        return handle;
    }

    public Node At(nint handle) => _nodes[handle];

    /// <summary>Appends a child under <paramref name="firstChildProperty"/>, walking the sibling chain as the API does.</summary>
    public nint Append(nint owner, ushort firstChildProperty)
    {
        nint child = Create();
        if (!_nodes[owner].Children.TryGetValue(firstChildProperty, out nint head))
        {
            _nodes[owner].Children[firstChildProperty] = child;
            return child;
        }

        while (_nodes[head].Children.TryGetValue(Codes.NextObject, out nint sibling))
        {
            head = sibling;
        }

        _nodes[head].Children[Codes.NextObject] = child;
        return child;
    }

    public string? Text(nint node, ushort property) =>
        _nodes[node].Texts.TryGetValue(property, out string? value) ? value : null;

    public uint? Number(nint node, ushort property) =>
        _nodes[node].Numbers.TryGetValue(property, out uint value) ? value : null;

    public bool? Boolean(nint node, ushort property) =>
        _nodes[node].Booleans.TryGetValue(property, out bool value) ? value : null;

    public nint Child(nint node, ushort property) =>
        _nodes[node].Children.TryGetValue(property, out nint child) ? child : nint.Zero;

    /// <summary>Names item and data type codes the way a header family lookup would.</summary>
    public static string? NameOfCode(string prefix, uint code) => (prefix, code) switch
    {
        ("D2FC_ITTY_", 1) => "TI",
        ("D2FC_ITTY_", 2) => "DI",
        ("D2FC_ITTY_", 3) => "LS",
        ("D2FC_ITTY_", 4) => "PB",
        ("D2FC_DATY_", 1) => "CHAR",
        ("D2FC_DATY_", 2) => "NUMBER",
        _ => null,
    };
}

public sealed class FakeNativeFormsProvider(FakeFormsObjectGraph graph, nint module) : INativeFormsProvider
{
    public static WorkerNativeEvidence Evidence { get; } = new(
        WorkerConfiguration.LibraryAlias,
        "6.0.8.7.3",
        "6.0.8.11.3",
        new string('a', 64),
        new string('b', 64),
        ["d2fctxcr_Create", "d2ffmdld_Load", "d2fobgt_GetTextProp"],
        ["name", "firstBlock"]);

    public Task<NativeProviderOpen> OpenAsync(WorkerConfiguration configuration, CancellationToken cancellationToken) =>
        Task.FromResult(new NativeProviderOpen(new Reader(graph, module), Evidence, null, null, null));

    private sealed class Reader(FakeFormsObjectGraph graph, nint module) : INativeFormsModuleReader
    {
        private readonly FormsModuleTraversal _traversal =
            new(graph, FakeFormsObjectGraph.Codes, FakeFormsObjectGraph.NameOfCode);

        public WorkerNativeEvidence Evidence => FakeNativeFormsProvider.Evidence;

        public NativeModuleRead Read(string modulePath, CancellationToken cancellationToken) =>
            _traversal.ReadModule(module, cancellationToken);

        public void Dispose()
        {
        }
    }
}

public sealed class BlockedNativeFormsProvider(string prerequisite, string remediation) : INativeFormsProvider
{
    public Task<NativeProviderOpen> OpenAsync(WorkerConfiguration configuration, CancellationToken cancellationToken) =>
        Task.FromResult(NativeProviderOpen.Blocked(prerequisite, remediation, "The provider refused to open."));
}

/// <summary>A workspace of temporary trusted-input and artifact roots that cleans itself up.</summary>
public sealed class WorkerWorkspace : IDisposable
{
    public WorkerWorkspace()
    {
        Root = Directory.CreateTempSubdirectory("ofm-source-worker-").FullName;
        InputRoot = Directory.CreateDirectory(Path.Combine(Root, "input")).FullName;
        OutputRoot = Directory.CreateDirectory(Path.Combine(Root, "artifacts")).FullName;
    }

    public string Root { get; }

    public string InputRoot { get; }

    public string OutputRoot { get; }

    public string WriteModule(string alias, string content)
    {
        string path = Path.Combine(InputRoot, alias);
        File.WriteAllText(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    public WorkerConfiguration Configuration() => new(
        InputRoot,
        OutputRoot,
        Path.Combine(Root, "orant"),
        new string('a', 64),
        "6.0.8.7.3",
        TimeSpan.FromSeconds(30));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
