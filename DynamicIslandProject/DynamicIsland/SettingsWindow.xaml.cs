using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland;

public partial class SettingsWindow : Window
{
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2D));
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush DashBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66));

    private readonly Dictionary<string, StackPanel> _pages;
    private bool _loading;
    private bool _navUpdating;
    private bool _tokenDirty;
    private bool _loadingToken;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private List<UpdatePackage.DroppedFile> _pending = new();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public SettingsWindow()
    {
        InitializeComponent();

        VersionText.Text = "Версия " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—");

        _pages = new Dictionary<string, StackPanel>
        {
            ["appearance"] = PageAppearance,
            ["shape"] = PageShape,
            ["animation"] = PageAnimation,
            ["features"] = PageFeatures,
            ["clipboard"] = PageClipboard,
            ["update"] = PageUpdate,
            ["about"] = PageAbout
        };

        BuildNav();
        RebuildPages();
        LoadUpdateSettings();
        ShowPage("appearance");
    }

    // ================= Навигация =================

    private void BuildNav()
    {
        var items = new (string Key, string Title)[]
        {
            ("appearance", "Внешний вид"),
            ("shape", "Форма и положение"),
            ("animation", "Анимация"),
            ("features", "Функции"),
            ("clipboard", "Буфер обмена"),
            ("update", "Обновление"),
            ("about", "О программе")
        };

        foreach (var (key, title) in items)
        {
            var button = new RadioButton
            {
                Content = title,
                Tag = key,
                GroupName = "Nav",
                Style = (Style)FindResource("SidebarButton")
            };
            button.Checked += (_, _) =>
            {
                if (!_navUpdating) ShowPage(key);
            };
            NavPanel.Children.Add(button);
        }
    }

    public void ShowPage(string key)
    {
        if (!_pages.ContainsKey(key)) return;

        foreach (var (name, panel) in _pages)
            panel.Visibility = name == key ? Visibility.Visible : Visibility.Collapsed;

        _navUpdating = true;
        foreach (var radio in NavPanel.Children.OfType<RadioButton>())
            radio.IsChecked = (string?)radio.Tag == key;
        _navUpdating = false;
    }

    // ================= Построение страниц настроек =================

    private void RebuildPages()
    {
        _loading = true;
        foreach (var key in new[] { "appearance", "shape", "animation", "features", "clipboard", "about" })
            _pages[key].Children.Clear();

        BuildAppearance(PageAppearance);
        BuildShape(PageShape);
        BuildAnimation(PageAnimation);
        BuildFeatures(PageFeatures);
        BuildClipboard(PageClipboard);
        BuildAbout(PageAbout);
        _loading = false;
    }

    private static AppSettings S => AppSettings.Current;

    private static void Commit() => AppSettings.NotifyChanged();

    private void BuildAppearance(Panel page)
    {
        AddTitle(page, "Внешний вид", "Все изменения сразу видны на острове в верхней части экрана.");
        AddColor(page, "Цвет фона острова",
            new[] { "#050507", "#000000", "#1C1C1E", "#1B2A41", "#2B1B3D", "#14301F", "#3A1C1C" },
            () => S.BackgroundColor, v => S.BackgroundColor = v);
        AddSlider(page, "Непрозрачность фона", 50, 100, () => S.BackgroundOpacity * 100,
            v => S.BackgroundOpacity = v / 100, "%");
        AddColor(page, "Цвет акцента",
            new[] { "#0A84FF", "#30D158", "#FF375F", "#FF9F0A", "#BF5AF2", "#64D2FF", "#FFD60A" },
            () => S.AccentColor, v => S.AccentColor = v);
        AddSwitch(page, "Эквалайзер в капсуле", "Анимированные полоски, пока играет музыка",
            () => S.ShowEqualizer, v => S.ShowEqualizer = v);
    }

    private void BuildShape(Panel page)
    {
        AddTitle(page, "Форма и положение", "Размеры и скругления капсулы и раскрытой панели.");
        AddSegmented(page, "Стиль",
            new[] { "Капсула", "Выступ у края", "Прямоугольник" },
            () => S.FlatTop ? 1 : (S.CompactRadius < 12 ? 2 : 0),
            index =>
            {
                switch (index)
                {
                    case 0:
                        S.FlatTop = false;
                        S.CompactRadius = Math.Round(S.CompactHeight / 2);
                        S.ExpandedRadius = 32;
                        S.TopOffset = 8;
                        break;
                    case 1:
                        S.FlatTop = true;
                        S.CompactRadius = 16;
                        S.ExpandedRadius = 30;
                        S.TopOffset = 0;
                        break;
                    default:
                        S.FlatTop = false;
                        S.CompactRadius = 8;
                        S.ExpandedRadius = 18;
                        S.TopOffset = 8;
                        break;
                }
            },
            refreshPage: true);
        AddSlider(page, "Ширина капсулы", 120, 320, () => S.CompactWidth, v => S.CompactWidth = v, " px");
        AddSlider(page, "Высота капсулы", 26, 56, () => S.CompactHeight, v => S.CompactHeight = v, " px");
        AddSlider(page, "Скругление капсулы", 0, 28, () => S.CompactRadius, v => S.CompactRadius = v, " px");
        AddSlider(page, "Скругление раскрытой панели", 8, 44, () => S.ExpandedRadius, v => S.ExpandedRadius = v, " px");
        AddSlider(page, "Отступ сверху", 0, 60, () => S.TopOffset, v => S.TopOffset = v, " px");
        AddSwitch(page, "Прилегает к верхнему краю", "Верхние углы без скругления, как «чёлка»",
            () => S.FlatTop, v => S.FlatTop = v);
    }

    private void BuildAnimation(Panel page)
    {
        AddTitle(page, "Анимация", "Остров раскрывается пружиной, как в macOS.");
        AddSwitch(page, "Плавная анимация", "Если выключить, остров будет раскрываться мгновенно",
            () => S.AnimationsEnabled, v => S.AnimationsEnabled = v);
        AddSlider(page, "Скорость", 150, 800, () => S.AnimationStiffness, v => S.AnimationStiffness = v, "");
        AddSlider(page, "Отскок", 0, 50, () => (1 - S.AnimationDamping) * 100, v => S.AnimationDamping = 1 - v / 100, " %");
    }

    private void BuildFeatures(Panel page)
    {
        AddTitle(page, "Функции", "Что показывать на острове и как он себя ведёт.");
        AddSegmented(page, "Раскрытие острова", new[] { "При наведении", "По клику" },
            () => S.ExpandOnHover ? 0 : 1, index => S.ExpandOnHover = index == 0);
        AddSlider(page, "Задержка раскрытия", 0, 600, () => S.ExpandDelayMs, v => S.ExpandDelayMs = (int)v, " мс");
        AddSlider(page, "Задержка сворачивания", 100, 1500, () => S.CollapseDelayMs, v => S.CollapseDelayMs = (int)v, " мс");
        AddSwitch(page, "Плеер", "Музыка из Spotify и других плееров",
            () => S.ShowPlayer, v => S.ShowPlayer = v);
        AddSwitch(page, "Громкость", "Регулятор громкости Windows",
            () => S.ShowVolume, v => S.ShowVolume = v);
        AddSwitch(page, "Буфер обмена", "История скопированного текста",
            () => S.ShowClipboard, v => S.ShowClipboard = v);
        AddSegmented(page, "Формат времени", new[] { "24 часа", "12 часов" },
            () => S.Use24Hour ? 0 : 1, index => S.Use24Hour = index == 0);
        AddSwitch(page, "Показывать секунды", null, () => S.ShowSeconds, v => S.ShowSeconds = v);
        AddSwitch(page, "Запускать вместе с Windows", null, AutoStart.IsEnabled, AutoStart.Set);
    }

    private void BuildClipboard(Panel page)
    {
        var limits = new[] { 5, 10, 20, 50 };
        AddTitle(page, "Буфер обмена", "История скопированного текста хранится только в памяти и пропадает при закрытии.");
        AddSegmented(page, "Сколько записей хранить", limits.Select(x => x.ToString()).ToArray(),
            () => Math.Max(0, Array.IndexOf(limits, S.ClipboardLimit)),
            index => S.ClipboardLimit = limits[index]);
        AddSwitch(page, "Записывать буфер обмена", null,
            () => S.ClipboardRecording, v => S.ClipboardRecording = v);
        AddSwitch(page, "Не сохранять пароли и номера карт", "Фильтр по ключевым словам и номерам банковских карт",
            () => S.ClipboardFilterSensitive, v => S.ClipboardFilterSensitive = v);
        AddButton(page, "Очистить историю", "Закреплённые записи останутся", "Очистить", () =>
        {
            (Application.Current.MainWindow as MainWindow)?.ClearClipboardHistory();
        });
    }

    private void BuildAbout(Panel page)
    {
        AddTitle(page, "О программе", "Dynamic Island для Windows 11 — остров с музыкой, громкостью и буфером обмена.");
        AddButton(page, "Папка с настройками", AppSettings.SettingsDirectory, "Открыть", () =>
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.SettingsDirectory}\"") { UseShellExecute = true });
        });
        AddButton(page, "Сбросить внешний вид и поведение", "Подключение к GitHub сохранится", "Сбросить", () =>
        {
            var answer = MessageBox.Show(this, "Вернуть все настройки острова к значениям по умолчанию?",
                "Dynamic Island", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            AppSettings.ResetAppearance();
            Dispatcher.BeginInvoke(new Action(RebuildPages));
        });
        AddButton(page, "Закрыть Dynamic Island", "Остров исчезнет, пока вы не запустите программу снова", "Закрыть", () =>
        {
            Application.Current.Shutdown();
        });
    }

    // ================= Элементы страниц =================

    private static void AddTitle(Panel page, string title, string subtitle)
    {
        page.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 18)
        });
    }

    private static Border MakeRow(string title, string? subtitle, UIElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(subtitle))
        {
            texts.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Gray,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        grid.Children.Add(texts);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private void AddSwitch(Panel page, string title, string? subtitle, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("MacSwitch"),
            IsChecked = get(),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Click += (_, _) =>
        {
            set(toggle.IsChecked == true);
            Commit();
        };
        page.Children.Add(MakeRow(title, subtitle, toggle));
    }

    private void AddSlider(Panel page, string title, double min, double max, Func<double> get, Action<double> set,
        string suffix)
    {
        var slider = new Slider
        {
            Style = (Style)FindResource("MacSlider"),
            Minimum = min,
            Maximum = max,
            Width = 230,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.Value = Math.Clamp(get(), min, max);

        var label = new TextBlock
        {
            Foreground = Gray,
            Width = 64,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        void UpdateLabel() => label.Text = Math.Round(slider.Value).ToString(CultureInfo.InvariantCulture) + suffix;
        UpdateLabel();

        slider.ValueChanged += (_, _) =>
        {
            UpdateLabel();
            if (_loading) return;
            set(slider.Value);
            Commit();
        };

        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(slider);
        host.Children.Add(label);
        page.Children.Add(MakeRow(title, null, host));
    }

    private void AddSegmented(Panel page, string title, string[] labels, Func<int> get, Action<int> set,
        bool refreshPage = false)
    {
        var group = Guid.NewGuid().ToString();
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var current = get();

        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var radio = new RadioButton
            {
                Content = labels[i],
                GroupName = group,
                Style = (Style)FindResource("SegmentButton"),
                IsChecked = i == current
            };
            radio.Checked += (_, _) =>
            {
                if (_loading) return;
                set(index);
                Commit();
                if (refreshPage) Dispatcher.BeginInvoke(new Action(RebuildPages));
            };
            stack.Children.Add(radio);
        }

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack
        };
        page.Children.Add(MakeRow(title, null, host));
    }

    private void AddColor(Panel page, string title, string[] presets, Func<string> get, Action<string> set)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox
        {
            Style = (Style)FindResource("DarkTextBox"),
            Width = 96,
            Margin = new Thickness(10, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            Text = get()
        };

        var swatches = new List<(Border Swatch, string Color)>();

        void Highlight()
        {
            var current = box.Text.Trim().TrimStart('#').ToUpperInvariant();
            foreach (var (swatch, color) in swatches)
            {
                swatch.BorderBrush = color.TrimStart('#').ToUpperInvariant() == current ? Brushes.White : Brushes.Transparent;
            }
        }

        foreach (var color in presets)
        {
            AppSettings.TryParseColor(color, out var parsed);
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(parsed),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = color
            };
            var captured = color;
            swatch.MouseLeftButtonUp += (_, _) => box.Text = captured;
            swatches.Add((swatch, color));
            host.Children.Add(swatch);
        }

        box.TextChanged += (_, _) =>
        {
            Highlight();
            if (_loading) return;
            if (!AppSettings.TryParseColor(box.Text, out _)) return;
            var text = box.Text.Trim();
            set(text.StartsWith('#') ? text : "#" + text);
            Commit();
        };

        host.Children.Add(box);
        Highlight();
        page.Children.Add(MakeRow(title, null, host));
    }

    private void AddButton(Panel page, string title, string? subtitle, string buttonText, Action click)
    {
        var button = new Button { Style = (Style)FindResource("PillButton"), Content = buttonText, Padding = new Thickness(16, 7) };
        button.Click += (_, _) => click();
        page.Children.Add(MakeRow(title, subtitle, button));
    }

    // ================= Обновление: подключение =================

    private void LoadUpdateSettings()
    {
        var settings = AppSettings.Current;
        if (settings.GitHubOwner.Length > 0 && settings.GitHubRepo.Length > 0)
            RepoBox.Text = $"https://github.com/{settings.GitHubOwner}/{settings.GitHubRepo}";

        if (settings.GetToken().Length > 0)
        {
            _loadingToken = true;
            TokenBox.Password = new string('•', 24);
            _loadingToken = false;
            SetConnectStatus("Подключение сохранено. Можно перетаскивать файлы обновления.", true);
        }

        TokenHelp.Text =
            "1. Откройте github.com → ваш аватар → Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token.\n" +
            "2. В разделе Repository access выберите Only select repositories и укажите ваш репозиторий.\n" +
            "3. В разделе Permissions → Repository permissions поставьте Contents: Read and write и Actions: Read and write.\n" +
            "4. Нажмите Generate token, скопируйте токен (он начинается с github_pat_) и вставьте его выше.\n" +
            "Никому не отправляйте токен и не вставляйте его в чаты.";
    }

    private void SetConnectStatus(string text, bool? ok)
    {
        ConnectStatus.Text = text;
        ConnectStatus.Foreground = ok is null ? Gray : ok.Value ? GreenBrush : RedBrush;
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingToken) _tokenDirty = true;
    }

    private void TokenHelpButton_Click(object sender, RoutedEventArgs e) =>
        TokenHelp.Visibility = TokenHelp.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!GitHubUpdater.TryParseRepository(RepoBox.Text, out var owner, out var repo))
        {
            SetConnectStatus("Не удалось разобрать адрес. Пример: https://github.com/логин/репозиторий", false);
            return;
        }

        var settings = AppSettings.Current;
        if (_tokenDirty) settings.SetToken(TokenBox.Password);
        var token = settings.GetToken();
        if (token.Length == 0)
        {
            SetConnectStatus("Вставьте токен доступа.", false);
            return;
        }

        ConnectButton.IsEnabled = false;
        SetConnectStatus("Проверяю подключение…", null);
        try
        {
            using var updater = new GitHubUpdater(owner, repo, token);
            var branch = await updater.GetDefaultBranchAsync(CancellationToken.None);
            var tree = await updater.GetTreePathsAsync(branch, CancellationToken.None);
            var hasProject = tree.Any(p => p.EndsWith("DynamicIsland.csproj", StringComparison.OrdinalIgnoreCase));

            settings.GitHubOwner = owner;
            settings.GitHubRepo = repo;
            _tokenDirty = false;
            Commit();

            SetConnectStatus(hasProject
                ? $"Подключено: {owner}/{repo} (ветка {branch}) ✓"
                : "Подключились к репозиторию, но файл DynamicIsland.csproj в нём не найден. Загрузите проект.", hasProject);
        }
        catch (Exception ex)
        {
            SetConnectStatus("Ошибка: " + ex.Message, false);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    // ================= Обновление: файлы =================

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // тёмный заголовок окна
        }
        catch { }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropRect.Stroke = AccentBrush;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DropRect.Stroke = DashBrush;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropRect.Stroke = DashBrush;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            ShowPage("update");
            LoadDropped(paths);
        }
        e.Handled = true;
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Выберите файлы обновления",
            Filter = "Файлы обновления|*.zip;*.cs;*.xaml;*.csproj;*.iss;*.manifest|Все файлы|*.*"
        };
        if (dialog.ShowDialog(this) == true) LoadDropped(dialog.FileNames);
    }

    private void LoadDropped(string[] paths)
    {
        try
        {
            _pending = UpdatePackage.Read(paths);
        }
        catch (Exception ex)
        {
            _pending = new List<UpdatePackage.DroppedFile>();
            ShowUpdateError("Не удалось прочитать файлы: " + ex.Message);
        }

        if (_pending.Count == 0)
        {
            FilesText.Text = "Подходящих файлов не найдено. Перетащите ZIP-архив или файлы проекта.";
            UpdateButton.IsEnabled = false;
            return;
        }

        var lines = _pending.Take(14).Select(f => "•  " + f.Name).ToList();
        if (_pending.Count > 14) lines.Add($"… и ещё {_pending.Count - 14}");
        FilesText.Text = $"Готово к загрузке: {_pending.Count} файл(ов)\n" + string.Join("\n", lines);
        UpdateButton.IsEnabled = !_busy;
        StatusLabel.Text = "";
    }

    // ================= Обновление: запуск =================

    private void Log(string text)
    {
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void SetStatus(string text)
    {
        StatusLabel.Foreground = Brushes.White;
        StatusLabel.Text = text;
    }

    private void Step(string text)
    {
        SetStatus(text);
        Log(text);
    }

    private void ShowUpdateError(string message)
    {
        StatusLabel.Foreground = RedBrush;
        StatusLabel.Text = "Ошибка: " + message;
        Log("ОШИБКА: " + message);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy && _pending.Count > 0;
        InstallLatestButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync(true);

    private async void InstallLatestButton_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync(false);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(LogBox.Text) ? StatusLabel.Text : LogBox.Text);
            CopyLogButton.Content = "Скопировано ✓";
        }
        catch { CopyLogButton.Content = "Не удалось скопировать"; }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cts?.Cancel();

    /// <param name="uploadFiles">true — загрузить файлы, собрать и установить; false — только установить последнюю готовую сборку.</param>
    private async Task RunUpdateAsync(bool uploadFiles)
    {
        if (_busy) return;

        var settings = AppSettings.Current;
        var token = settings.GetToken();
        if (settings.GitHubOwner.Length == 0 || token.Length == 0)
        {
            ShowUpdateError("Сначала подключитесь к GitHub (блок «Подключение» выше).");
            return;
        }

        SetBusy(true);
        CopyLogButton.Content = "Копировать журнал";
        LogBox.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            using var updater = new GitHubUpdater(settings.GitHubOwner, settings.GitHubRepo, token);

            Step("Подключение к GitHub…");
            var branch = await updater.GetDefaultBranchAsync(ct);
            var workflowId = await updater.FindWorkflowAsync(ct);

            RunInfo? run;
            if (!uploadFiles)
            {
                Step("Ищу последнюю успешную сборку…");
                run = await updater.GetLatestSuccessfulRunAsync(workflowId, ct);
                if (run is null)
                    throw new GitHubException("Готовых сборок пока нет. Загрузите файлы и запустите обновление.");
            }
            else
            {
                if (_pending.Count == 0) throw new GitHubException("Сначала перетащите файлы обновления.");

                var baseline = await updater.GetLatestRunIdAsync(workflowId, ct);

                Step("Разбираю файлы…");
                var tree = await updater.GetTreePathsAsync(branch, ct);
                var files = UpdatePackage.MapToRepository(_pending, tree, out var skipped);
                foreach (var name in skipped) Log("Пропущено: " + name);
                if (files.Count == 0) throw new GitHubException("Ни один из файлов не подошёл к проекту.");
                foreach (var file in files) Log($"{file.Source}  →  {file.RepoPath}");

                Step("Загружаю файлы в репозиторий…");
                var changed = await updater.CommitFilesAsync(branch, files,
                    "Обновление из приложения Dynamic Island", Log, ct);
                Log(changed ? "Файлы загружены." : "Файлы совпадают с репозиторием — изменений нет.");

                run = null;
                if (changed)
                {
                    Step("Жду запуска сборки…");
                    run = await updater.WaitForNewRunAsync(workflowId, baseline, 25, ct);
                }

                if (run is null)
                {
                    Step("Запускаю сборку…");
                    await updater.DispatchAsync(workflowId, branch, ct);
                    run = await updater.WaitForNewRunAsync(workflowId, baseline, 120, ct)
                          ?? throw new GitHubException("GitHub не запустил сборку. Проверьте вкладку Actions в репозитории.");
                }

                Log("Сборка: " + run.Url);
                run = await updater.WaitForCompletionAsync(run.Id, SetStatus, ct);

                if (run.Conclusion != "success")
                {
                    var summary = await updater.GetFailureSummaryAsync(run.Id, ct);
                    StatusLabel.Foreground = RedBrush;
                    StatusLabel.Text = "Сборка не удалась. Нажмите «Копировать журнал» и отправьте его в чат — я исправлю.";
                    Log("СБОРКА НЕ УДАЛАСЬ: " + run.Url);
                    Log(summary);
                    return;
                }
                Log("Сборка завершена успешно.");
            }

            Step("Скачиваю установщик…");
            var directory = Path.Combine(Path.GetTempPath(), "DynamicIslandUpdate");
            var setup = await updater.DownloadInstallerAsync(run.Id, directory, ct);

            Step("Устанавливаю. Программа закроется и сама запустится снова…");
            Process.Start(new ProcessStartInfo(setup, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART")
            {
                UseShellExecute = true
            });
            await Task.Delay(1200, CancellationToken.None);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Отменено");
            Log("Отменено.");
        }
        catch (Exception ex)
        {
            ShowUpdateError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
