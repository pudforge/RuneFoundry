using System.Windows;
using RuneFoundry.UI;

namespace RuneFoundry.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Started as its own elevated helper: no window, no session, just the install.
        // Returning before base.OnStartup is what keeps StartupUri from opening one.
        if (e.Args.Length == 2 && e.Args[0] == RuneFoundry.Core.ElevatedApply.Switch)
        {
            Shutdown(RuneFoundry.Core.ElevatedApply.Execute(e.Args[1]));
            return;
        }

        // Before anything can throw: a window that closes without a word leaves nothing
        // to fix it with.
        CrashLog.Watch(this, "");

        base.OnStartup(e);
    }
}
