using System.Diagnostics;
using System.Text;

namespace Themearr.API.Services;

internal sealed record AudioProcessOutput(int ExitCode, string Stdout, string Stderr);

internal static class AudioProcessRunner
{
    internal const int RetainedCharacters = 65536;
    internal static async Task<AudioProcessOutput> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start audio tool.");
        var stdout = DrainAsync(process.StandardOutput, deadline.Token); var stderr = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Audio processing exceeded its time budget.");
        }
    }
    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            if (result.Length < RetainedCharacters) result.Append(buffer, 0, Math.Min(count, RetainedCharacters - result.Length));
        return result.ToString();
    }
}
