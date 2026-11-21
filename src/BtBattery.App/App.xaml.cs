using System.Windows;
using System.Windows.Threading;

namespace BtBattery.App;

public partial class App : Application
{
    private TrayApplication? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("界面线程出现异常。", args.Exception);
            args.Handled = true;
        };

#if !NETFRAMEWORK
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        }
        catch (InvalidOperationException)
        {
            // DPI 模式已经由程序清单设置过。
        }
#endif

        var context = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Dispatcher);
        SynchronizationContext.SetSynchronizationContext(context);
        _tray = new TrayApplication(e.Args, context);
        if (!_tray.Start())
            Shutdown();
    }
}
