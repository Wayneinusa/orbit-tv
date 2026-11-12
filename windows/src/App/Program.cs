namespace SuperiorIptv.Win;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
        {
            Theme.Scale = Math.Clamp(g.DpiX / 96f, 1f, 3f);
        }

        int smoke = Array.IndexOf(args, "--self-test");
        if (smoke >= 0 && args.Length > smoke + 2) return SelfTest.Run(args[smoke + 1], args[smoke + 2]);

        using var single = new Mutex(true, "SuperiorIPTVPlayer.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show(Storage.AppName + " is already running.", Storage.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using var app = new AppServices();
        var form = new MainForm(app);
        form.Shown += async (_, _) =>
        {
            await Task.Run(() => app.Library.LoadFromDisk());
            app.Downloads.Start();
            if (app.Library.Playlists.Count == 0)
            {
                using var welcome = new ActionDialog("Welcome to " + Storage.AppName, "",
                    "Add the playlist from your TV provider to get started: an Xtream Codes login or an M3U address. " +
                    "This app is a player only and does not include any channels.");
                welcome.AddAction("Add playlist…", Theme.Glyph.Add, () =>
                {
                    using var dialog = new PlaylistDialog(app, null);
                    dialog.ShowDialog(form);
                });
                welcome.AddClose("Later");
                welcome.ShowDialog(form);
            }
            else
            {
                _ = Task.Run(() => app.Library.RefreshStaleAsync());
            }
        };
        Application.Run(form);
        return 0;
    }

    public static void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(Storage.File("errors.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}
