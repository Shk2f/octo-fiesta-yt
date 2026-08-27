using System.Diagnostics;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services.YouTube;

public interface IYtDlpProcessRunner
{
    /// <summary>
    /// Runs yt-dlp with the default (metadata/search) timeout.
    /// </summary>
    Task<YtDlpProcessRunner.ExecutionResult> ExecuteAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs yt-dlp with an explicit timeout (used by downloads, which need a larger budget).
    /// </summary>
    Task<YtDlpProcessRunner.ExecutionResult> ExecuteAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs yt-dlp subprocesses under two guard rails:
///
/// 1. A process-wide concurrency gate. yt-dlp shells out to Python and, for searches, spawns
///    its own HTTP workers. Without a ceiling, a burst of metadata-enrichment lookups (a client
///    browsing the library or re-syncing favourites fires getAlbum/getArtist for hundreds of
///    items at once) spawns hundreds of concurrent yt-dlp processes and exhausts host RAM +
///    swap, which the kernel resolves with an OOM kill that takes the app (and often the box)
///    down.
///
/// 2. A hard per-invocation timeout. A yt-dlp that hangs on YouTube throttling would otherwise
///    hold its concurrency slot forever; on timeout the whole process tree is killed.
///
/// Exit codes on the guard-rail paths: -1 = gave up waiting for a free slot, 124 = timed out
/// (matches coreutils `timeout`). Every caller already treats a non-zero exit code as "no
/// result", so these degrade gracefully.
/// </summary>
public sealed class YtDlpProcessRunner : IYtDlpProcessRunner, IDisposable
{
    public sealed record ExecutionResult(int ExitCode, string StandardOutput, string StandardError);

    private readonly SemaphoreSlim _gate;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _queueWaitTimeout;
    private readonly ILogger<YtDlpProcessRunner> _logger;

    public YtDlpProcessRunner(IOptions<YouTubeSettings> settings, ILogger<YtDlpProcessRunner> logger)
    {
        var value = settings.Value;
        _maxConcurrency = Math.Max(1, value.MaxConcurrentProcesses);
        _gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        _defaultTimeout = TimeSpan.FromSeconds(Math.Max(5, value.ProcessTimeoutSeconds));
        _queueWaitTimeout = TimeSpan.FromSeconds(Math.Max(1, value.ProcessQueueTimeoutSeconds));
        _logger = logger;
    }

    public Task<ExecutionResult> ExecuteAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => ExecuteAsync(ytDlpPath, arguments, _defaultTimeout, cancellationToken);

    public async Task<ExecutionResult> ExecuteAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var acquired = await _gate.WaitAsync(_queueWaitTimeout, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            _logger.LogWarning(
                "yt-dlp invocation dropped after waiting {Seconds:0}s for a free slot (max {Max} concurrent). Args: {Args}",
                _queueWaitTimeout.TotalSeconds, _maxConcurrency, string.Join(' ', arguments));
            return new ExecutionResult(-1, string.Empty,
                $"yt-dlp queue wait exceeded {_queueWaitTimeout.TotalSeconds:0}s (max {_maxConcurrency} concurrent)");
        }

        try
        {
            return await RunAsync(ytDlpPath, arguments, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ExecutionResult> RunAsync(
        string ytDlpPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read pipes without a cancellation token: after a kill the pipes close on their own,
        // and DrainAsync bounds the wait so a wedged grandchild can't hang us.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            // Distinguish a caller-requested cancellation from our own timeout.
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            _logger.LogWarning("yt-dlp killed after exceeding {Seconds:0}s timeout. Args: {Args}",
                timeout.TotalSeconds, string.Join(' ', arguments));
            return new ExecutionResult(124, await DrainAsync(stdoutTask).ConfigureAwait(false),
                $"yt-dlp timed out after {timeout.TotalSeconds:0}s");
        }

        var stdout = await DrainAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await DrainAsync(stderrTask).ConfigureAwait(false);
        return new ExecutionResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        try
        {
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            return completed == readTask ? await readTask.ConfigureAwait(false) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill timed-out yt-dlp process tree");
        }
    }

    public void Dispose() => _gate.Dispose();
}
