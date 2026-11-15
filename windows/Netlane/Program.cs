namespace Netlane;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // After a self-update the new copy starts while the old one is still exiting; wait so the single-instance check passes.
        Updater.HandleStartup(args);
        using var mutex = new Mutex(true, "Netlane.SingleInstance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        // NotifyIcon isn't a Control, so install the UI context explicitly before any async work starts.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
    }

    private static void Log(Exception? e)
    {
        if (e is null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"{DateTime.Now:u} {e}\n\n");
        }
        catch (IOException) { }
    }
}
