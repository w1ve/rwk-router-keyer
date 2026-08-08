namespace WinKeyerEmulator.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Catch all unhandled exceptions and log to file instead of crashing
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            LogCrash("UI Thread", e.Exception);
            MessageBox.Show($"Error: {e.Exception.Message}\n\nSee crash.log for details.",
                "WKRServer Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogCrash("AppDomain", ex);
            if (e.IsTerminating)
            {
                MessageBox.Show($"Fatal error: {ex?.Message}\n\nSee crash.log for details.",
                    "WKRServer Fatal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {ex}\n\n";
            File.AppendAllText(path, msg);
        }
        catch { }
    }
}
