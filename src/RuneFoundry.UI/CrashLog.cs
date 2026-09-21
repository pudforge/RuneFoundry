using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RuneFoundry.UI;

/// <summary>
/// Catches what would otherwise close the program without a word.
///
/// A WPF exception on the UI thread ends the process. No message, no file, nothing to send
/// anybody: the window is simply gone, and the only thing its author can report is that it
/// crashed. That is not enough to fix anything, and it has cost this project real time.
///
/// <para>
/// So every unhandled exception is written to a file beside the settings, and shown once.
/// The program then carries on where it can: a fault while drawing a list is not a reason
/// to lose the project somebody has open.
/// </para>
/// </summary>
public static class CrashLog
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RuneFoundry", "crash.log");

    /// <summary>Attaches to an application. Call once, at startup.</summary>
    public static void Watch(Application application, string program)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            var told = Record(program, e.Exception, "on the window thread");

            // Continuing is the right default: the thing that threw is usually one control,
            // and closing would throw away unsaved work in every other part of the editor.
            e.Handled = true;
            Show(e.Exception, told);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Record(program, error, "on a background thread");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record(program, e.Exception, "in a task nobody waited on");
            e.SetObserved();
        };
    }

    /// <summary>Appends one entry. Returns whether it reached the file.</summary>
    private static bool Record(string program, Exception error, string where)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            File.AppendAllText(Path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {program}  {where}{Environment.NewLine}"
                + error + Environment.NewLine
                + new string('-', 78) + Environment.NewLine);

            return true;
        }
        catch (Exception)
        {
            // A crash log that cannot be written is not worth a second crash.
            return false;
        }
    }

    private static void Show(Exception error, bool logged)
    {
        try
        {
            // The type as well as the message: "object reference not set" on its own has
            // never told anybody which object.
            var what = error.GetType().Name + ": " + error.Message;

            MessageBox.Show(
                what + "\n\n"
                + (logged
                    ? "The details are in " + Path + "\n\nRuneFoundry is still running. "
                      + "Save your work, then restart it."
                    : "RuneFoundry is still running. Save your work, then restart it."),
                "Something went wrong",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            // Showing the message is the last thing that should be able to fail.
        }
    }
}
