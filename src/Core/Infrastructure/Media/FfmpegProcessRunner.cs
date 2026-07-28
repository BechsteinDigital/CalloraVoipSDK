using System.Diagnostics;
using System.Text;
using System.ComponentModel;

namespace CalloraVoipSdk.Core.Infrastructure.Media;

/// <summary>
/// Runs ffmpeg processes for media transcode operations with structured error handling.
/// </summary>
internal static class FfmpegProcessRunner
{
    /// <summary>
    /// Executes one ffmpeg command and throws on non-zero exit status.
    /// </summary>
    public static async Task RunAsync(
        Action<ProcessStartInfo> configure,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        configure(psi);

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start ffmpeg process.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Unable to start ffmpeg. Ensure ffmpeg is installed and available in PATH.",
                ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        string stdout;
        string stderr;
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Media #16: on cancellation the ffmpeg process would otherwise leak. Kill the whole
            // process tree best-effort before propagating the cancellation.
            KillProcessTree(process);
            throw;
        }

        if (process.ExitCode == 0)
            return;

        var sb = new StringBuilder();
        sb.Append("ffmpeg failed with exit code ").Append(process.ExitCode).Append('.');

        if (!string.IsNullOrWhiteSpace(stderr))
            sb.Append(" stderr: ").Append(stderr.Trim());
        else if (!string.IsNullOrWhiteSpace(stdout))
            sb.Append(" output: ").Append(stdout.Trim());

        throw new InvalidOperationException(sb.ToString());
    }

    /// <summary>
    /// Checks if ffmpeg is invokable in the current runtime environment.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            if (!process.WaitForExit(1000))
            {
                // Media #16: the probe timed out — kill the process tree before returning so no
                // ffmpeg -version process is left running.
                KillProcessTree(process);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // ffmpeg not on PATH, or the process could not be inspected — treat as unavailable.
            return false;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Process already exited or the OS refused the kill; nothing more we can do here.
            _ = ex;
        }
    }
}
