using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// Binds to the installed Oracle Forms API in <c>ifd2f60.dll</c>.
///
/// Nothing here is assumed to exist. The library is hash-checked against the operator's approved digest
/// before it is loaded, every entry point is resolved by its exact exported name from the loaded image, and
/// every object and property code is resolved from the installed header. A missing export or a missing code
/// is a reported prerequisite, never a fallback path, so this worker cannot report a module it did not
/// actually read through the API.
///
/// The calling convention, the library name and the context lifecycle match what
/// <c>infra/source-lab/forms6i/Build-MeridianForms6i.ps1</c> already exercises successfully against this
/// estate's install. The read-side signatures and export spellings are those of the installed
/// <c>FORMS60/API</c> headers on the source VM; nothing in this repository has yet executed them.
/// </summary>
public sealed class D2FNativeFormsProvider : INativeFormsProvider
{
    public async Task<NativeProviderOpen> OpenAsync(WorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.NativeProviderConfigured)
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsOpenApiLibraries",
                "operator.configure.forms.home.and.approved.binary",
                $"The worker has no approved native provider configured ({configuration.FirstMissingSetting} is unset), so no library was loaded.");
        }

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X86)
        {
            return NativeProviderOpen.Blocked(
                "WorkerHostArchitecture",
                "operator.provision.windows.x86.worker",
                $"The Forms API is a 32-bit Windows library and this worker is running as {RuntimeInformation.ProcessArchitecture} on " +
                $"{(OperatingSystem.IsWindows() ? "Windows" : "a non-Windows host")}, so it was not loaded.");
        }

        string libraryPath = configuration.LibraryPath!;
        if (!File.Exists(libraryPath))
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsInstallation",
                "operator.install.forms.6i.worker",
                $"The configured Forms home does not contain {WorkerConfiguration.LibraryAlias}.");
        }

        string observedLibraryHash = await ContentHash.OfFileAsync(libraryPath, cancellationToken);
        if (!ContentHash.Matches(configuration.ApprovedLibrarySha256!, observedLibraryHash))
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsOpenApiLibraries",
                "operator.supply.authorized.forms.libraries",
                $"The installed {WorkerConfiguration.LibraryAlias} does not match the approved SHA-256, so it was not loaded.");
        }

        FileVersionInfo version = FileVersionInfo.GetVersionInfo(libraryPath);
        string? observedFileVersion = ApprovedLibraryVersion.Observed(version.FileVersion);
        if (ApprovedLibraryVersion.Mismatch(observedFileVersion, configuration.ApprovedLibraryFileVersion!) is { } mismatch)
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsInstallation",
                "operator.install.forms.6i.worker",
                $"The installed {WorkerConfiguration.LibraryAlias} {mismatch}");
        }

        string headerPath = configuration.DefinitionHeaderPath!;
        if (!File.Exists(headerPath))
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsInstallation",
                "operator.install.forms.6i.worker",
                $"The configured Forms home does not contain {WorkerConfiguration.DefinitionHeaderAlias}, so no object or property codes could be read.");
        }

        string headerHash = await ContentHash.OfFileAsync(headerPath, cancellationToken);
        FormsApiDefinitions? definitions = FormsApiDefinitions.Load(headerPath, headerHash);
        if (definitions is null)
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsOpenApiLibraries",
                "operator.supply.authorized.forms.libraries",
                $"{WorkerConfiguration.DefinitionHeaderAlias} defined no usable D2F constants, so nothing could be read through the API.");
        }

        (nint handle, int loadError) = NativeFormsLibrary.Load(libraryPath);
        if (handle == nint.Zero)
        {
            return NativeProviderOpen.Blocked(
                "OracleFormsOpenApiLibraries",
                "operator.supply.authorized.forms.libraries",
                $"The approved {WorkerConfiguration.LibraryAlias} could not be loaded into this process (Win32 error {loadError}). " +
                $"Its dependencies are searched only in its own directory and the system directory.");
        }

        (D2FEntryPoints? entries, string? missingExport) = D2FEntryPoints.Resolve(handle);
        if (entries is null)
        {
            NativeFormsLibrary.Free(handle);
            return NativeProviderOpen.Blocked(
                "OracleFormsOpenApiLibraries",
                "operator.supply.authorized.forms.libraries",
                $"The approved {WorkerConfiguration.LibraryAlias} exports no entry point for '{missingExport}', so this release cannot be read through this binding.");
        }

        (FormsPropertyCodes? codes, string? missingCode) = D2FPropertyCodes.Resolve(definitions);
        if (codes is null)
        {
            NativeFormsLibrary.Free(handle);
            return NativeProviderOpen.Blocked(
                "OracleFormsOpenApiLibraries",
                "operator.supply.authorized.forms.libraries",
                $"{WorkerConfiguration.DefinitionHeaderAlias} defines no constant for '{missingCode}', so this release cannot be read through this binding.");
        }

        // The observed version is recorded, never the approved value: an operator who declared the binary
        // unversioned must not end up with a run record that claims a version was read off it.
        WorkerNativeEvidence evidence = new(
            WorkerConfiguration.LibraryAlias,
            observedFileVersion,
            ApprovedLibraryVersion.Observed(version.ProductVersion),
            observedLibraryHash,
            headerHash,
            entries.ResolvedExports,
            definitions.ResolvedNames);

        return new NativeProviderOpen(
            new D2FNativeFormsModuleReader(handle, entries, codes, definitions, evidence),
            evidence,
            null,
            null,
            null);
    }
}

internal sealed class D2FNativeFormsModuleReader : INativeFormsModuleReader, IFormsObjectGraph
{
    private const int MaxTextLength = 1024 * 1024;

    private readonly nint _library;
    private readonly D2FEntryPoints _api;
    private readonly FormsPropertyCodes _codes;
    private readonly FormsModuleTraversal _traversal;
    private nint _context;
    private bool _disposed;

    internal D2FNativeFormsModuleReader(
        nint library,
        D2FEntryPoints api,
        FormsPropertyCodes codes,
        FormsApiDefinitions definitions,
        WorkerNativeEvidence evidence)
    {
        _library = library;
        _api = api;
        _codes = codes;
        _traversal = new FormsModuleTraversal(this, codes, definitions.NameOfCode);
        Evidence = evidence;
    }

    public WorkerNativeEvidence Evidence { get; }

    public NativeModuleRead Read(string modulePath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        D2FContextAttributes attributes = default;
        int status = _api.CreateContext(out _context, ref attributes);
        if (status != 0 || _context == nint.Zero)
        {
            return NativeModuleRead.Failed($"The Forms API refused to create a context (status {status}).");
        }

        status = _api.LoadModule(_context, out nint module, modulePath, 0);
        if (status != 0 || module == nint.Zero)
        {
            return NativeModuleRead.Failed(
                $"The Forms API refused to open the module (status {status}). The file was hash-matched but not readable by this release's API.");
        }

        try
        {
            return _traversal.ReadModule(module, cancellationToken);
        }
        finally
        {
            _api.DestroyModule(_context, module);
        }
    }

    public string? Text(nint node, ushort property)
    {
        if (_api.GetTextProperty(_context, node, property, out nint text) != 0 || text == nint.Zero)
        {
            return null;
        }

        string? value = Marshal.PtrToStringAnsi(text);
        return value is null || value.Length > MaxTextLength ? null : value;
    }

    public uint? Number(nint node, ushort property) =>
        _api.GetNumberProperty(_context, node, property, out uint value) == 0 ? value : null;

    public bool? Boolean(nint node, ushort property) =>
        _api.GetBooleanProperty(_context, node, property, out int value) == 0 ? value != 0 : null;

    public nint Child(nint node, ushort property) =>
        _api.GetObjectProperty(_context, node, property, out nint child) == 0 ? child : nint.Zero;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context != nint.Zero)
        {
            _api.DestroyContext(_context);
            _context = nint.Zero;
        }

        NativeFormsLibrary.Free(_library);
    }
}

/// <summary>
/// Loads the approved Forms library with a dependency search path this worker controls.
///
/// <c>ifd2f60.dll</c> pulls in the rest of the Forms runtime from its own directory, so the load has to
/// reach those siblings — but only those. <c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR</c> with
/// <c>LOAD_LIBRARY_SEARCH_SYSTEM32</c> resolves dependencies from the approved library's directory and the
/// system directory and nowhere else, so the machine <c>PATH</c>, the current directory and the application
/// directory cannot supply a substitute binary that the approved digest never covered.
/// </summary>
internal static class NativeFormsLibrary
{
    private const uint SearchDllLoadDirectory = 0x00000100;
    private const uint SearchSystem32 = 0x00000800;

    /// <summary>The loaded module, or zero and the Win32 error that explains why it is zero.</summary>
    public static (nint Handle, int Error) Load(string libraryPath)
    {
        // The flags are only honoured for a fully qualified path, which the configured home always produces.
        nint handle = LoadLibraryEx(libraryPath, nint.Zero, SearchDllLoadDirectory | SearchSystem32);
        return (handle, handle == nint.Zero ? Marshal.GetLastWin32Error() : 0);
    }

    public static void Free(nint handle)
    {
        if (handle != nint.Zero)
        {
            FreeLibrary(handle);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadLibraryEx(string fileName, nint reservedFile, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}

/// <summary>
/// Decides whether the installed binary is the one the operator approved by version.
///
/// The <c>ifd2f60.dll</c> verified on this estate carries no file version resource at all, so the approved
/// value may be the literal <see cref="WorkerConfiguration.UnversionedSentinel"/>. That sentinel is an
/// explicit operator opt-in: it matches only a version that is genuinely absent, it is never recorded as a
/// version that was observed, and it is never what an unset configuration falls back to.
/// </summary>
internal static class ApprovedLibraryVersion
{
    /// <summary>The version string the binary actually carries, or null when it carries none.</summary>
    public static string? Observed(string? resourceValue) =>
        string.IsNullOrWhiteSpace(resourceValue) ? null : resourceValue.Trim();

    /// <summary>Null when the observed version is the approved one, otherwise the detail of the mismatch.</summary>
    public static string? Mismatch(string? observedFileVersion, string approvedFileVersion)
    {
        if (string.Equals(approvedFileVersion, WorkerConfiguration.UnversionedSentinel, StringComparison.Ordinal))
        {
            return observedFileVersion is null
                ? null
                : $"reports file version '{observedFileVersion}' and the approved configuration declares it carries none.";
        }

        return string.Equals(observedFileVersion, approvedFileVersion, StringComparison.Ordinal)
            ? null
            : $"reports file version '{observedFileVersion ?? "(none)"}' and the approved version is '{approvedFileVersion}'.";
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2FContextAttributes
{
    public uint Mask;
    public nint Data;
    public nint Allocate;
    public nint Free;
    public nint Reallocate;
}

/// <summary>
/// The exact names the installed <c>FORMS60/API</c> library exports for the entry points this binding
/// needs, read off the source VM's headers. They are single names on purpose: an alternative spelling would
/// be a guess about a release this repository has not read, and binding to one would let the worker report a
/// module it read through an entry point nobody approved.
/// </summary>
internal static class D2FExports
{
    public const string CreateContext = "d2fctxcr_Create";
    public const string DestroyContext = "d2fctxde_Destroy";
    public const string LoadModule = "d2ffmdld_Load";
    public const string DestroyModule = "d2ffmdde_Destroy";
    public const string GetTextProperty = "d2fobgt_GetTextProp";
    public const string GetNumberProperty = "d2fobgn_GetNumProp";
    public const string GetBooleanProperty = "d2fobgb_GetBoolProp";
    public const string GetObjectProperty = "d2fobgo_GetObjProp";

    /// <summary>Every export this binding requires, in the order it resolves them.</summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        CreateContext,
        DestroyContext,
        LoadModule,
        DestroyModule,
        GetTextProperty,
        GetNumberProperty,
        GetBooleanProperty,
        GetObjectProperty,
    ];
}

/// <summary>
/// The entry points this binding needs, each resolved by its exact exported name from the loaded image.
/// A name the installed library does not export is reported as a missing prerequisite; there is no second
/// candidate to fall back to.
/// </summary>
internal sealed class D2FEntryPoints
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreateContextDelegate(out nint context, ref D2FContextAttributes attributes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DestroyContextDelegate(nint context);

    // d2ffmdld_Load(d2fctx*, d2ffmd**, text *formname, boolean db) — four arguments, and the last is the
    // load-from-database flag. It is not a connect string, and this binding never passes one.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal delegate int LoadModuleDelegate(nint context, out nint module, string fileName, int fromDatabase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DestroyModuleDelegate(nint context, nint module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetTextPropertyDelegate(nint context, nint node, ushort property, out nint text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetNumberPropertyDelegate(nint context, nint node, ushort property, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetBooleanPropertyDelegate(nint context, nint node, ushort property, out int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetObjectPropertyDelegate(nint context, nint node, ushort property, out nint value);

    private D2FEntryPoints(IReadOnlyList<string> resolvedExports) => ResolvedExports = resolvedExports;

    public IReadOnlyList<string> ResolvedExports { get; }

    public required CreateContextDelegate CreateContext { get; init; }
    public required DestroyContextDelegate DestroyContext { get; init; }
    public required LoadModuleDelegate LoadModule { get; init; }
    public required DestroyModuleDelegate DestroyModule { get; init; }
    public required GetTextPropertyDelegate GetTextProperty { get; init; }
    public required GetNumberPropertyDelegate GetNumberProperty { get; init; }
    public required GetBooleanPropertyDelegate GetBooleanProperty { get; init; }
    public required GetObjectPropertyDelegate GetObjectProperty { get; init; }

    public static (D2FEntryPoints? Entries, string? MissingExport) Resolve(nint library)
    {
        List<string> resolved = [];

        if (Bind<CreateContextDelegate>(library, resolved, D2FExports.CreateContext) is not { } createContext)
        {
            return (null, D2FExports.CreateContext);
        }

        if (Bind<DestroyContextDelegate>(library, resolved, D2FExports.DestroyContext) is not { } destroyContext)
        {
            return (null, D2FExports.DestroyContext);
        }

        if (Bind<LoadModuleDelegate>(library, resolved, D2FExports.LoadModule) is not { } load)
        {
            return (null, D2FExports.LoadModule);
        }

        if (Bind<DestroyModuleDelegate>(library, resolved, D2FExports.DestroyModule) is not { } destroyModule)
        {
            return (null, D2FExports.DestroyModule);
        }

        if (Bind<GetTextPropertyDelegate>(library, resolved, D2FExports.GetTextProperty) is not { } text)
        {
            return (null, D2FExports.GetTextProperty);
        }

        if (Bind<GetNumberPropertyDelegate>(library, resolved, D2FExports.GetNumberProperty) is not { } number)
        {
            return (null, D2FExports.GetNumberProperty);
        }

        if (Bind<GetBooleanPropertyDelegate>(library, resolved, D2FExports.GetBooleanProperty) is not { } boolean)
        {
            return (null, D2FExports.GetBooleanProperty);
        }

        if (Bind<GetObjectPropertyDelegate>(library, resolved, D2FExports.GetObjectProperty) is not { } obj)
        {
            return (null, D2FExports.GetObjectProperty);
        }

        return (new D2FEntryPoints([.. resolved])
        {
            CreateContext = createContext,
            DestroyContext = destroyContext,
            LoadModule = load,
            DestroyModule = destroyModule,
            GetTextProperty = text,
            GetNumberProperty = number,
            GetBooleanProperty = boolean,
            GetObjectProperty = obj,
        }, null);
    }

    private static TDelegate? Bind<TDelegate>(nint library, List<string> resolved, string export)
        where TDelegate : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, export, out nint address) || address == nint.Zero)
        {
            return null;
        }

        resolved.Add(export);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }
}

/// <summary>
/// Resolves every property code the traversal needs from the installed header, or names the first one
/// missing.
///
/// Each logical field maps to exactly one header constant, the spelling verified on the source VM's
/// <c>D2FDEF.H</c>. The numeric values are deliberately not compiled in \u2014 they come from whichever header
/// is installed \u2014 but the names are fixed, so a header that spells a property differently is reported as an
/// unreadable release rather than silently half-read through an older constant.
/// </summary>
internal static class D2FPropertyCodes
{
    public const string Name = "D2FP_NAME";
    public const string Title = "D2FP_TITLE";
    public const string NextObject = "D2FP_NEXT";
    public const string FirstBlock = "D2FP_BLOCK";
    public const string FirstItem = "D2FP_ITEM";
    public const string FirstTrigger = "D2FP_TRIGGER";
    public const string FirstProgramUnit = "D2FP_PROG_UNIT";
    public const string FirstLov = "D2FP_LOV";
    public const string TriggerText = "D2FP_TRG_TXT";
    public const string ItemType = "D2FP_ITM_TYP";
    public const string DataType = "D2FP_DAT_TYP";
    public const string ColumnName = "D2FP_COL_NAM";
    public const string Prompt = "D2FP_PRMPT";
    public const string Required = "D2FP_REQUIRED";
    public const string Visible = "D2FP_VISIBLE";
    public const string MaxLength = "D2FP_MAX_LEN";
    public const string RecordsDisplayed = "D2FP_RECS_DISP_COUNT";
    public const string BaseTable = "D2FP_QRY_DAT_SRC_NAM";

    /// <summary>Every header constant this binding requires, in the order it resolves them.</summary>
    public static IReadOnlyList<string> RequiredConstants { get; } =
    [
        Name,
        Title,
        NextObject,
        FirstBlock,
        FirstItem,
        FirstTrigger,
        FirstProgramUnit,
        FirstLov,
        TriggerText,
        ItemType,
        DataType,
        ColumnName,
        Prompt,
        Required,
        Visible,
        MaxLength,
        RecordsDisplayed,
        BaseTable,
    ];

    public static (FormsPropertyCodes? Codes, string? MissingCode) Resolve(FormsApiDefinitions definitions)
    {
        ushort? name = Code(definitions, "name", Name);
        ushort? title = Code(definitions, "title", Title);
        ushort? next = Code(definitions, "nextObject", NextObject);
        ushort? firstBlock = Code(definitions, "firstBlock", FirstBlock);
        ushort? firstItem = Code(definitions, "firstItem", FirstItem);
        ushort? firstTrigger = Code(definitions, "firstTrigger", FirstTrigger);
        ushort? firstUnit = Code(definitions, "firstProgramUnit", FirstProgramUnit);
        ushort? firstLov = Code(definitions, "firstLov", FirstLov);
        ushort? triggerText = Code(definitions, "triggerText", TriggerText);
        ushort? itemType = Code(definitions, "itemType", ItemType);
        ushort? dataType = Code(definitions, "dataType", DataType);
        ushort? columnName = Code(definitions, "columnName", ColumnName);
        ushort? prompt = Code(definitions, "prompt", Prompt);
        ushort? required = Code(definitions, "required", Required);
        ushort? visible = Code(definitions, "visible", Visible);
        ushort? maxLength = Code(definitions, "maxLength", MaxLength);
        ushort? records = Code(definitions, "recordsDisplayed", RecordsDisplayed);
        ushort? baseTable = Code(definitions, "baseTable", BaseTable);

        string? missing =
            name is null ? Name
            : title is null ? Title
            : next is null ? NextObject
            : firstBlock is null ? FirstBlock
            : firstItem is null ? FirstItem
            : firstTrigger is null ? FirstTrigger
            : firstUnit is null ? FirstProgramUnit
            : firstLov is null ? FirstLov
            : triggerText is null ? TriggerText
            : itemType is null ? ItemType
            : dataType is null ? DataType
            : columnName is null ? ColumnName
            : prompt is null ? Prompt
            : required is null ? Required
            : visible is null ? Visible
            : maxLength is null ? MaxLength
            : records is null ? RecordsDisplayed
            : baseTable is null ? BaseTable
            : null;

        return missing is not null
            ? (null, missing)
            : (new FormsPropertyCodes
            {
                Name = name!.Value,
                Title = title!.Value,
                NextObject = next!.Value,
                FirstBlock = firstBlock!.Value,
                FirstItem = firstItem!.Value,
                FirstTrigger = firstTrigger!.Value,
                FirstProgramUnit = firstUnit!.Value,
                FirstLov = firstLov!.Value,
                TriggerText = triggerText!.Value,
                ItemType = itemType!.Value,
                DataType = dataType!.Value,
                ColumnName = columnName!.Value,
                Prompt = prompt!.Value,
                Required = required!.Value,
                Visible = visible!.Value,
                MaxLength = maxLength!.Value,
                RecordsDisplayed = records!.Value,
                BaseTable = baseTable!.Value,
            }, null);
    }

    private static ushort? Code(FormsApiDefinitions definitions, string logicalName, string constantName) =>
        definitions.Resolve(logicalName, constantName) is { } value && value <= ushort.MaxValue ? (ushort)value : null;
}
