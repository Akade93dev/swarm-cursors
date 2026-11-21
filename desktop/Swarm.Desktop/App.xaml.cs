using System.Threading;
using System.Windows;
using Swarm.Core.Diagnostics;

namespace Swarm.Desktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\SwarmDesktop.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Swarm Desktop 已在运行，请使用任务栏中的现有窗口。", "Swarm Desktop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        FileLog.Write("desktop", "start");
        DispatcherUnhandledException += (_, args) => FileLog.Write("desktop", $"unhandled: {args.Exception}");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLog.Write("desktop", "exit");
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
            _ownsMutex = false;
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
