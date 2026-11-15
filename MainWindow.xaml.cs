using System.IO;
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
    // Размеры капсулы и раскрытой панели (окно всегда одного размера — это убирает лаги).
    private const double CompactW = 200, CompactH = 34;
    private const double ExpandedW = 400, ExpandedH = 560;
    private const double CompactRadius = 17, ExpandedRadius = 32;

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
    private bool _eqOn;

    // Пружинная анимация: _p = 0 (капсула) ... 1 (раскрыто).
    private double _p;
    private double _v;
    private double _target;
    private bool _rendering;
    private TimeSpan _lastRender = TimeSpan.Zero;

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _audioDevice;

    public MainWindow()
    {
        InitializeComponent();
        ClipboardItems.ItemsSource = _history;
        _history.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
        ApplyProgress();

        _collapseTimer.Tick += (_, _) => OnCollapseTimer();
        _expandTimer.Tick += (_, _) => OnExpandTimer();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = ""; };

        Loaded += async (_, _) =>
        {
            PositionWindow();
            UpdateClock();
            _clockTimer.Tick += (_, _) => OnClockTick();
            _clockTimer.Start();

            _clipboardTimer.Tick += (_, _) => ReadClipboard();
            _clipboardTimer.Start();

            RefreshVolume();

            await InitializeMediaAsync();
            _mediaTimer.Tick += async (_, _) => await RefreshMediaAsync();
            _mediaTimer.Start();
        };

        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
    }

    // ---------- Положение окна ----------

    private void PositionWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    // ---------- Наведение мыши ----------

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _expandTimer.Stop();
        _expandTimer.Start();
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
        Collapse();
    }

    private void Expand()
    {
        _collapseTimer.Stop();
        if (_expanded) return;
        _expanded = true;

        UpdateClock();
        RefreshVolume();
        _ = RefreshMediaAsync();
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
        if (_rendering) return;
        _rendering = true;
        _lastRender = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
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

        // Лёгкий «пружинный» отскок: коэффициент затухания около 0.8.
        const double stiffness = 380.0;
        const double damping = 31.0;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240.0)));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var acceleration = -stiffness * (_p - _target) - damping * _v;
            _v += acceleration * h;
            _p += _v * h;
        }

        if (Math.Abs(_p - _target) < 0.0008 && Math.Abs(_v) < 0.01)
        {
            _p = _target;
            _v = 0;
            ApplyProgress();
            StopRendering();
            return;
        }

        ApplyProgress();
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    private void ApplyProgress()
    {
        var p = _p;
        var g = Math.Max(0.0, p);

        RootBorder.Width = Lerp(CompactW, ExpandedW, g);
        RootBorder.Height = Lerp(CompactH, ExpandedH, g);
        RootBorder.CornerRadius = new CornerRadius(Lerp(CompactRadius, ExpandedRadius, Clamp01(g)));

        var expandedOpacity = Clamp01((p - 0.30) / 0.45);
        var compactOpacity = Clamp01(1.0 - p / 0.22);

        ExpandedPanel.Opacity = expandedOpacity;
        CompactPanel.Opacity = compactOpacity;

        var expandedVisibility = expandedOpacity > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        if (ExpandedPanel.Visibility != expandedVisibility) ExpandedPanel.Visibility = expandedVisibility;

        var compactVisibility = compactOpacity > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        if (CompactPanel.Visibility != compactVisibility) CompactPanel.Visibility = compactVisibility;

        PanelShift.Y = (1.0 - Clamp01(p)) * -14.0;
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
        var time = now.ToString("HH:mm", ru);
        ClockText.Text = time;
        ClockBig.Text = time;
        var date = now.ToString("dddd, d MMMM", ru);
        DateText.Text = char.ToUpper(date[0], ru) + date[1..];
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
            if (LooksSensitive(value)) return;

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
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var value))
        {
            _limit = value;
            TrimHistory();
        }
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        _paused = RecordSwitch.IsChecked != true;
        ShowStatus(_paused ? "Запись выключена" : "Запись включена");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
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

    private void SetPlayingUi(bool playing)
    {
        PlayIcon.Data = (Geometry)FindResource(playing ? "PauseGeo" : "PlayGeo");
        SetEqualizer(playing);
    }

    private void SetNoMedia()
    {
        TrackTitle.Text = "Ничего не играет";
        TrackArtist.Text = "Запустите музыку в Spotify или другом плеере";
        SetPlayingUi(false);
        ProgressFill.Width = 0;
        ElapsedText.Text = "0:00";
        RemainingText.Text = "";
        CompactArtBox.Visibility = Visibility.Collapsed;
        if (_trackKey != "")
        {
            _trackKey = "";
            ArtImage.Source = null;
            CompactArt.Source = null;
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

            var key = $"{title}|{artist}";
            if (key != _trackKey)
            {
                _trackKey = key;
                var art = await LoadArtAsync(info);
                ArtImage.Source = art;
                CompactArt.Source = art;
                CompactArtBox.Visibility = art is null ? Visibility.Collapsed : Visibility.Visible;
            }

            var playing = session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            SetPlayingUi(playing);
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

    private void SetEqualizer(bool on)
    {
        if (on == _eqOn) return;
        _eqOn = on;
        EqPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        var bars = new[] { EqScale1, EqScale2, EqScale3, EqScale4 };
        var durations = new[] { 420, 330, 500, 380 };
        for (var i = 0; i < bars.Length; i++)
        {
            if (on)
            {
                var animation = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
            else
            {
                bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                bars[i].ScaleY = 0.3;
            }
        }
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
            _audioDevice?.Dispose();
            _audioDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        StopRendering();
        _clockTimer.Stop();
        _clipboardTimer.Stop();
        _mediaTimer.Stop();
        _collapseTimer.Stop();
        _expandTimer.Stop();
        _statusTimer.Stop();
        _audioDevice?.Dispose();
        _enumerator.Dispose();
        base.OnClosed(e);
    }
}
