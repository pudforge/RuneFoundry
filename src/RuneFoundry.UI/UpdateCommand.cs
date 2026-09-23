using System.Diagnostics;
using System.IO;
using System.Windows;

using RuneFoundry.Core;

namespace RuneFoundry.UI;

/// <summary>
/// Help → Check for updates, the same in both programs.
///
/// Explicit on purpose: nothing is checked or downloaded until someone asks, and nothing is
/// installed until they agree. An update replaces the whole install, so it closes this
/// program, lets a script swap the files, and starts it again.
/// </summary>
public static class UpdateCommand
{
    /// <param name="program">"Editor" or "Launcher": which standalone executable this is.</param>
    public static async Task Run(Window owner, string program)
    {
        var layout = InstallLayout.Current(program);

        var release = await Working.While(owner, "Checking for updates…",
            () => Updates.GetLatestAsync().GetAwaiter().GetResult(),
            ex => Ui.Failed(owner, "check for updates", ex));
        if (release is null) return;

        if (!release.IsNewerThanCurrent)
        {
            Ui.Info(owner, "No update", $"You have the latest version, RuneFoundry {AppVersion.Current}.");
            return;
        }

        if (!layout.IsReleaseInstall)
        {
            Ui.Info(owner, "Update available",
                $"RuneFoundry {release.Version} is out. This copy was not installed from a release "
                + $"download, so it cannot update itself.\n\n{release.PageUrl}");
            return;
        }

        var asset = Updates.PickAsset(release, layout);
        if (asset is null)
        {
            Ui.Failed(owner, "update",
                $"RuneFoundry {release.Version} has no download for this kind of install. Get it from {release.PageUrl}");
            return;
        }

        var other = OtherProgramsRunning(layout);
        if (other.Count > 0)
        {
            Ui.Error(owner, "Close the other program first",
                $"{string.Join(" and ", other)} is open from the same folder and uses the files the "
                + "update replaces. Close it, then check for updates again.");
            return;
        }

        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : $"\n\n{release.Notes.Trim()}";
        if (!Ui.Confirm(owner, "Update RuneFoundry?",
                $"RuneFoundry {release.Version} is available. You have {AppVersion.Current}.{notes}\n\n"
                + $"Download it ({asset.Size / (1024.0 * 1024):N0} MB) and install it now? "
                + $"RuneFoundry {program} will close and start again."))
            return;

        var work = Path.Combine(Path.GetTempPath(), "RuneFoundry-update");
        var log = Path.Combine(work, "update.log");

        var script = await Working.While(owner, $"Downloading RuneFoundry {release.Version}…", () =>
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            var downloaded = Updates.DownloadAsync(asset, work).GetAwaiter().GetResult();
            var pid = Environment.ProcessId;

            if (layout.IsStandalone)
                return UpdateScript.ForStandalone(pid, downloaded, layout.ExePath,
                    Updates.StandaloneTarget(layout.ExePath, AppVersion.Current, release.Version), log);

            var staging = Updates.StageZip(downloaded, Path.Combine(work, "staged"));
            return UpdateScript.ForFolder(pid, staging, layout.Directory, layout.ExePath, log);
        }, ex => Ui.Failed(owner, "download the update", ex));
        if (script is null) return;

        var scriptPath = Path.Combine(work, "update.ps1");
        // With a BOM, so Windows PowerShell reads the paths as UTF-8 and not the ANSI codepage.
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));

        var start = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // Program Files needs administrator rights; Windows asks for them here, once.
        if (!Updates.CanWrite(layout.Directory)) start.Verb = "runas";

        // Closed the ordinary way, so the Editor saves what it saves on exit, and the update
        // starts only once the window has really gone: a close that is called off leaves
        // everything as it was.
        owner.Closed += (_, _) =>
        {
            try
            {
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Ui.Failed(null, "start the update",
                    ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                        ? "Administrator rights were declined. Nothing was changed."
                        : ex.Message);
            }
        };
        owner.Close();
    }

    /// <summary>The other RuneFoundry program, when it is running from this same install.</summary>
    private static List<string> OtherProgramsRunning(InstallLayout layout)
    {
        var names = new List<string>();
        if (layout.IsStandalone) return names;   // each standalone exe is its own install

        foreach (var name in new[] { "RuneFoundry Editor", "RuneFoundry Launcher" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is not null && string.Equals(Path.GetDirectoryName(path), layout.Directory,
                                StringComparison.OrdinalIgnoreCase))
                            names.Add(name);
                    }
                    catch
                    {
                        // An elevated copy cannot be inspected from here. Name it anyway: if it
                        // is from this folder the copy would fail, and closing it costs little.
                        names.Add(name);
                    }
                }
            }
        }
        return names.Distinct().ToList();
    }
}
