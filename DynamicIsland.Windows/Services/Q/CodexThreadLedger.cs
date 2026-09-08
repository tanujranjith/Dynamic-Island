using System.Text.Json;

namespace DynamicIsland.Windows.Services.Q;

public sealed class CodexThreadLedger
{
    private readonly object _gate = new();
    private readonly string _path;
    private HashSet<string>? _ids;

    public CodexThreadLedger(string? path = null)
    {
        _path = path ?? Path.Combine(DynamicIsland.Windows.Infrastructure.AppDataPaths.Root, "codex-thread-ledger.json");
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return [.. Load()];
    }

    public void Add(string id)
    {
        lock (_gate) if (Load().Add(id)) Save();
    }

    public void Remove(string id)
    {
        lock (_gate) if (Load().Remove(id)) Save();
    }

    private HashSet<string> Load()
    {
        if (_ids is not null) return _ids;
        try
        {
            _ids = File.Exists(_path)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch { _ids = []; }
        return _ids;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_ids));
    }
}
