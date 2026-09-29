namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// The four reads the Forms object graph is walked with: a typed property off a node, and the node's first
/// child of a kind. Sibling traversal is the same child read with the "next object" property.
///
/// The traversal above this interface is the only implementation of module enumeration in the worker, so a
/// test that drives it through an in-memory graph exercises the same ordering, bounding, scoping and
/// serialization the native path uses. It does not exercise the native binding itself, and nothing here
/// should ever be read as evidence that it does.
/// </summary>
public interface IFormsObjectGraph
{
    string? Text(nint node, ushort property);

    uint? Number(nint node, ushort property);

    bool? Boolean(nint node, ushort property);

    nint Child(nint node, ushort property);
}

/// <summary>The property codes the traversal needs, every one of them resolved from the installed header.</summary>
public sealed record FormsPropertyCodes
{
    public required ushort Name { get; init; }
    public required ushort Title { get; init; }
    public required ushort NextObject { get; init; }
    public required ushort FirstBlock { get; init; }
    public required ushort FirstItem { get; init; }
    public required ushort FirstTrigger { get; init; }
    public required ushort FirstProgramUnit { get; init; }
    public required ushort FirstLov { get; init; }
    public required ushort TriggerText { get; init; }
    public required ushort ItemType { get; init; }
    public required ushort DataType { get; init; }
    public required ushort ColumnName { get; init; }
    public required ushort Prompt { get; init; }
    public required ushort Required { get; init; }
    public required ushort Visible { get; init; }
    public required ushort MaxLength { get; init; }
    public required ushort RecordsDisplayed { get; init; }
    public required ushort BaseTable { get; init; }
}

/// <summary>
/// Walks a loaded Forms module and produces the neutral shape the host consumes.
///
/// Every list is bounded, every traversal is cancellable, and an object the API returns without a name
/// fails the whole read rather than being skipped: a module that silently lost a block would be reported
/// as a smaller estate that still looked whole, which is the failure mode worth being strict about.
/// </summary>
/// <param name="nameOfCode">
/// Maps a numeric code back to the installed header's constant within one prefix family, so an item type
/// becomes a portable name without this file carrying a table of its own invention.
/// </param>
public sealed class FormsModuleTraversal(
    IFormsObjectGraph graph,
    FormsPropertyCodes codes,
    Func<string, uint, string?> nameOfCode)
{
    public const int MaxBlocks = 5_000;
    public const int MaxChildren = 20_000;

    private readonly IFormsObjectGraph _graph = graph;
    private readonly FormsPropertyCodes _codes = codes;
    private readonly Func<string, uint, string?> _nameOfCode = nameOfCode;

    public NativeModuleRead ReadModule(nint module, CancellationToken cancellationToken)
    {
        List<string> findings = [];

        if (_graph.Text(module, _codes.Name) is not { Length: > 0 } name)
        {
            return NativeModuleRead.Failed("The Forms API returned no module name, so the module could not be identified.");
        }

        (List<NeutralFormsBlock> blocks, string? blockError) = ReadBlocks(module, findings, cancellationToken);
        if (blockError is not null)
        {
            return NativeModuleRead.Failed(blockError);
        }

        (List<NeutralFormsTrigger> triggers, string? triggerError) =
            ReadTriggers(module, name, new HashSet<string>(StringComparer.Ordinal) { name }, cancellationToken);
        if (triggerError is not null)
        {
            return NativeModuleRead.Failed(triggerError);
        }

        (List<string> units, string? unitError) = ReadNames(module, _codes.FirstProgramUnit, "program unit", cancellationToken);
        if (unitError is not null)
        {
            return NativeModuleRead.Failed(unitError);
        }

        (List<string> lovs, string? lovError) = ReadNames(module, _codes.FirstLov, "list of values", cancellationToken);
        if (lovError is not null)
        {
            return NativeModuleRead.Failed(lovError);
        }

        if (units.Count > 0 || lovs.Count > 0)
        {
            return NativeModuleRead.Failed(Unread(units, lovs));
        }

        return new NativeModuleRead(
            new NeutralFormsModule(name, _graph.Text(module, _codes.Title), blocks, triggers, units, lovs),
            findings);
    }

    /// <summary>
    /// Why a module that declares program units or lists of values is refused rather than returned.
    ///
    /// This worker can name those objects and cannot yet read what they do: a program unit's PL/SQL body
    /// (D2FP_PGU_TXT) and unit type (D2FP_PGU_TYP), and a list of values' record group, column mapping and
    /// return items. A neutral module carrying only their names would reach the host with that behaviour
    /// missing and a shape that reads as whole, so the read fails and says exactly what went unread.
    /// </summary>
    private static string Unread(IReadOnlyList<string> units, IReadOnlyList<string> lovs)
    {
        List<string> unread = [];

        if (units.Count > 0)
        {
            unread.Add(
                $"{units.Count} program unit(s) whose PL/SQL bodies (D2FP_PGU_TXT) and unit types (D2FP_PGU_TYP) this worker " +
                $"does not read: {Listed(units)}");
        }

        if (lovs.Count > 0)
        {
            unread.Add(
                $"{lovs.Count} list(s) of values whose record groups, column mappings and return items this worker does not " +
                $"read: {Listed(lovs)}");
        }

        return $"The module declares {string.Join(" and ", unread)}. A name is not the behaviour the object carries, so " +
            "nothing was extracted rather than emitting a module that would read as complete.";
    }

    private static string Listed(IReadOnlyList<string> names) =>
        names.Count <= 10
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(10))} and {names.Count - 10} more";

    private (List<NeutralFormsBlock> Blocks, string? Error) ReadBlocks(
        nint module,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        List<NeutralFormsBlock> blocks = [];

        foreach (nint block in Children(module, _codes.FirstBlock, cancellationToken))
        {
            if (blocks.Count >= MaxBlocks)
            {
                return (blocks, $"The module declares more than {MaxBlocks} blocks, which exceeds this worker's bound.");
            }

            if (_graph.Text(block, _codes.Name) is not { Length: > 0 } name)
            {
                return (blocks, "The Forms API returned a block with no name, so the module was not read.");
            }

            (List<NeutralFormsItem> items, HashSet<string> scopes, string? itemError) =
                ReadItems(block, name, findings, cancellationToken);
            if (itemError is not null)
            {
                return (blocks, itemError);
            }

            scopes.Add(name);
            (List<NeutralFormsTrigger> triggers, string? triggerError) =
                ReadTriggers(block, name, scopes, cancellationToken);
            if (triggerError is not null)
            {
                return (blocks, triggerError);
            }

            foreach (nint item in Children(block, _codes.FirstItem, cancellationToken))
            {
                if (_graph.Text(item, _codes.Name) is not { Length: > 0 } itemName)
                {
                    continue;
                }

                (List<NeutralFormsTrigger> itemTriggers, string? itemTriggerError) =
                    ReadTriggers(item, $"{name}.{itemName}", scopes, cancellationToken);
                if (itemTriggerError is not null)
                {
                    return (blocks, itemTriggerError);
                }

                triggers.AddRange(itemTriggers);
            }

            blocks.Add(new NeutralFormsBlock(
                name,
                _graph.Text(block, _codes.BaseTable),
                (int)Math.Min(_graph.Number(block, _codes.RecordsDisplayed) ?? 1, int.MaxValue),
                items,
                triggers));
        }

        return (blocks, null);
    }

    private (List<NeutralFormsItem> Items, HashSet<string> Scopes, string? Error) ReadItems(
        nint block,
        string blockName,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        List<NeutralFormsItem> items = [];
        HashSet<string> scopes = new(StringComparer.Ordinal);

        foreach (nint item in Children(block, _codes.FirstItem, cancellationToken))
        {
            if (items.Count >= MaxChildren)
            {
                return (items, scopes, $"Block '{blockName}' declares more than {MaxChildren} items, which exceeds this worker's bound.");
            }

            if (_graph.Text(item, _codes.Name) is not { Length: > 0 } name)
            {
                return (items, scopes, $"The Forms API returned an item with no name in block '{blockName}', so the module was not read.");
            }

            uint? itemTypeCode = _graph.Number(item, _codes.ItemType);
            string? itemType = itemTypeCode is null ? null : _nameOfCode("D2FC_ITTY_", itemTypeCode.Value);
            if (itemType is null)
            {
                // The code is real and the installed header simply does not name it uniquely. Carrying the
                // raw code keeps the item readable without inventing a type name for it.
                itemType = itemTypeCode is null ? "UNREPORTED" : $"D2FC_ITTY_{itemTypeCode.Value}";
                findings.Add($"Item '{blockName}.{name}' reports an item type the installed header does not name uniquely; the raw code was retained.");
            }

            uint? dataTypeCode = _graph.Number(item, _codes.DataType);

            items.Add(new NeutralFormsItem(
                name,
                itemType,
                dataTypeCode is null ? null : _nameOfCode("D2FC_DATY_", dataTypeCode.Value),
                _graph.Text(item, _codes.ColumnName),
                _graph.Text(item, _codes.Prompt),
                _graph.Boolean(item, _codes.Required) ?? false,
                _graph.Boolean(item, _codes.Visible) ?? true,
                _graph.Number(item, _codes.MaxLength) is { } length and <= int.MaxValue ? (int)length : null));

            scopes.Add($"{blockName}.{name}");
        }

        return (items, scopes, null);
    }

    private (List<NeutralFormsTrigger> Triggers, string? Error) ReadTriggers(
        nint owner,
        string scope,
        HashSet<string> permittedScopes,
        CancellationToken cancellationToken)
    {
        List<NeutralFormsTrigger> triggers = [];

        foreach (nint trigger in Children(owner, _codes.FirstTrigger, cancellationToken))
        {
            if (triggers.Count >= MaxChildren)
            {
                return (triggers, $"'{scope}' declares more than {MaxChildren} triggers, which exceeds this worker's bound.");
            }

            if (_graph.Text(trigger, _codes.Name) is not { Length: > 0 } name)
            {
                return (triggers, $"The Forms API returned a trigger with no name on '{scope}', so the module was not read.");
            }

            if (!permittedScopes.Contains(scope))
            {
                return (triggers, $"Trigger '{name}' resolved to scope '{scope}', which is not an owner this module declares.");
            }

            // Trigger bodies are source text from a customer estate. They are retained verbatim with line
            // endings normalized, and are neither parsed nor executed here.
            triggers.Add(new NeutralFormsTrigger(
                name,
                scope,
                _graph.Text(trigger, _codes.TriggerText)?.Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        return (triggers, null);
    }

    private (List<string> Names, string? Error) ReadNames(
        nint owner,
        ushort firstChild,
        string kind,
        CancellationToken cancellationToken)
    {
        List<string> names = [];

        foreach (nint child in Children(owner, firstChild, cancellationToken))
        {
            if (names.Count >= MaxChildren)
            {
                return (names, $"The module declares more than {MaxChildren} {kind} entries, which exceeds this worker's bound.");
            }

            if (_graph.Text(child, _codes.Name) is { Length: > 0 } name)
            {
                names.Add(name);
            }
            else
            {
                return (names, $"The Forms API returned an unnamed {kind}; its definition could not be read.");
            }
        }

        return (names, null);
    }

    private IEnumerable<nint> Children(nint owner, ushort firstChild, CancellationToken cancellationToken)
    {
        nint child = _graph.Child(owner, firstChild);
        int visited = 0;

        while (child != nint.Zero && visited++ < MaxChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return child;
            child = _graph.Child(child, _codes.NextObject);
        }
    }
}
