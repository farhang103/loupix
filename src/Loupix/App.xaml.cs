using System.Windows;
using System.Windows.Threading;
using Loupix.Services;

namespace Loupix;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Developer helper: write the app icon file used by the build (loupix.exe --write-icon <path>).
        if (e.Args.Length == 2 && e.Args[0] == "--write-icon")
        {
            UI.IconFactory.WriteIco(e.Args[1], 16, 20, 24, 32, 40, 48, 64, 128, 256);
            Shutdown();
            return;
        }

#if DEBUG
        if (e.Args.Length == 2 && e.Args[0] == "--selftest")
        {
            base.OnStartup(e);
            Dispatcher.BeginInvoke(async () =>
            {
                try { await Dev.SelfTest.RunAsync(e.Args[1]); }
                catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "crash.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }
#endif
        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Already running: hand over our arguments (or "show the editor") and exit.
            SingleInstance.SendToFirst(e.Args.Length > 0 ? e.Args : ["loupix://show"]);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Unobserved task", ev.Exception); ev.SetObserved(); };

        _instance.ArgumentsReceived += args => Dispatcher.BeginInvoke(() => AppController.Instance.HandleArgs(args));
        _instance.StartListening();
        AppController.Instance.Start(e.Args);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        UI.ToastWindow.Show("Something went wrong", e.Exception.Message, seconds: 4);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
