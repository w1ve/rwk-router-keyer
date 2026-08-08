namespace WKRClient;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            File.AppendAllText("crash.log", $"[{DateTime.Now}] {e.Exception}\n\n");
            MessageBox.Show(e.Exception.Message, "WKRClient Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            File.AppendAllText("crash.log", $"[{DateTime.Now}] {e.ExceptionObject}\n\n");
        };

        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
