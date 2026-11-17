using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>Все настройки острова. Хранятся в %AppData%\DynamicIsland\settings.json.</summary>
public sealed class AppSettings
{
    // ---------- Внешний вид ----------
    public string BackgroundColor { get; set; } = "#050507";
    public double BackgroundOpacity { get; set; } = 0.98;
    public string AccentColor { get; set; } = "#0A84FF";

    // ---------- Форма и положение ----------
    public double CompactWidth { get; set; } = 200;
    public double CompactHeight { get; set; } = 34;
    public double CompactRadius { get; set; } = 17;
    public double ExpandedRadius { get; set; } = 32;
    public bool FlatTop { get; set; } = false;
    public double TopOffset { get; set; } = 8;
    public double UiScale { get; set; } = 0.85;
    public double MediaExtraWidth { get; set; } = 64;
    public bool SmoothMode { get; set; } = false;

    // ---------- Музыка ----------
    public bool EqFromArtwork { get; set; } = true;

    // ---------- Дополнения (по умолчанию выключены) ----------
    public bool ShowSystemStats { get; set; } = false;
    public bool ShowTrackTitle { get; set; } = false;
    public bool TrackChangePopup { get; set; } = false;
    public bool HideInFullscreen { get; set; } = false;
    public double TitleExtraWidth { get; set; } = 120;

    // ---------- Раскладка ----------
    public string BlockOrder { get; set; } = "player,volume,clipboard,stats";

    // ---------- Анимация ----------
    public bool AnimationsEnabled { get; set; } = true;
    public double AnimationStiffness { get; set; } = 380;
    public double AnimationDamping { get; set; } = 0.8; // 1.0 — без отскока

    // ---------- Поведение ----------
    public bool ExpandOnHover { get; set; } = true;
    public int ExpandDelayMs { get; set; } = 140;
    public int CollapseDelayMs { get; set; } = 300;

    // ---------- Блоки ----------
    public bool ShowPlayer { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowEqualizer { get; set; } = true;

    // ---------- Часы ----------
    public bool Use24Hour { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;

    // ---------- Буфер обмена ----------
    public int ClipboardLimit { get; set; } = 5;
    public bool ClipboardRecording { get; set; } = true;
    public bool ClipboardFilterSensitive { get; set; } = true;

    // ---------- Обновление через GitHub ----------
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string GitHubTokenProtected { get; set; } = "";

    // =====================================================================

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static DispatcherTimer? _saveTimer;

    public static AppSettings Current { get; private set; } = Load();

    public static event Action? Changed;

    /// <summary>Порядок блоков острова сверху вниз (всегда содержит все четыре блока).</summary>
    public string[] GetBlockOrder()
    {
        var known = new[] { "player", "volume", "clipboard", "stats" };
        var result = new List<string>();
        foreach (var part in (BlockOrder ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (known.Contains(part) && !result.Contains(part)) result.Add(part);
        }
        foreach (var key in known)
        {
            if (!result.Contains(key)) result.Add(key);
        }
        return result.ToArray();
    }

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* Повреждённый файл — начинаем с настроек по умолчанию. */ }
        return new AppSettings();
    }

    /// <summary>Сообщить окнам, что настройки изменились, и сохранить их на диск (с небольшой задержкой).</summary>
    public static void NotifyChanged()
    {
        Changed?.Invoke();
        ScheduleSave();
    }

    private static void ScheduleSave()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            SaveNow();
            return;
        }

        _saveTimer ??= CreateSaveTimer(dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static DispatcherTimer CreateSaveTimer(Dispatcher dispatcher)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveNow();
        };
        return timer;
    }

    public static void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch { /* Не удалось сохранить — настройки останутся только до закрытия. */ }
    }

    /// <summary>Сбросить внешний вид, поведение и блоки. Подключение к GitHub сохраняется.</summary>
    public static void ResetAppearance()
    {
        var fresh = new AppSettings
        {
            GitHubOwner = Current.GitHubOwner,
            GitHubRepo = Current.GitHubRepo,
            GitHubTokenProtected = Current.GitHubTokenProtected
        };
        Current = fresh;
        NotifyChanged();
    }

    // ---------- Токен GitHub (шифруется средствами Windows для текущего пользователя) ----------

    public string GetToken()
    {
        if (string.IsNullOrEmpty(GitHubTokenProtected)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(GitHubTokenProtected), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return ""; }
    }

    public void SetToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            GitHubTokenProtected = "";
            return;
        }
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser);
        GitHubTokenProtected = Convert.ToBase64String(data);
    }

    // ---------- Цвета ----------

    public static bool TryParseColor(string? text, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var value = text.Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            color = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch { return false; }
    }

    [JsonIgnore]
    public Color BackgroundColorValue => TryParseColor(BackgroundColor, out var c) ? c : Color.FromRgb(5, 5, 7);

    [JsonIgnore]
    public Color AccentColorValue => TryParseColor(AccentColor, out var c) ? c : Color.FromRgb(0x0A, 0x84, 0xFF);
}

/// <summary>Запуск вместе с Windows (раздел реестра текущего пользователя).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
            else
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
