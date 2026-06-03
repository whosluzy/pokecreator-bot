using System.Text;

namespace pokecreator_setup;

static class Program
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "crash.log");

    [STAThread]
    static void Main()
    {
        // Never let the app vanish silently. Catch every flavour of unhandled error,
        // write it to crash.log next to the exe, and show it instead of closing.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, "background thread");
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception, "task"); e.SetObserved(); };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Report(ex, "startup");
        }
    }

    private static void Report(Exception? ex, string where)
    {
        if (ex is null) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Unhandled error ({where}), v{Updater.AppVersion}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 60));
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch { /* logging must never throw */ }

        try
        {
            MessageBox.Show(
                $"Something went wrong, but the app will stay open.\n\n{ex.Message}\n\nDetails were saved to:\n{LogPath}",
                "PokeCreator — error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
