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

    /// <summary>
    /// Writes a file of exactly <paramref name="totalBytes"/> whose first bytes are <paramref name="prefix"/>.
    ///
    /// The tail is produced with SetLength rather than written, so a file well past an intake limit costs
    /// no real I/O. That matters because the behaviour under test is that nothing reads the file at all.
    /// </summary>
    public void WriteFileOfLength(string relativePath, string prefix, long totalBytes)
    {
        string absolute = Absolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        using FileStream stream = File.Create(absolute);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(prefix));
        stream.SetLength(totalBytes);
    }

    /// <summary>Creates <paramref name="count"/> empty files under one directory, for file-count limits.</summary>
    public void WriteEmptyFiles(string relativeDirectory, string extension, int count)
    {
        string absolute = Absolute(relativeDirectory);
        Directory.CreateDirectory(absolute);

        for (int index = 0; index < count; index++)
        {
            File.Create(Path.Combine(absolute, $"f{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}{extension}")).Dispose();
        }
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
