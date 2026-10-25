using System.Windows;
using Velopack;
using Velopack.Sources;

namespace Galary.Services;

// Checks GitHub Releases for a newer version; Velopack downloads only the delta when one exists.
public static class Updater
{
    const string Repo = "https://github.com/RDK456/Galary";

    public static async Task CheckAsync()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(Repo, null, false));
            if (!mgr.IsInstalled) return; // dev / portable run
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return;
            await mgr.DownloadUpdatesAsync(info);
            var ask = Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                $"Galary {info.TargetFullRelease.Version} is ready. Restart now to update?",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information));
            if (ask == MessageBoxResult.Yes) mgr.ApplyUpdatesAndRestart(info);
        }
        catch { /* offline or no release yet: stay quiet */ }
    }
}
