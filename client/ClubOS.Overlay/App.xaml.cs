using System.Windows;
using System.Windows.Threading;
using ClubOS.Shared;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

public partial class App : Application
{
    private Mutex? _single;
    private WindowCoordinator? _coordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, "ClubOS.Overlay", out var first);
        if (!first)               // второй экземпляр не нужен: экран блокировки должен быть один
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        var options = Options.Parse(e.Args);
        var client = new PipeAgentClient(options.PipeName);
        var flow = new FlowController(client);
        var launcher = new LauncherModel(flow, client, new WindowsAppLauncher());
        _coordinator = new WindowCoordinator(flow, launcher, options);
        _coordinator.Start();
    }

    /// <summary>Ошибка в интерфейсе не должна ронять экран блокировки: пишем в журнал и продолжаем.</summary>
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Необработанная ошибка интерфейса", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
