using System.Text.Json;
using DynamicIsland.Windows.Models;

namespace DynamicIsland.Windows.Services;

public sealed class SettingsService(LoggingService log)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = DynamicIsland.Windows.Infrastructure.AppDataPaths.Root;

    public string SettingsPath => Path.Combine(_directory, "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        Directory.CreateDirectory(_directory);
        if (!File.Exists(SettingsPath)) return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex)
        {
            try
            {
                var backup = Path.Combine(_directory,
                    $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(SettingsPath, backup, true);
            }
            catch { }
            log.Error("Settings were invalid and defaults were restored", ex);
            var defaults = new AppSettings();
            await SaveAsync(defaults);
            return defaults;
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Normalize(settings);
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = SettingsPath + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception ex)
        {
            log.Error("Unable to save settings", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Normalize(AppSettings settings)
    {
        var previousSchema = settings.SchemaVersion;
        if (previousSchema < 4 && settings.QMaxResponseTokens == 1200)
            settings.QMaxResponseTokens = 8192;
        if (previousSchema < 6)
            settings.ShowIslandInScreenshots = false;
        if (previousSchema < 10)
            settings.QAutoExpandIsland = true;
        settings.SchemaVersion = Math.Max(11, settings.SchemaVersion);
        if (settings.PinnedActivity is not (IslandActivity.None or IslandActivity.Media or IslandActivity.Timer)) settings.PinnedActivity = IslandActivity.None;
        settings.SelectedMediaApp = string.IsNullOrWhiteSpace(settings.SelectedMediaApp)
            ? "Automatic" : settings.SelectedMediaApp;
        settings.CollapseDelayMilliseconds = Math.Clamp(settings.CollapseDelayMilliseconds, 100, 5000);
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = ThemeMode.System;
        if (!Enum.IsDefined(settings.IslandSize)) settings.IslandSize = IslandSize.Normal;
        if (!Enum.IsDefined(settings.IslandVisualMode)) settings.IslandVisualMode = IslandVisualMode.Apple;
        settings.IslandWidth = Math.Clamp(settings.IslandWidth, 72, 360);
        settings.IslandHeight = Math.Clamp(settings.IslandHeight, 38, 90);
        if (!Enum.IsDefined(settings.AnimationIntensity)) settings.AnimationIntensity = AnimationIntensity.Normal;
        if (!Enum.IsDefined(settings.DefaultPosition)) settings.DefaultPosition = PositionMode.TopCenter;
        if (settings.DefaultPosition == PositionMode.Manual &&
            (settings.ManualLeftPixels is null || settings.ManualTopPixels is null))
            settings.DefaultPosition = PositionMode.TopCenter;
        settings.InterfaceScale = Math.Clamp(settings.InterfaceScale, 70, 150);
        settings.ClockSize = Math.Clamp(settings.ClockSize, 60, 160);
        settings.DateSize = Math.Clamp(settings.DateSize, 60, 160);
        settings.BatterySize = Math.Clamp(settings.BatterySize, 60, 160);
        settings.MediaTitleSize = Math.Clamp(settings.MediaTitleSize, 60, 160);
        settings.MediaArtistSize = Math.Clamp(settings.MediaArtistSize, 60, 160);
        settings.VolumeSize = Math.Clamp(settings.VolumeSize, 60, 160);
        settings.CompactTextSize = Math.Clamp(settings.CompactTextSize, 60, 160);
        settings.AlbumCornerRadius = Math.Clamp(settings.AlbumCornerRadius, 0, 30);
        settings.IdleOpacityPercent = Math.Clamp(settings.IdleOpacityPercent, 20, 100);
        if (string.IsNullOrWhiteSpace(settings.AccentColorHex)) settings.AccentColorHex = "#5AA7FF";
        if (string.IsNullOrWhiteSpace(settings.FontFamilyName)) settings.FontFamilyName = "Segoe UI Variable Text";
        if (string.IsNullOrWhiteSpace(settings.ExpandedOrder)) settings.ExpandedOrder = "media,volume,status";
        settings.LowBatteryThreshold = Math.Clamp(settings.LowBatteryThreshold, 5, 50);
        settings.VolumeWarningThreshold = Math.Clamp(settings.VolumeWarningThreshold, 10, 100);
        if (!Enum.IsDefined(settings.QCaptureMode)) settings.QCaptureMode = Models.QCaptureMode.ActiveWindow;
        settings.QSelectedProvider = string.IsNullOrWhiteSpace(settings.QSelectedProvider) ? "openai" : settings.QSelectedProvider.Trim();
        settings.QSelectedModel = string.IsNullOrWhiteSpace(settings.QSelectedModel) ? "gpt-4o-mini" : settings.QSelectedModel.Trim();
        settings.QOllamaBaseUrl = string.IsNullOrWhiteSpace(settings.QOllamaBaseUrl) ? "http://localhost:11434" : settings.QOllamaBaseUrl.TrimEnd('/');
        settings.QTimeoutSeconds = Math.Clamp(settings.QTimeoutSeconds, 10, 300);
        settings.QAutoCloseDelaySeconds = Math.Clamp(settings.QAutoCloseDelaySeconds, 1, 300);
        settings.QMaxResponseTokens = Math.Clamp(settings.QMaxResponseTokens, 2048, 32768);
        settings.QReasoningEffort = settings.QReasoningEffort?.Trim().ToLowerInvariant() switch
        {
            "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra" => settings.QReasoningEffort.Trim().ToLowerInvariant(),
            _ => "auto"
        };
        settings.QAskSystemPrompt ??= "";
        settings.QSaySystemPrompt ??= "";
        settings.QShortcuts ??= [];
        settings.QHotkeyShortcut ??= "";
        if (!string.IsNullOrWhiteSpace(settings.QHotkeyShortcut) &&
            !settings.QShortcuts.Any(shortcut => string.Equals(shortcut.Name, settings.QHotkeyShortcut, StringComparison.OrdinalIgnoreCase)))
            settings.QHotkeyShortcut = "";
        if (!Enum.IsDefined(settings.QuotePlacement)) settings.QuotePlacement = QuotePlacement.Off;
        if (!Enum.IsDefined(settings.QuoteRotation)) settings.QuoteRotation = QuoteRotation.Static;
        settings.QuoteSize = Math.Clamp(settings.QuoteSize, 60, 160);
    }

    public string PresetsDir => Path.Combine(_directory, "presets");
    private string PresetPath(string name) => Path.Combine(PresetsDir, SanitizeName(name) + ".json");
    private static string SanitizeName(string name) =>
        string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();

    public IReadOnlyList<string> ListPresets()
    {
        try
        {
            if (!Directory.Exists(PresetsDir)) return [];
            return Directory.GetFiles(PresetsDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).OrderBy(n => n).ToList();
        }
        catch { return []; }
    }

    public async Task SavePresetAsync(AppSettings settings, string name)
    {
        Directory.CreateDirectory(PresetsDir);
        await ExportAsync(settings, PresetPath(name));
    }

    public Task<AppSettings?> LoadPresetAsync(string name) => ImportAsync(PresetPath(name));

    public void DeletePreset(string name)
    {
        try { var p = PresetPath(name); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) { log.Error("Unable to delete preset", ex); }
    }

    /// <summary>Writes the current settings to an arbitrary file (for sharing/backup).</summary>
    public async Task ExportAsync(AppSettings settings, string path)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    /// <summary>Reads settings from an arbitrary file. Returns null if it can't be parsed.</summary>
    public async Task<AppSettings?> ImportAsync(string path)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions);
            if (settings is null) return null;
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) { log.Error("Unable to import settings", ex); return null; }
    }
}
