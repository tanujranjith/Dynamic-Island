using System.Text;

namespace DynamicIsland.Windows.Services;

public sealed class LoggingService
{
    private readonly object _gate = new();
    private readonly string _logPath;
    private bool _debugEnabled;

    public LoggingService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicIsland.Windows", "logs");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "dynamic-island.log");
    }

    public string LogPath => _logPath;
    public void SetDebugEnabled(bool enabled) => _debugEnabled = enabled;
    public void Info(string message) => Write("INFO", message);
    public void Debug(string message) { if (_debugEnabled) Write("DEBUG", message); }
    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(_logPath,
                    $"{DateTimeOffset.Now:O} [{level}] {sanitized}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length < 2_000_000) return;
        var previous = _logPath + ".1";
        File.Move(_logPath, previous, true);
    }
}
