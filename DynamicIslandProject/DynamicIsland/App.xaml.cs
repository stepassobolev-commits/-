using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

public partial class App : Application
{
    private const string MutexName = "DynamicIsland.SingleInstance.7C4A7A20";
    private const string ShowEventName = "DynamicIsland.ShowSettings.7C4A7A20";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var created);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!created)
        {
            // Приложение уже запущено — просим его показать окно настроек и выходим.
            if (!e.Args.Contains("--background")) _showEvent.Set();
            Shutdown();
            return;
        }

        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => ShowSettings()),
            null, Timeout.Infinite, false);

        base.OnStartup(e);

        // При обычном запуске (ярлык) открываем настройки, при автозапуске — работаем в фоне.
        if (!e.Args.Contains("--background"))
            Dispatcher.BeginInvoke(() => ShowSettings(), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Открыть окно настроек (если уже открыто — показать поверх остальных).</summary>
    public void ShowSettings(string? page = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        if (page is not null) _settingsWindow.ShowPage(page);

        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;   // Выводим поверх, затем возвращаем обычный режим.
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        AppSettings.SaveNow();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
