using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Platform.Rtss;

/// <summary>How an automatic RTSS install attempt ended.</summary>
public enum RtssInstallOutcome
{
    /// <summary>winget ran to completion; <see cref="RtssInstallResult.ExitCode"/> says how it went.</summary>
    Finished,

    /// <summary>Windows Package Manager is not available on this PC.</summary>
    WingetMissing,

    /// <summary>No network connection, so nothing was attempted.</summary>
    Offline,

    /// <summary>Another install is already in flight (ours, or an RTSS setup the user started).</summary>
    AlreadyInstalling,

    /// <summary>winget did not finish in time and was stopped.</summary>
    TimedOut,

    /// <summary>winget could not be started at all.</summary>
    CouldNotStart,
}

/// <summary>Where an automatic RTSS install has got to, in order.</summary>
public enum RtssInstallStage
{
    Preparing,
    Downloading,
    Installing,
    Finishing,
}

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="ExitCode">winget's exit code when <paramref name="Outcome"/> is <see cref="RtssInstallOutcome.Finished"/>.</param>
public readonly record struct RtssInstallResult(RtssInstallOutcome Outcome, int ExitCode = 0);

/// <summary>
/// Installs RivaTuner Statistics Server through Windows Package Manager. winget fetches the official
/// installer from Guru3D's mirror and verifies its hash, so Tsume never hosts or bundles RTSS itself.
/// </summary>
public static class RtssWingetInstaller
{
    public const string PackageId = "Guru3D.RTSS";

    // Generous: the download is small, but a Windows permission prompt waits on the user.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private static readonly Journal Log = new("rtss");

    private static int _busy;

    /// <summary>True while an install started by Tsume is still running.</summary>
    public static bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Full path to <c>winget.exe</c>, or null when Windows Package Manager is not installed.</summary>
    public static string? FindWinget()
    {
        try
        {
            // The per-user app execution alias. Resolved explicitly rather than through PATH, which a
            // service-started process does not always inherit from the signed-in user.
            var alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");

            // Not File.Exists: the alias is a reparse point, which some .NET versions report as missing.
            return File.GetAttributes(alias).HasFlag(FileAttributes.Directory) ? null : alias;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when an RTSS setup program is already open (for example one the user ran by hand).</summary>
    public static bool IsSetupRunning()
    {
        var found = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.Contains("RTSSSetup", StringComparison.OrdinalIgnoreCase)) found = true;
                }
                catch (InvalidOperationException) { }   // exited while we were looking
            }
        }
        return found;
    }

    /// <summary>
    /// Runs <c>winget install Guru3D.RTSS</c> silently and waits for it. Only one install runs at a
    /// time; a second call while one is in flight returns <see cref="RtssInstallOutcome.AlreadyInstalling"/>.
    /// The caller decides success by looking for RTSS afterwards, since winget's exit code alone cannot
    /// tell "already installed" from a real failure reliably across winget versions.
    /// </summary>
    /// <param name="progress">Told each time the install moves on to a later stage.</param>
    public static Task<RtssInstallResult> InstallAsync(IProgress<RtssInstallStage>? progress = null) =>
        RunAsync("install", progress);

    /// <summary>
    /// Runs <c>winget upgrade Guru3D.RTSS</c> silently and waits for it, exactly like <see cref="InstallAsync"/>.
    /// RTSS must already be closed: its installer would otherwise restart it under a running game.
    /// </summary>
    public static Task<RtssInstallResult> UpgradeAsync(IProgress<RtssInstallStage>? progress = null) =>
        RunAsync("upgrade", progress);

    /// <summary>
    /// Asks winget whether a newer RTSS is available. Returns the newer version, "" when there is none,
    /// or null when winget could not be asked (missing, offline, busy, or it failed).
    /// </summary>
    public static async Task<string?> FindUpgradeAsync()
    {
        if (IsBusy || FindWinget() is not { } winget || !NetworkInterface.GetIsNetworkAvailable()) return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(winget)
            {
                ArgumentList =
                {
                    "list", "--id", PackageId, "--exact", "--upgrade-available",
                    "--source", "winget", "--accept-source-agreements", "--disable-interactivity",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null) return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException or NotSupportedException) { }
                throw;
            }

            // Headers are translated, so find the row by id and read what follows it:
            // "… Guru3D.RTSS 7.3.6 7.3.7" is installed, then available (a Source column may follow,
            // and installed can read "< 7.3.6").
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t != "<" && !t.Equals("winget", StringComparison.OrdinalIgnoreCase)).ToArray();
                var at = Array.FindIndex(tokens, t => t.Equals(PackageId, StringComparison.OrdinalIgnoreCase));
                if (at >= 0 && tokens.Length - at - 1 >= 2) return tokens[^1];
            }

            // 0x8A150014: no installed package matched, i.e. nothing to upgrade.
            return process.ExitCode is 0 or unchecked((int)0x8A150014) ? "" : null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            Log.Write($"rtss upgrade check failed: {error.Message}");
            return null;
        }
    }

    private static async Task<RtssInstallResult> RunAsync(string verb, IProgress<RtssInstallStage>? progress)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return new RtssInstallResult(RtssInstallOutcome.AlreadyInstalling);

        try
        {
            if (FindWinget() is not { } winget) return new RtssInstallResult(RtssInstallOutcome.WingetMissing);
            if (await Task.Run(IsSetupRunning)) return new RtssInstallResult(RtssInstallOutcome.AlreadyInstalling);
            if (!NetworkInterface.GetIsNetworkAvailable()) return new RtssInstallResult(RtssInstallOutcome.Offline);

            progress?.Report(RtssInstallStage.Preparing);

            Process? process;
            try
            {
                process = Process.Start(new ProcessStartInfo(winget)
                {
                    ArgumentList =
                    {
                        verb, "--id", PackageId, "--exact",
                        "--source", "winget",              // skips the Store source, which fails in some regions
                        "--silent",
                        "--accept-source-agreements", "--accept-package-agreements",
                        "--disable-interactivity",
                    },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                });
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
            {
                Log.Write($"rtss winget start failed: {error.Message}");
                return new RtssInstallResult(RtssInstallOutcome.CouldNotStart);
            }

            if (process is null) return new RtssInstallResult(RtssInstallOutcome.CouldNotStart);

            using (process)
            {
                using var timeout = new CancellationTokenSource(Timeout);

                // Neither watcher is awaited: an elevated installer can hold the output pipe open
                // after winget itself has gone, and the result must not wait on that.
                _ = WatchOutputAsync(process, progress);
                _ = WatchSetupAsync(process, progress, timeout.Token);

                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException or NotSupportedException) { }

                    Log.Write($"rtss winget {verb} timed out");
                    return new RtssInstallResult(RtssInstallOutcome.TimedOut);
                }

                Log.Write($"rtss winget {verb} exited 0x{process.ExitCode:X8}");
                return new RtssInstallResult(RtssInstallOutcome.Finished, process.ExitCode);
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    /// Drains winget's output (so it never blocks on a full pipe) and spots the download starting.
    /// winget's messages are translated, so the only thing matched is the installer URL it prints.
    /// </summary>
    private static async Task WatchOutputAsync(Process process, IProgress<RtssInstallStage>? progress)
    {
        try
        {
            var reported = false;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (reported || !line.Contains("https://", StringComparison.OrdinalIgnoreCase)) continue;
                reported = true;
                progress?.Report(RtssInstallStage.Downloading);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>Follows the RTSS setup program itself: running means installing, gone again means finishing.</summary>
    private static async Task WatchSetupAsync(Process process, IProgress<RtssInstallStage>? progress, CancellationToken cancel)
    {
        try
        {
            var seenSetup = false;
            while (!cancel.IsCancellationRequested && !process.HasExited)
            {
                await Task.Delay(750, cancel);

                if (await Task.Run(IsSetupRunning, cancel))
                {
                    if (!seenSetup) progress?.Report(RtssInstallStage.Installing);
                    seenSetup = true;
                }
                else if (seenSetup)
                {
                    progress?.Report(RtssInstallStage.Finishing);
                    return;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
    }
}
