using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Loupix.Services.Updates;
using Loupix.UI;

namespace Loupix.Services;

public enum UpdateStatus { Idle, Checking, UpToDate, CheckFailed, Available, Downloading, WaitingForEditor }

/// <summary>Finds new versions on GitHub and installs them. It goes online only while automatic checks are on, or when the user asks.</summary>
public sealed class Updater(Func<bool> editorInUse, Action quit)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "Loupix-Update");
    private static readonly HttpClient Http = CreateClient();

    private DispatcherTimer? _timer;
    private CancellationTokenSource? _download;

    public static Version CurrentVersion { get; } = typeof(Updater).Assembly.GetName().Version!;

    public UpdateStatus Status { get; private set; }
    public AvailableUpdate? Update { get; private set; }
    public double Progress { get; private set; }

    /// <summary>The status or download progress changed.</summary>
    public event Action? Changed;

    /// <summary>A good moment to offer a known update, if the user still wants to hear about it.</summary>
    public event Action? Prompt;

    private static AppSettings Settings => SettingsStore.Current;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Loupix/{CurrentVersion.ToString(3)}"); // GitHub rejects requests without one
        return http;
    }

    public void Start()
    {
        try { Directory.Delete(Folder, recursive: true); } catch { } // the installer left over from the last update

        // The first check waits a little so that launching and signing in stay quiet.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => _ = OnTickAsync();
        _timer.Start();
    }

    private async Task OnTickAsync()
    {
        _timer!.Interval = TimeSpan.FromHours(1); // Loupix can run for weeks, so keep looking for a due check
        if (!Settings.CheckForUpdates) return;
        if (Settings.LastUpdateCheck is not { } last || DateTime.UtcNow - last >= CheckEvery) await CheckAsync();
        Prompt?.Invoke();
    }

    public async Task CheckAsync(bool userAsked = false)
    {
        if (Status == UpdateStatus.Checking || _download != null) return;
        var before = Status;
        if (userAsked) Set(UpdateStatus.Checking);
        try
        {
            var json = await Http.GetStringAsync(ReleaseFeed.LatestUrl);
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var update = ReleaseFeed.Parse(json, CurrentVersion, arch);
            Settings.LastUpdateCheck = DateTime.UtcNow;
            SettingsStore.Save();
            if (_download != null) return; // the user started an install while this was running
            Update = update;
            Set(update != null ? UpdateStatus.Available : UpdateStatus.UpToDate);
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.Message}");
            Set(userAsked ? UpdateStatus.CheckFailed : before);
        }
    }

    /// <summary>Downloads and runs the installer, or opens the release page for a copy that wasn't installed with it.</summary>
    public async Task InstallAsync()
    {
        if (Update is not { } update || _download != null) return;
        if (update.InstallerUrl == null || !InstalledWithSetup)
        {
            Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
            return;
        }

        _download = new CancellationTokenSource();
        Progress = 0;
        Set(UpdateStatus.Downloading);
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, Path.GetFileName(new Uri(update.InstallerUrl).LocalPath));
            var progress = new Progress<double>(ReportProgress);
            await ReleaseFeed.DownloadAsync(Http, update.InstallerUrl, path, update.Sha256, progress, _download.Token);

            // The installer closes Loupix, so don't take away a screenshot the user is still working on.
            if (editorInUse())
            {
                Set(UpdateStatus.WaitingForEditor);
                while (editorInUse()) await Task.Delay(1000, _download.Token);
            }
            Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1") { UseShellExecute = true });
            quit();
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            Set(UpdateStatus.Available);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            Set(UpdateStatus.Available);
            ToastWindow.Show("Couldn't update Loupix", "Check your internet connection and try again.");
        }
        finally
        {
            _download.Dispose();
            _download = null;
        }
    }

    public void CancelInstall() => _download?.Cancel();

    private static bool InstalledWithSetup =>
        File.Exists(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "unins000.exe"));

    private void ReportProgress(double value)
    {
        // Whole percents only, so that a fast download doesn't flood the UI.
        if ((int)(value * 100) == (int)(Progress * 100)) return;
        Progress = value;
        Changed?.Invoke();
    }

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

#if DEBUG
    /// <summary>Self-test hook: pretend to be in a given state.</summary>
    internal void SetForTest(UpdateStatus status, AvailableUpdate? update, double progress)
    {
        Update = update;
        Progress = progress;
        Set(status);
    }
#endif
}
