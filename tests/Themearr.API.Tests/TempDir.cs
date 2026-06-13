namespace Themearr.API.Tests;

/// <summary>Throwaway directory under the OS temp path, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "themearr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Write(string name, byte[] bytes) => System.IO.File.WriteAllBytes(File(name), bytes);
    public void Write(string name, string text) => System.IO.File.WriteAllText(File(name), text);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
