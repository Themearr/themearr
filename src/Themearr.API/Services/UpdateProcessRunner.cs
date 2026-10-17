using System.Diagnostics;
using System.Text;

namespace Themearr.API.Services;

internal static class UpdateProcessRunner
{
    internal static async Task<int> RunAsync(ProcessStartInfo start, Action<string> log,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start updater.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // Either pipe can fill before the child writes to the other. Drain both
            // concurrently, including children that keep a pipe open after parent exit.
            await Task.WhenAll(
                DrainAsync(process.StandardOutput, log, deadline.Token),
                DrainAsync(process.StandardError, log, deadline.Token),
                process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Update command timed out after {timeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task DrainAsync(StreamReader reader, Action<string> log, CancellationToken token)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n' || line.Length == 4096)
                {
                    log(line.ToString());
                    line.Clear();
                }
                if (buffer[i] != '\n') line.Append(buffer[i]);
            }
        }
        if (line.Length != 0) log(line.ToString());
    }
}
