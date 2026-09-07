using System.Diagnostics;

namespace Saehakgi.Core.Util;

public readonly record struct ProcessResult(bool Started, int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => Started && !TimedOut && ExitCode == 0;
}

/// <summary>Runs a console tool (e.g. winget), capturing output, without a visible window.</summary>
public static class ProcessRunner
{
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch
        {
            // Tool not found on PATH (e.g. winget/App Installer absent).
            return new ProcessResult(Started: false, ExitCode: -1, StdOut: "", StdErr: "", TimedOut: false);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ProcessResult(Started: true, ExitCode: -1, StdOut: "", StdErr: "", TimedOut: true);
            }

            return new ProcessResult(
                Started: true,
                ExitCode: process.ExitCode,
                StdOut: stdout.GetAwaiter().GetResult(),
                StdErr: stderr.GetAwaiter().GetResult(),
                TimedOut: false);
        }
    }
}
