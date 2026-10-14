using Microsoft.UI.Xaml;
using PegLine.Core;
using PegLine.Hosting;

namespace PegLine;

public partial class App : Application
{
    private AppHost? _host;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost();
        _host.Start();
    }

    public static void Log(Exception exception)
    {
        try
        {
            var directory = SettingsStore.DefaultDirectory();
            Directory.CreateDirectory(directory);
            var line = $"[{DateTimeOffset.Now:u}] {exception}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "error.log"), line);
        }
        catch
        {
            // Logging must not take the process down. The file is local only.
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log(e.Exception);
        }
    }
}
