using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.YouTube;

namespace octo_fiesta.Tests;

/// <summary>
/// Guard-rail behaviour for the process runner: a global concurrency ceiling and a hard
/// per-invocation timeout. These exist because an unbounded burst of yt-dlp subprocesses
/// exhausted host RAM + swap in production and got the app OOM-killed.
/// </summary>
public class YtDlpProcessRunnerTests
{
    private static YtDlpProcessRunner CreateRunner(YouTubeSettings settings) =>
        new(Options.Create(settings), new Mock<ILogger<YtDlpProcessRunner>>().Object);

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string ShellExe => IsWindows ? "cmd.exe" : "/bin/sh";

    /// <summary>A command that exits immediately with code 0.</summary>
    private static string[] NoOp => IsWindows ? ["/c", "exit", "0"] : ["-c", "exit 0"];

    /// <summary>A command that blocks for roughly the given number of seconds.</summary>
    private static string[] Sleep(int seconds) => IsWindows
        ? ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"]
        : ["-c", $"sleep {seconds}"];

    [Fact]
    public async Task ExecuteAsync_RunsNormally_WhenUnderTimeoutAndConcurrencyLimit()
    {
        var runner = CreateRunner(new YouTubeSettings());

        var result = await runner.ExecuteAsync(ShellExe, NoOp, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_KillsProcessAndReturns124_WhenItExceedsTimeout()
    {
        var runner = CreateRunner(new YouTubeSettings());
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.ExecuteAsync(ShellExe, Sleep(30), TimeSpan.FromMilliseconds(700), CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("timed out", result.StandardError);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"runner did not return promptly after timeout (took {stopwatch.Elapsed})");
    }

    [Fact]
    public async Task ExecuteAsync_SerializesInvocations_WhenConcurrencyLimitIsOne()
    {
        var runner = CreateRunner(new YouTubeSettings { MaxConcurrentProcesses = 1 });
        var stopwatch = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => runner.ExecuteAsync(ShellExe, Sleep(1), TimeSpan.FromSeconds(30), CancellationToken.None)));
        stopwatch.Stop();

        // Three ~1s commands forced through a single slot cannot finish in under ~2s.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(2200), $"invocations were not serialized (took {stopwatch.Elapsed})");
    }

    [Fact]
    public async Task ExecuteAsync_RunsInParallel_WhenConcurrencyLimitAllows()
    {
        var runner = CreateRunner(new YouTubeSettings { MaxConcurrentProcesses = 3 });
        var stopwatch = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => runner.ExecuteAsync(ShellExe, Sleep(1), TimeSpan.FromSeconds(30), CancellationToken.None)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.5), $"invocations did not run in parallel (took {stopwatch.Elapsed})");
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Minus1_WhenItGivesUpWaitingForAFreeSlot()
    {
        var runner = CreateRunner(new YouTubeSettings { MaxConcurrentProcesses = 1, ProcessQueueTimeoutSeconds = 1 });

        using var blockerCts = new CancellationTokenSource();
        var blocker = runner.ExecuteAsync(ShellExe, Sleep(20), TimeSpan.FromSeconds(60), blockerCts.Token);
        try
        {
            await Task.Delay(300);
            var stopwatch = Stopwatch.StartNew();

            var result = await runner.ExecuteAsync(ShellExe, NoOp, TimeSpan.FromSeconds(30), CancellationToken.None);
            stopwatch.Stop();

            Assert.Equal(-1, result.ExitCode);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"queue wait did not time out promptly (took {stopwatch.Elapsed})");
        }
        finally
        {
            blockerCts.Cancel();
            try { await blocker; } catch { /* cancelled */ }
        }
    }
}
