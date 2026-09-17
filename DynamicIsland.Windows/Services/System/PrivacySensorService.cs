using System.Windows.Threading;
using DynamicIsland.Windows.Models;
using Microsoft.Win32;

namespace DynamicIsland.Windows.Services;

/// <summary>
/// Reports whether any app is currently using the webcam or microphone by reading the Windows
/// CapabilityAccessManager consent store. An app in active use has LastUsedTimeStop == 0 while it holds
/// the device. Read-only registry polling — no device is opened. Raises on the UI thread.
/// </summary>
public sealed class PrivacySensorService(LoggingService log) : IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    public event EventHandler<PrivacySensorState>? Changed;
    public PrivacySensorState Current { get; private set; } = PrivacySensorState.None;
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    private void Poll()
    {
        var next = new PrivacySensorState(ActiveApps("webcam"), ActiveApps("microphone"));
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(this, next);
    }

    private IReadOnlyList<string> ActiveApps(string capability)
    {
        try
        {
            var found = new List<string>();
            CollectFromRoot(Registry.CurrentUser, capability, found);
            CollectFromRoot(Registry.LocalMachine, capability, found);
            return found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch (Exception ex) { log.Debug($"Privacy sensor scan failed: {ex.Message}"); return Array.Empty<string>(); }
    }

    private static void CollectFromRoot(RegistryKey root, string capability, List<string> found)
    {
        try
        {
            using var key = root.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
            if (key is not null) CollectTree(key, capability, found);
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    private static void CollectTree(RegistryKey key, string trail, List<string> found)
    {
        if (IsActive(key))
        {
            var friendly = ToFriendlyAppName(trail);
            if (!string.IsNullOrWhiteSpace(friendly) && !IsCapabilityRootName(trail))
                found.Add(friendly);
        }

        foreach (var name in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(name);
                if (sub is not null) CollectTree(sub, trail + "\\" + name, found);
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    private static bool IsCapabilityRootName(string trail) =>
        trail.Equals("webcam", StringComparison.OrdinalIgnoreCase) ||
        trail.Equals("microphone", StringComparison.OrdinalIgnoreCase);

    internal static string ToFriendlyAppName(string trail)
    {
        if (string.IsNullOrWhiteSpace(trail)) return "Unknown app";
        try
        {
            // Desktop (non-packaged) entries encode the exe path with '#' separators,
            // e.g. NonPackaged#C:#Program Files#Foo#bar.exe — pull the exe file name.
            var exeIndex = trail.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                var exeToken = ExtractExeToken(trail[..(exeIndex + 4)]);
                var product = TryGetProductName(exeToken, trail);
                if (!string.IsNullOrWhiteSpace(product)) return Truncate(product!, 42);
                var baseName = Path.GetFileNameWithoutExtension(exeToken);
                if (!string.IsNullOrWhiteSpace(baseName)) return Truncate(Prettify(baseName!), 42);
            }

            // Packaged entries look like Microsoft.WindowsCamera_8wekyb3d8bbwe
            // (sometimes nested under a NonPackaged/parent node — use the leaf).
            var leaf = trail.Split(['\\', '#'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? trail;
            if (leaf.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) return "Desktop app";
            var beforeHash = leaf.Split('#')[0];
            var packagePart = beforeHash.Split('_')[0];
            var shortName = packagePart.Split('.').LastOrDefault() ?? packagePart;
            if (string.IsNullOrWhiteSpace(shortName)) return "Unknown app";
            return Truncate(Prettify(shortName), 42);
        }
        catch
        {
            return "Unknown app";
        }
    }

    private static string ExtractExeToken(string upToExe)
    {
        var parts = upToExe.Split(['\\', '#', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return parts[i];
        }
        return parts.LastOrDefault() ?? upToExe;
    }

    private static string? TryGetProductName(string exeToken, string trail)
    {
        try
        {
            // Rebuild a candidate full path (C:#Program Files#... -> C:\Program Files\...)
            // and prefer the publisher's ProductName/FileDescription when the file exists.
            var rebuilt = trail.Replace('#', '\\');
            var match = System.Text.RegularExpressions.Regex.Match(
                rebuilt, @"[A-Za-z]:\\.*?\.exe", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var candidate = match.Success ? match.Value : null;
            foreach (var path in new[] { candidate, exeToken })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var clean = path!.Trim('"');
                if (!Path.IsPathRooted(clean)) continue;
                if (!File.Exists(clean)) continue;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(clean);
                var product = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName!.Trim()
                    : !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription!.Trim() : null;
                if (!string.IsNullOrWhiteSpace(product)) return product;
                return null; // file found but no product string — fall back to exe base name
            }
        }
        catch { }
        return null;
    }

    private static string Prettify(string raw)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"[-_]+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return raw.Trim();
        // Keep all-caps acronyms (e.g. OBS) but title-case the rest.
        if (s.All(c => !char.IsLetter(c) || char.IsUpper(c))) return s;
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        return value[..(max - 1)].TrimEnd() + "…";
    }
    private static bool IsActive(RegistryKey key)
    {
        var start = ReadInt64(key.GetValue("LastUsedTimeStart"));
        var stop = ReadInt64(key.GetValue("LastUsedTimeStop"));
        return start is > 0 && stop.GetValueOrDefault() == 0;
    }

    private static long? ReadInt64(object? value) => value switch
    {
        long number => number,
        int number => number,
        byte[] bytes when bytes.Length >= sizeof(long) => BitConverter.ToInt64(bytes, 0),
        string text when long.TryParse(text, out var number) => number,
        _ => null
    };

    public void Dispose()
    {
        _timer.Stop();
        _started = false;
    }
}
