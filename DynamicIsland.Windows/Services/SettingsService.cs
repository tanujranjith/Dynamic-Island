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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland.Windows");

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
        settings.SelectedMediaApp = string.IsNullOrWhiteSpace(settings.SelectedMediaApp)
            ? "Automatic" : settings.SelectedMediaApp;
        settings.CollapseDelayMilliseconds = Math.Clamp(settings.CollapseDelayMilliseconds, 100, 5000);
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = ThemeMode.System;
        if (!Enum.IsDefined(settings.IslandSize)) settings.IslandSize = IslandSize.Normal;
        if (!Enum.IsDefined(settings.AnimationIntensity)) settings.AnimationIntensity = AnimationIntensity.Normal;
        if (!Enum.IsDefined(settings.DefaultPosition)) settings.DefaultPosition = PositionMode.TopCenter;
        if (settings.DefaultPosition == PositionMode.Manual &&
            (settings.ManualLeftPixels is null || settings.ManualTopPixels is null))
            settings.DefaultPosition = PositionMode.TopCenter;
        settings.VisionTargetFps = Math.Clamp(settings.VisionTargetFps, 3, 15);
        settings.VisionCameraIndex = Math.Max(0, settings.VisionCameraIndex);
        settings.VisionFaceMatchThreshold = Math.Clamp(settings.VisionFaceMatchThreshold, 0.2, 0.6);
        settings.InterfaceScale = Math.Clamp(settings.InterfaceScale, 70, 150);
        settings.ClockSize = Math.Clamp(settings.ClockSize, 60, 160);
        settings.DateSize = Math.Clamp(settings.DateSize, 60, 160);
        settings.BatterySize = Math.Clamp(settings.BatterySize, 60, 160);
        settings.MediaTitleSize = Math.Clamp(settings.MediaTitleSize, 60, 160);
        settings.MediaArtistSize = Math.Clamp(settings.MediaArtistSize, 60, 160);
        settings.VolumeSize = Math.Clamp(settings.VolumeSize, 60, 160);
        settings.VisionTextSize = Math.Clamp(settings.VisionTextSize, 60, 160);
        settings.CompactTextSize = Math.Clamp(settings.CompactTextSize, 60, 160);
        settings.IdleOpacityPercent = Math.Clamp(settings.IdleOpacityPercent, 20, 100);
        settings.AutoLockDelaySeconds = Math.Clamp(settings.AutoLockDelaySeconds, 2, 60);
        if (string.IsNullOrWhiteSpace(settings.AccentColorHex)) settings.AccentColorHex = "#5AA7FF";
        if (string.IsNullOrWhiteSpace(settings.FontFamilyName)) settings.FontFamilyName = "Segoe UI Variable Text";
        if (string.IsNullOrWhiteSpace(settings.ExpandedOrder)) settings.ExpandedOrder = "media,volume,status";
        settings.LowBatteryThreshold = Math.Clamp(settings.LowBatteryThreshold, 5, 50);
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
