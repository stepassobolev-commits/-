using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace DynamicIsland;

public sealed record ClipboardEntry(string Text, DateTime CopiedAt)
{
    public string DisplayText => $"{CopiedAt:HH:mm:ss}  {Text}";
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ClipboardEntry> _clipboardHistory = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastClipboard = "";
    private bool _volumeUpdateFromCode;
    private bool _expanded;
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private MMDevice? _audioDevice;

    public MainWindow()
    {
        InitializeComponent();
        ClipboardItems.ItemsSource = _clipboardHistory;

        Loaded += async (_, _) =>
        {
            PositionWindow();
            UpdateClock();
            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();

            _clipboardTimer.Tick += (_, _) => ReadClipboard();
            _clipboardTimer.Start();

            try
            {
                _audioDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                SetVolumeUi(_audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            }
            catch { VolumeText.Text = "н/д"; }

            await InitializeMediaAsync();
            _mediaTimer.Tick += async (_, _) => await RefreshMediaAsync();
            _mediaTimer.Start();
        };

        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e) => Expand();
    private void Root_MouseLeave(object sender, MouseEventArgs e) => Collapse();

    private void PositionWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 8;
    }

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("ddd, dd MMM", CultureInfo.GetCultureInfo("ru-RU"));
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        ExpandedPanel.Visibility = Visibility.Visible;
        CompactPanel.Visibility = Visibility.Collapsed;
        Height = 310;
        Width = 450;
        RootBorder.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(430, TimeSpan.FromMilliseconds(220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        RootBorder.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(300, TimeSpan.FromMilliseconds(220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - 430) / 2;
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        RootBorder.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(190, TimeSpan.FromMilliseconds(180)));
        RootBorder.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(54, TimeSpan.FromMilliseconds(180)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_expanded) return;
            ExpandedPanel.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible;
            Width = 190;
            Height = 54;
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - 190) / 2;
        };
        timer.Start();
    }

    private void ReadClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var value = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(value) || value == _lastClipboard) return;
            _lastClipboard = value;

            // Avoid collecting text that clearly looks like credentials or payment data.
            if (LooksSensitive(value)) return;

            if (value.Length > 500) value = value[..500] + "…";
            _clipboardHistory.Insert(0, new ClipboardEntry(value, DateTime.Now));
            while (_clipboardHistory.Count > 5) _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);
        }
        catch { /* Clipboard may be temporarily locked by another application. */ }
    }

    private void ClipboardItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntry entry })
        {
            try { Clipboard.SetText(entry.Text); }
            catch { }
        }
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

        // Skip likely payment-card numbers (13–19 digits, allowing spaces/dashes).
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
            TrackArtist.Text = "Проверь поддержку приложения";
        }
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaManager is null) return;
        try
        {
            var session = _mediaManager.GetCurrentSession();
            if (session is null)
            {
                TrackTitle.Text = "Нет активного воспроизведения";
                TrackArtist.Text = "Открой Spotify или другой плеер";
                return;
            }

            var info = await session.TryGetMediaPropertiesAsync();
            TrackTitle.Text = string.IsNullOrWhiteSpace(info.Title) ? "Неизвестная композиция" : info.Title;
            TrackArtist.Text = string.IsNullOrWhiteSpace(info.Artist) ? session.SourceAppUserModelId : info.Artist;
            var playback = session.GetPlaybackInfo();
            PlayPauseButton.Content = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "Ⅱ" : "▶";
        }
        catch { }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _mediaManager?.GetCurrentSession();
            if (session is not null) await session.TryTogglePlayPauseAsync();
            await RefreshMediaAsync();
        }
        catch { }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        try { var s = _mediaManager?.GetCurrentSession(); if (s is not null) await s.TrySkipPreviousAsync(); }
        catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        try { var s = _mediaManager?.GetCurrentSession(); if (s is not null) await s.TrySkipNextAsync(); }
        catch { }
    }

    private void SetVolumeUi(double value)
    {
        _volumeUpdateFromCode = true;
        VolumeSlider.Value = Math.Clamp(value, 0, 100);
        VolumeText.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
        _volumeUpdateFromCode = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is null) return;
        VolumeText.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
        if (_volumeUpdateFromCode || _audioDevice is null) return;
        try { _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(VolumeSlider.Value / 100); }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _clipboardTimer.Stop();
        _mediaTimer.Stop();
        _audioDevice?.Dispose();
        base.OnClosed(e);
    }
}
