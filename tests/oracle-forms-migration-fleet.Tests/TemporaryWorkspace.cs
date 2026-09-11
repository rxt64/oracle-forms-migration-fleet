// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Tests;

/// <summary>A disposable workspace root for execution tests. Nothing is written outside it.</summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ofmf-execution-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Absolute(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void WriteFile(string relativePath, string content)
    {
        string absolute = Absolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    public void WriteBytes(string relativePath, byte[] content)
    {
        string absolute = Absolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, content);
    }

    public bool Exists(string relativePath) => File.Exists(Absolute(relativePath));

    public string Read(string relativePath) => File.ReadAllText(Absolute(relativePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
