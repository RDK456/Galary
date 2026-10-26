using System.Windows;
using Velopack;
using Velopack.Sources;

namespace Galary.Services;

// Checks GitHub Releases for a newer version; Velopack downloads only the delta when one exists.
public static class Updater
{
    const string Repo = "https://github.com/RDK456/Galary";

    // say != null means the user clicked the button: report progress/results instead of staying quiet.
    public static async Task CheckAsync(Action<string>? say = null)
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(Repo, null, false));
            if (!mgr.IsInstalled) { say?.Invoke("Updates work in the installed version only"); return; } // dev / portable run
            say?.Invoke("Checking for updates…");
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) { say?.Invoke($"Galary {mgr.CurrentVersion} is up to date"); return; }
            say?.Invoke($"Downloading {info.TargetFullRelease.Version}…");
            await mgr.DownloadUpdatesAsync(info);
            var ask = Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                $"Galary {info.TargetFullRelease.Version} is ready. Restart now to update?",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information));
            if (ask == MessageBoxResult.Yes) mgr.ApplyUpdatesAndRestart(info);
        }
        catch { say?.Invoke("Couldn't reach the update server"); } // auto check stays quiet
    }
}
