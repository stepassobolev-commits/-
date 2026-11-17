using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace DynamicIsland;

public sealed class ClipboardEntry : INotifyPropertyChanged
{
    private bool _isPinned;

    public ClipboardEntry(string text, DateTime copiedAt)
    {
        Text = text;
        CopiedAt = copiedAt;
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        Preview = flat.Length > 140 ? flat[..140] + "…" : flat;
    }

    public string Text { get; }
    public DateTime CopiedAt { get; }
    public string Preview { get; }
    public string TimeText => CopiedAt.ToString("HH:mm");

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinOpacity)));
        }
    }

    public double PinOpacity => IsPinned ? 1.0 : 0.55;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    // Окно всегда одного размера — это убирает лаги. Размеры капсулы и панели берутся из настроек.
    private const double ExpandedW = 340;

    private static AppSettings S => AppSettings.Current;

    private static double ExpandedH
    {
        get
        {
            var s = S;
            var height = 19.0 + 39.0 + 15.0;
            if (s.ShowPlayer) height += 151;
            if (s.ShowVolume) height += 31;
            if (s.ShowClipboard) height += 221;
            if (s.ShowSystemStats) height += 48;
            return height;
        }
    }

    private readonly ObservableCollection<ClipboardEntry> _history = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    private string _lastClipboard = "";
    private string _trackKey = "";
    private int _limit = 5;
    private bool _paused;
    private bool _expanded;
    private bool _refreshingMedia;
    private bool _volumeUpdateFromCode;
    private bool _ready;
    private bool _applying;

    // Пружинная анимация: _p = 0 (капсула) ... 1 (раскрыто).
    private double _p;
    private double _v;
    private double _target;
    private bool _rendering;
    private TimeSpan _lastRender = TimeSpan.Zero;

    // Пружина ширины капсулы при музыке и «биты» эквалайзера.
    private double _wp;
    private double _wv;
    private double _wTarget;
    private bool _mediaActive;
    private bool _playing;
    private double _level;
    private double _cacheScale;
    private IntPtr _hwnd;
    private bool _smoothReady;
    private int _winX = int.MinValue;
    private int _winY = int.MinValue;
    private int _winW;
    private int _winH;
    private bool _prewide;
    private DateTime _deviceStamp = DateTime.MinValue;

    // Музыка: спектр, цвет обложки, название в капсуле.
    private AudioSpectrum? _spectrum;
    private readonly float[] _spectrumBuffer = new float[AudioSpectrum.Bands];
    private readonly double[] _barLevels = new double[AudioSpectrum.Bands];
    private Color? _artColor;
    private DateTime _titleUntil = DateTime.MinValue;
    private double _tp;
    private double _tv;
    private double _tTarget;

    // Дополнения.
    private readonly DispatcherTimer _titleTimer = new() { Interval = TimeSpan.FromSeconds(3.6) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private double _cpuSmooth = -1;
    private double _ramSmooth = -1;
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private bool _hiddenByFullscreen;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt Monitor;
        public RectInt Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectInt rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    private ScaleTransform[] _bars = Array.Empty<ScaleTransform>();
    private readonly Stopwatch _beatClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _beatTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _audioDevice;

    public MainWindow()
    {
        InitializeComponent();
        // Режим отрисовки выбирается до показа окна: «плавный» (через DWM) или «совместимый» (прозрачное окно).
        AllowsTransparency = !S.SmoothMode;
        _bars = new[] { EqScale1, EqScale2, EqScale3, EqScale4, EqScale5 };
        ClipboardItems.ItemsSource = _history;
        _history.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
        ApplySettings();
        _ready = true;
        AppSettings.Changed += ApplySettings;

        _collapseTimer.Tick += (_, _) => OnCollapseTimer();
        _expandTimer.Tick += (_, _) => OnExpandTimer();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = ""; };
        _beatTimer.Tick += (_, _) => OnBeatTick();
        _titleTimer.Tick += (_, _) =>
        {
            _titleTimer.Stop();
            UpdateTitleDisplay();
        };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();

        Loaded += async (_, _) =>
        {
            PositionWindow();
            UpdateClock();

            // «Прогрев»: раскрытая панель строится один раз заранее, чтобы первое раскрытие не подтормаживало.
            try
            {
                ExpandedPanel.Visibility = Visibility.Visible;
                UpdateLayout();
                ApplyProgress();
            }
            catch { }

            _clockTimer.Tick += (_, _) => OnClockTick();
            _clockTimer.Start();

            _clipboardTimer.Tick += (_, _) => ReadClipboard();
            _clipboardTimer.Start();

            RefreshVolume();

            await InitializeMediaAsync();
            _mediaTimer.Tick += async (_, _) =>
            {
                if (_audioDevice is null) RefreshVolume();
                await RefreshMediaAsync();
            };
            _mediaTimer.Start();
        };

        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
    }

    // ---------- Плавный режим: окно повторяет размер острова ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!S.SmoothMode) return;

        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(_hwnd);
            if (source?.CompositionTarget is not null) source.CompositionTarget.BackgroundColor = Colors.Transparent;

            // Прозрачность средствами DWM: окно рисуется видеокартой и показывается синхронно с экраном.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            var doNotRound = 1;                                   // без скруглений и рамки Windows 11
            DwmSetWindowAttribute(_hwnd, 33, ref doNotRound, sizeof(int));
            var noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));

            _smoothReady = true;
            PositionSmoothWindow(RootBorder.Width, RootBorder.Height);
        }
        catch
        {
            _smoothReady = false;
        }
    }

    /// <summary>Сколько физических пикселей в одной единице разметки острова (масштаб × DPI).</summary>
    private double PixelsPerUnit()
    {
        var scale = Math.Clamp(S.UiScale, 0.5, 1.5);
        var dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        return Math.Max(0.5, scale * dpi);
    }

    /// <summary>Чётное число пикселей: тогда остров ровно по центру окна, без полупикселей (они дают мыло и дрожание).</summary>
    private static int EvenPx(double px)
    {
        var v = Math.Max(2, (int)Math.Round(px));
        return v % 2 == 0 ? v : v + 1;
    }

    /// <summary>Рабочая область основного монитора в физических пикселях.</summary>
    private RectInt PrimaryWorkAreaPx()
    {
        try
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitor = MonitorFromWindow(IntPtr.Zero, 1); // MONITOR_DEFAULTTOPRIMARY
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.Work;
        }
        catch { }

        var dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        return new RectInt
        {
            Left = 0,
            Top = 0,
            Right = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi),
            Bottom = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi)
        };
    }

    /// <summary>
    /// Размер и положение окна. Во время движения окно НЕ меняет размер каждый кадр (это и давало тряску):
    /// оно один раз растёт до максимума, а точно по острову подгоняется, когда движение закончилось.
    /// </summary>
    private void PositionSmoothWindow(double localWidth, double localHeight, bool exact = true)
    {
        if (!_smoothReady || _hwnd == IntPtr.Zero || double.IsNaN(localWidth) || double.IsNaN(localHeight)) return;

        var s = S;
        var k = PixelsPerUnit();
        var top = (int)Math.Round(s.TopOffset * k);
        var islandW = EvenPx(localWidth * k);
        var islandH = Math.Max(1, (int)Math.Round(localHeight * k));
        var widthPx = islandW;
        var heightPx = top + islandH;

        if (exact) _prewide = false;
        else
        {
            var wide = _prewide || _target > 0 || _p > 0.002;
            var boundW = wide ? ExpandedW * 1.12 : (s.CompactWidth + s.MediaExtraWidth + s.TitleExtraWidth) * 1.15;
            var boundH = wide ? ExpandedH * 1.12 + 2.0 : s.CompactHeight * 1.15 + 2.0;
            widthPx = Math.Max(widthPx, EvenPx(boundW * k));
            heightPx = Math.Max(heightPx, top + (int)Math.Ceiling(boundH * k));
        }

        var area = PrimaryWorkAreaPx();
        var xPx = area.Left + (area.Right - area.Left - widthPx) / 2;
        var yPx = area.Top;
        if (xPx == _winX && yPx == _winY && widthPx == _winW && heightPx == _winH) return;

        _winX = xPx;
        _winY = yPx;
        _winW = widthPx;
        _winH = heightPx;

        // SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS
        SetWindowPos(_hwnd, IntPtr.Zero, xPx, yPx, widthPx, heightPx, 0x0004 | 0x0010 | 0x0100);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _winW = 0;
        if (_ready) ApplyProgress();
    }

    // ---------- Положение окна ----------

    private void PositionWindow()
    {
        if (_smoothReady)
        {
            PositionSmoothWindow(RootBorder.Width, RootBorder.Height);
            return;
        }
        var area = SystemParameters.WorkArea;
        var dpiX = 1.0;
        try { dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        Left = Math.Round((area.Left + (area.Width - Width) / 2) * dpiX) / dpiX;
        Top = Math.Round(area.Top * dpiX) / dpiX;
    }

    // ---------- Наведение мыши ----------

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_expanded || !S.ExpandOnHover) return;

        // Окно увеличиваем заранее, пока мышь только подошла: само раскрытие тогда стартует без рывка.
        if (_smoothReady && !_rendering && !_prewide)
        {
            _prewide = true;
            PositionSmoothWindow(RootBorder.Width, RootBorder.Height, false);
        }

        if (S.ExpandDelayMs <= 0)
        {
            Expand();
            return;
        }
        _expandTimer.Stop();
        _expandTimer.Start();
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // В режиме «по клику» остров раскрывается нажатием на капсулу.
        if (!_expanded) Expand();
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _expandTimer.Stop();
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void OnExpandTimer()
    {
        _expandTimer.Stop();
        if (RootBorder.IsMouseOver) Expand();
    }

    private void OnCollapseTimer()
    {
        _collapseTimer.Stop();
        if (RootBorder.IsMouseOver) return;

        // Не сворачиваем, пока тянут ползунок или открыто контекстное меню.
        if (Mouse.LeftButton == MouseButtonState.Pressed || IslandMenu.IsOpen)
        {
            _collapseTimer.Start();
            return;
        }

        // Мышь ушла, не раскрыв остров: возвращаем окну точный размер.
        if (!_expanded && !_rendering)
        {
            if (_prewide && _smoothReady) PositionSmoothWindow(RootBorder.Width, RootBorder.Height);
            _prewide = false;
            return;
        }
        Collapse();
    }

    private void Expand()
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _expanded = true;

        UpdateClock();
        SetTarget(1);
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        SetTarget(0);
    }

    // ---------- Пружинная анимация (как в macOS) ----------

    private void SetTarget(double target)
    {
        _target = target;
        if (!S.AnimationsEnabled)
        {
            _p = target;
            _v = 0;
            ApplyProgress();
            OnMotionSettled();
            return;
        }
        EnsureRendering();
    }

    private void SetMediaWide(bool wide)
    {
        var newTarget = wide ? 1.0 : 0.0;
        if (_wTarget == newTarget) return; // таймер плеера зовёт это каждые 1,5 с — без изменений ничего не запускаем
        _wTarget = newTarget;
        if (!S.AnimationsEnabled)
        {
            _wp = _wTarget;
            _wv = 0;
            ApplyProgress();
            return;
        }
        EnsureRendering();
    }

    private void EnsureRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastRender = TimeSpan.Zero;
        // Окно увеличиваем заранее, до первого кадра движения (только при реальном движении).
        if (_smoothReady && (Math.Abs(_p - _target) > 0.002 || Math.Abs(_wp - _wTarget) > 0.002 || Math.Abs(_tp - _tTarget) > 0.002))
            PositionSmoothWindow(RootBorder.Width, RootBorder.Height, false);
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private static void Integrate(ref double x, ref double v, double target, double stiffness, double damping, double dt)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240.0)));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var acceleration = -stiffness * (x - target) - damping * v;
            v += acceleration * h;
            x += v * h;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastRender == TimeSpan.Zero)
        {
            _lastRender = now;
            return;
        }

        var dt = (now - _lastRender).TotalSeconds;
        if (dt <= 0) return; // несколько событий за один кадр
        _lastRender = now;
        dt = Math.Min(dt, 1.0 / 20.0);

        // Пружина: жёсткость и затухание берутся из настроек.
        var stiffness = Math.Max(50.0, S.AnimationStiffness);
        var damping = 2.0 * Math.Clamp(S.AnimationDamping, 0.3, 1.0) * Math.Sqrt(stiffness);
        Integrate(ref _p, ref _v, _target, stiffness, damping, dt);
        Integrate(ref _wp, ref _wv, _wTarget, stiffness, damping, dt);
        Integrate(ref _tp, ref _tv, _tTarget, stiffness, damping, dt);

        var settledP = Math.Abs(_p - _target) < 0.0008 && Math.Abs(_v) < 0.01;
        var settledW = Math.Abs(_wp - _wTarget) < 0.0008 && Math.Abs(_wv) < 0.01;
        var settledT = Math.Abs(_tp - _tTarget) < 0.0008 && Math.Abs(_tv) < 0.01;
        if (settledP)
        {
            _p = _target;
            _v = 0;
        }
        if (settledW)
        {
            _wp = _wTarget;
            _wv = 0;
        }
        if (settledT)
        {
            _tp = _tTarget;
            _tv = 0;
        }

        ApplyProgress();

        if (settledP && settledW && settledT)
        {
            StopRendering();
            if (_smoothReady) PositionSmoothWindow(RootBorder.Width, RootBorder.Height); // окно точно по острову
            OnMotionSettled();
        }
    }

    private void SetTitleWide(bool wide)
    {
        var newTarget = wide ? 1.0 : 0.0;
        if (_tTarget == newTarget) return;
        _tTarget = newTarget;
        if (!S.AnimationsEnabled)
        {
            _tp = _tTarget;
            _tv = 0;
            ApplyProgress();
            return;
        }
        EnsureRendering();
    }

    /// <summary>Тяжёлые операции (звуковое устройство, плеер) выполняем только когда движение закончилось.</summary>
    private void OnMotionSettled()
    {
        if (!_expanded) return;

        // Не в тот же кадр, где закончилась анимация: иначе слышен «подлаг» в самом конце раскрытия.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!_expanded) return;
            RefreshVolume();
            _ = RefreshMediaAsync();
            UpdateStats();
        }));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    private void ApplyProgress()
    {
        var p = _p;
        var g = Math.Max(0.0, p);

        var s = S;
        var compactNow = s.CompactWidth + s.MediaExtraWidth * Math.Clamp(_wp, 0.0, 1.15)
                         + s.TitleExtraWidth * Math.Clamp(_tp, 0.0, 1.1);
        // Размеры острова всегда в целых физических пикселях: края остаются резкими, ничего не дрожит.
        var k = PixelsPerUnit();
        var width = EvenPx(Lerp(compactNow, ExpandedW, g) * k) / k;
        var height = Math.Max(1, (int)Math.Round(Lerp(s.CompactHeight, ExpandedH, g) * k)) / k;
        RootBorder.Width = width;
        RootBorder.Height = height;
        RootBorder.Margin = new Thickness(0, Math.Round(s.TopOffset * k) / k, 0, 0);
        RootBorder.BorderThickness = new Thickness(1.0 / k);

        var radius = Math.Min(Lerp(s.CompactRadius, s.ExpandedRadius, Clamp01(g)), Math.Min(width, height) / 2.0);
        var topRadius = s.FlatTop ? 0.0 : radius;
        RootBorder.CornerRadius = new CornerRadius(topRadius, topRadius, radius, radius);

        var expandedOpacity = Clamp01((p - 0.30) / 0.45);
        var compactOpacity = Clamp01(1.0 - p / 0.22);

        ExpandedPanel.Opacity = expandedOpacity;
        CompactPanel.Opacity = compactOpacity;

        var expandedVisibility = expandedOpacity > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        if (ExpandedPanel.Visibility != expandedVisibility) ExpandedPanel.Visibility = expandedVisibility;

        var compactVisibility = compactOpacity > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        if (CompactPanel.Visibility != compactVisibility) CompactPanel.Visibility = compactVisibility;

        PanelShift.Y = Math.Round((1.0 - Clamp01(p)) * -14.0 * k) / k;

        // Пока идёт движение, размер окна не трогаем (см. PositionSmoothWindow).
        if (_smoothReady) PositionSmoothWindow(width, height, !_rendering);
    }

    // ---------- Часы ----------

    private void OnClockTick()
    {
        UpdateClock();
        if (_expanded) UpdateProgress();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var time = FormatClock(now);
        ClockText.Text = time;
        ClockBig.Text = time;
        var date = now.ToString("ddd, d MMMM", ru);
        DateText.Text = char.ToUpper(date[0], ru) + date[1..];
    }

    private static string FormatClock(DateTime now)
    {
        var s = S;
        if (s.Use24Hour)
            return now.ToString(s.ShowSeconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        return now.ToString(s.ShowSeconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.InvariantCulture);
    }

    // ---------- Применение настроек ----------

    private void ApplySettings()
    {
        var s = S;

        _applying = true;
        try
        {
            _limit = s.ClipboardLimit;
            TrimHistory();
            _paused = !s.ClipboardRecording;
            RecordSwitch.IsChecked = s.ClipboardRecording;
            foreach (var radio in LimitPanel.Children.OfType<RadioButton>())
                radio.IsChecked = radio.Tag is string tag && tag == _limit.ToString(CultureInfo.InvariantCulture);
        }
        finally { _applying = false; }

        var background = s.BackgroundColorValue;
        var alpha = (byte)Math.Clamp(Math.Round(s.BackgroundOpacity * 255.0), 0, 255);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, background.R, background.G, background.B));

        var accent = new SolidColorBrush(s.AccentColorValue);
        StatusText.Foreground = accent;
        ProgressFill.Background = accent;
        ApplyEqColor();
        EqPanel.Visibility = _mediaActive && s.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;

        RootBorder.Margin = new Thickness(0, s.TopOffset, 0, 0);
        IslandScale.ScaleX = s.UiScale;
        IslandScale.ScaleY = s.UiScale;
        UpdateCacheScale(s.UiScale);
        SizeWindow();

        PlayerCard.Visibility = s.ShowPlayer ? Visibility.Visible : Visibility.Collapsed;
        VolumeRow.Visibility = s.ShowVolume ? Visibility.Visible : Visibility.Collapsed;
        ClipboardBlock.Visibility = s.ShowClipboard ? Visibility.Visible : Visibility.Collapsed;
        StatsRow.Visibility = s.ShowSystemStats ? Visibility.Visible : Visibility.Collapsed;
        ApplyBlockOrder();

        if (s.ShowSystemStats) { if (!_statsTimer.IsEnabled) _statsTimer.Start(); }
        else _statsTimer.Stop();

        if (s.HideInFullscreen)
        {
            if (!_fullscreenTimer.IsEnabled) _fullscreenTimer.Start();
        }
        else
        {
            _fullscreenTimer.Stop();
            if (_hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                Show();
            }
        }

        UpdateTitleDisplay();
        ExpandedPanel.Height = ExpandedH - 34.0;
        ExpandedPanel.Width = 308.0;

        _expandTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, s.ExpandDelayMs));
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, s.CollapseDelayMs));

        UpdateClock();
        ApplyProgress();
    }

    /// <summary>Порядок блоков внутри раскрытого острова (настраивается в приложении).</summary>
    private void ApplyBlockOrder()
    {
        var order = S.GetBlockOrder();
        for (var i = 0; i < order.Length; i++)
        {
            FrameworkElement element = order[i] switch
            {
                "player" => PlayerCard,
                "volume" => VolumeRow,
                "clipboard" => ClipboardBlock,
                _ => StatsRow
            };
            Grid.SetRow(element, i + 1);
            ExpandedPanel.RowDefinitions[i + 1].Height =
                element == ClipboardBlock ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
    }

    private void ApplyEqColor()
    {
        var s = S;
        var color = s.EqFromArtwork && _artColor is { } art ? art : s.AccentColorValue;
        var brush = new SolidColorBrush(color);
        foreach (var bar in EqPanel.Children.OfType<System.Windows.Shapes.Rectangle>()) bar.Fill = brush;
    }

    private void UpdateTitleDisplay()
    {
        var show = _mediaActive && (S.ShowTrackTitle || DateTime.UtcNow < _titleUntil);
        CompactTitle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ClockText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SetTitleWide(show);
    }

    private void UpdateStats()
    {
        if (!_expanded || !S.ShowSystemStats) return;

        var cpu = SystemStats.CpuPercent();
        var ram = SystemStats.MemoryPercent();
        _cpuSmooth = _cpuSmooth < 0 ? cpu : _cpuSmooth * 0.5 + cpu * 0.5;
        _ramSmooth = _ramSmooth < 0 ? ram : _ramSmooth * 0.5 + ram * 0.5;

        CpuText.Text = $"{_cpuSmooth:0}%";
        RamText.Text = $"{_ramSmooth:0}%";
        AnimateFill(CpuFill, CpuTrack.ActualWidth * _cpuSmooth / 100.0);
        AnimateFill(RamFill, RamTrack.ActualWidth * _ramSmooth / 100.0);
    }

    private static void AnimateFill(FrameworkElement fill, double width)
    {
        var animation = new DoubleAnimation(Math.Max(0, width), TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fill.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    // ---------- Полноэкранные игры и видео ----------

    private void CheckFullscreen()
    {
        var hide = S.HideInFullscreen && IsForegroundFullscreen();
        if (hide == _hiddenByFullscreen) return;
        _hiddenByFullscreen = hide;
        if (hide) Hide();
        else Show();
    }

    private bool IsForegroundFullscreen()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            GetWindowThreadProcessId(foreground, out var processId);
            if (processId == (uint)Environment.ProcessId) return false;

            var className = new StringBuilder(64);
            GetClassName(foreground, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

            if (!GetWindowRect(foreground, out var rect)) return false;
            var monitor = MonitorFromWindow(foreground, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
                   rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        }
        catch { return false; }
    }

    /// <summary>Кэш в виде картинки: панель не перерисовывается на каждом кадре анимации.</summary>
    private void UpdateCacheScale(double uiScale)
    {
        // В плавном режиме кэш не нужен: он делал картинку мутной.
        if (S.SmoothMode)
        {
            ExpandedPanel.CacheMode = null;
            CompactPanel.CacheMode = null;
            _cacheScale = 0;
            return;
        }

        var dpi = 1.0;
        try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        var scale = Math.Max(0.5, uiScale * dpi);
        if (Math.Abs(scale - _cacheScale) < 0.001 && ExpandedPanel.CacheMode is not null) return;

        _cacheScale = scale;
        ExpandedPanel.CacheMode = new BitmapCache(scale) { EnableClearType = false, SnapsToDevicePixels = false };
        CompactPanel.CacheMode = new BitmapCache(scale) { EnableClearType = false, SnapsToDevicePixels = false };
    }

    /// <summary>Размер окна подстраивается под масштаб интерфейса и больше не меняется во время анимации.</summary>
    private void SizeWindow()
    {
        if (_smoothReady)
        {
            PositionSmoothWindow(RootBorder.Width, RootBorder.Height);
            return;
        }
        var s = S;
        var scale = Math.Clamp(s.UiScale, 0.5, 1.5);
        var dpiX = 1.0;
        try { dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        var width = EvenPx((ExpandedW + 80.0) * scale * dpiX) / dpiX; // чётное число пикселей — остров ровно по центру
        var height = (s.TopOffset + ExpandedH * 1.1 + 24.0) * scale;
        if (Math.Abs(Width - width) > 0.5) Width = width;
        if (Math.Abs(Height - height) > 0.5) Height = height;
        if (IsLoaded) PositionWindow();
    }

    // ---------- Буфер обмена ----------

    private void ReadClipboard()
    {
        if (_paused) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var value = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(value) || value == _lastClipboard) return;
            _lastClipboard = value;

            // Не собираем то, что похоже на пароли, коды и номера карт.
            if (S.ClipboardFilterSensitive && LooksSensitive(value)) return;

            if (value.Length > 2000) value = value[..2000];
            AddEntry(value);
        }
        catch { /* Буфер может быть временно занят другим приложением. */ }
    }

    private void AddEntry(string text)
    {
        var existing = _history.FirstOrDefault(e => e.Text == text);
        if (existing is not null)
        {
            if (existing.IsPinned) return;
            _history.Remove(existing);
        }
        var pinnedCount = _history.Count(e => e.IsPinned);
        _history.Insert(pinnedCount, new ClipboardEntry(text, DateTime.Now));
        TrimHistory();
    }

    private void TrimHistory()
    {
        while (_history.Count(e => !e.IsPinned) > _limit)
        {
            var last = _history.Last(e => !e.IsPinned);
            _history.Remove(last);
        }
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ClipboardItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _lastClipboard = entry.Text;
                Clipboard.SetText(entry.Text);
                ShowStatus("Скопировано");
                return;
            }
            catch { Thread.Sleep(30); }
        }
        ShowStatus("Буфер занят");
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        entry.IsPinned = !entry.IsPinned;
        _history.Remove(entry);
        var pinnedCount = _history.Count(x => x.IsPinned);
        _history.Insert(entry.IsPinned ? 0 : pinnedCount, entry);
        TrimHistory();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        _history.Remove(entry);
    }

    private void Limit_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _applying) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var value))
        {
            S.ClipboardLimit = value;
            AppSettings.NotifyChanged();
        }
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        S.ClipboardRecording = RecordSwitch.IsChecked == true;
        AppSettings.NotifyChanged();
        ShowStatus(S.ClipboardRecording ? "Запись включена" : "Запись выключена");
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearClipboardHistory();

    /// <summary>Удаляет все записи, кроме закреплённых (вызывается и из окна настроек).</summary>
    public void ClearClipboardHistory()
    {
        foreach (var entry in _history.Where(x => !x.IsPinned).ToList())
            _history.Remove(entry);
    }

    private static bool LooksSensitive(string value)
    {
        string[] markers =
        {
            "password", "passwd", "пароль", "подтверждения", "verification code",
            "one-time code", "bearer ", "api_key", "api-key", "secret="
        };

        var lower = value.ToLowerInvariant();
        if (markers.Any(lower.Contains)) return true;

        var digits = Regex.Replace(value, @"\D", "");
        return digits.Length is >= 13 and <= 19 && PassesLuhn(digits);
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (doubleDigit)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    // ---------- Музыка ----------

    private async Task InitializeMediaAsync()
    {
        try
        {
            _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            await RefreshMediaAsync();
        }
        catch
        {
            TrackTitle.Text = "Медиасессии недоступны";
            TrackArtist.Text = "Плеер не поддерживает управление Windows";
        }
    }

    private void SetMediaState(bool active, bool playing)
    {
        var icon = (Geometry)FindResource(playing ? "PauseGeo" : "PlayGeo");
        PlayIcon.Data = icon;
        CompactPlayIcon.Data = icon;
        _playing = playing;

        CompactPlayButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        EqPanel.Visibility = active && S.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
        CompactArtBox.Visibility = active && CompactArt.Source is not null ? Visibility.Visible : Visibility.Collapsed;

        if (active != _mediaActive)
        {
            _mediaActive = active;
            SetMediaWide(active);
        }

        if (playing && S.ShowEqualizer)
        {
            _spectrum ??= new AudioSpectrum();
            if (!_spectrum.IsRunning) _spectrum.Start();
            if (!_beatTimer.IsEnabled) _beatTimer.Start();
        }
        else
        {
            _beatTimer.Stop();
            _spectrum?.Stop();
            Array.Clear(_barLevels);
            foreach (var bar in _bars) bar.ScaleY = 0.2;
        }

        UpdateTitleDisplay();
    }

    /// <summary>Полоски двигаются по спектру реального звука: бас слева, верха справа.</summary>
    private void OnBeatTick()
    {
        if (!_playing || _bars.Length == 0 || CompactPanel.Visibility != Visibility.Visible) return;

        var spectrumOn = _spectrum is { IsRunning: true };
        var live = spectrumOn && Environment.TickCount64 - _spectrum!.LastDataTick < 150;
        if (spectrumOn) _spectrum!.GetLevels(_spectrumBuffer);

        var fallback = 0.0;
        if (!spectrumOn)
        {
            try { fallback = Math.Sqrt(Math.Clamp(_audioDevice?.AudioMeterInformation.MasterPeakValue ?? 0.0, 0.0, 1.0)); }
            catch { _audioDevice = null; }
        }

        var t = _beatClock.Elapsed.TotalSeconds;
        for (var i = 0; i < _bars.Length; i++)
        {
            double target;
            if (spectrumOn)
                target = live ? _spectrumBuffer[i] : 0.0;
            else
                target = Math.Clamp(fallback * (0.65 + 0.35 * Math.Sin(t * (6.0 + i * 1.7) + i * 1.3)) * 1.6, 0.0, 1.0);

            var current = _barLevels[i];
            current += (target - current) * (target > current ? 0.55 : 0.16);
            _barLevels[i] = current;
            _bars[i].ScaleY = 0.2 + 0.8 * current;
        }
    }

    /// <summary>Основной цвет обложки: яркий и насыщенный, чтобы хорошо читался на чёрном.</summary>
    private static Color? DominantColor(BitmapSource? source)
    {
        if (source is null) return null;
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            double red = 0, green = 0, blue = 0, total = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 16)
            {
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max < 40) continue;
                var saturation = (max - min) / max;
                var weight = saturation * saturation + 0.02;
                red += r * weight;
                green += g * weight;
                blue += b * weight;
                total += weight;
            }
            if (total <= 0) return null;

            red /= total;
            green /= total;
            blue /= total;
            var brightest = Math.Max(red, Math.Max(green, blue));
            if (brightest < 1) return null;
            var scale = 235.0 / brightest;
            return Color.FromRgb(
                (byte)Math.Min(255, red * scale),
                (byte)Math.Min(255, green * scale),
                (byte)Math.Min(255, blue * scale));
        }
        catch { return null; }
    }

    private void SetNoMedia()
    {
        TrackTitle.Text = "Ничего не играет";
        TrackArtist.Text = "Откройте плеер";
        SetMediaState(false, false);
        ProgressFill.Width = 0;
        ElapsedText.Text = "0:00";
        RemainingText.Text = "";
        CompactArtBox.Visibility = Visibility.Collapsed;
        if (_trackKey != "")
        {
            _trackKey = "";
            ArtImage.Source = null;
            CompactArt.Source = null;
            _artColor = null;
            ApplyEqColor();
        }
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaManager is null || _refreshingMedia) return;
        _refreshingMedia = true;
        try
        {
            var session = _mediaManager.GetCurrentSession();
            if (session is null)
            {
                SetNoMedia();
                return;
            }

            var info = await session.TryGetMediaPropertiesAsync();
            var title = string.IsNullOrWhiteSpace(info.Title) ? "Неизвестная композиция" : info.Title;
            var artist = string.IsNullOrWhiteSpace(info.Artist) ? session.SourceAppUserModelId : info.Artist;
            TrackTitle.Text = title;
            TrackArtist.Text = artist;
            CompactTitle.Text = title;

            var key = $"{title}|{artist}";
            if (key != _trackKey)
            {
                _trackKey = key;
                var art = await LoadArtAsync(info);
                ArtImage.Source = art;
                CompactArt.Source = art;
                _artColor = DominantColor(art);
                ApplyEqColor();

                if (S.TrackChangePopup)
                {
                    _titleUntil = DateTime.UtcNow.AddSeconds(3.5);
                    _titleTimer.Stop();
                    _titleTimer.Start();
                }
            }

            var status = session.GetPlaybackInfo().PlaybackStatus;
            var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var active = playing || status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            SetMediaState(active, playing);
            UpdateProgress();
        }
        catch { }
        finally { _refreshingMedia = false; }
    }

    private void UpdateProgress()
    {
        try
        {
            var session = _mediaManager?.GetCurrentSession();
            if (session is null)
            {
                ProgressFill.Width = 0;
                return;
            }

            var timeline = session.GetTimelineProperties();
            var total = (timeline.EndTime - timeline.StartTime).TotalSeconds;
            var position = (timeline.Position - timeline.StartTime).TotalSeconds;
            var playing = session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing) position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;

            if (total < 1)
            {
                ProgressFill.Width = 0;
                ElapsedText.Text = "";
                RemainingText.Text = "";
                return;
            }

            position = Math.Clamp(position, 0, total);
            ProgressFill.Width = ProgressTrack.ActualWidth * position / total;
            ElapsedText.Text = FormatTime(position);
            RemainingText.Text = "-" + FormatTime(total - position);
        }
        catch { }
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static async Task<BitmapImage?> LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties info)
    {
        try
        {
            if (info.Thumbnail is null) return null;
            using var randomAccess = await info.Thumbnail.OpenReadAsync();
            using var stream = randomAccess.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 128;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _mediaManager?.GetCurrentSession();
            if (session is not null) await session.TryTogglePlayPauseAsync();
            await Task.Delay(150);
            await RefreshMediaAsync();
        }
        catch { }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _mediaManager?.GetCurrentSession();
            if (session is not null) await session.TrySkipPreviousAsync();
            await Task.Delay(400);
            await RefreshMediaAsync();
        }
        catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _mediaManager?.GetCurrentSession();
            if (session is not null) await session.TrySkipNextAsync();
            await Task.Delay(400);
            await RefreshMediaAsync();
        }
        catch { }
    }

    // ---------- Громкость ----------

    private void RefreshVolume()
    {
        if (VolumeSlider.IsMouseCaptureWithin) return;
        try
        {
            // Устройство вывода перечисляем не чаще раза в 10 секунд: это медленный вызов.
            if (_audioDevice is null || (DateTime.UtcNow - _deviceStamp).TotalSeconds > 10)
            {
                _audioDevice?.Dispose();
                _audioDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceStamp = DateTime.UtcNow;
            }
            _volumeUpdateFromCode = true;
            VolumeSlider.Value = Math.Clamp(_audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100, 0, 100);
            _volumeUpdateFromCode = false;
        }
        catch { _volumeUpdateFromCode = false; }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeUpdateFromCode || _audioDevice is null) return;
        try { _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(VolumeSlider.Value / 100.0); }
        catch { }
    }

    // ---------- Меню и завершение ----------

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowSettings();

    private void Settings_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowSettings();

    private void UpdateApp_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowSettings("update");

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        AppSettings.Changed -= ApplySettings;
        StopRendering();
        _clockTimer.Stop();
        _clipboardTimer.Stop();
        _mediaTimer.Stop();
        _collapseTimer.Stop();
        _expandTimer.Stop();
        _statusTimer.Stop();
        _beatTimer.Stop();
        _titleTimer.Stop();
        _statsTimer.Stop();
        _fullscreenTimer.Stop();
        _spectrum?.Dispose();
        _audioDevice?.Dispose();
        _enumerator.Dispose();
        base.OnClosed(e);
    }
}
