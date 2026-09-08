using System.Configuration;
using RuneFoundry.UI;
using System.Data;
using System.Windows;

namespace RuneFoundry.Editor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
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

        base.OnStartup(e);
    }
}

