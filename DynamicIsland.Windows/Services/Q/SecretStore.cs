using System.Runtime.InteropServices;
using System.Text.Json;

namespace DynamicIsland.Windows.Services.Q;

public interface IQSecretStore
{
    string? Get(string providerId);
    void Set(string providerId, string? value);
    void Remove(string providerId);
}

public sealed class DpapiSecretStore(LoggingService log) : IQSecretStore
{
    private readonly string _path = Path.Combine(DynamicIsland.Windows.Infrastructure.AppDataPaths.Root, "q-secrets.dat");
    private readonly object _gate = new();

    public string? Get(string providerId) => Read().TryGetValue(providerId, out var value) ? value : null;

    public void Set(string providerId, string? value)
    {
        lock (_gate)
        {
            var values = Read();
            if (string.IsNullOrWhiteSpace(value)) values.Remove(providerId);
            else values[providerId] = value;
            Write(values);
        }
    }

    public void Remove(string providerId)
    {
        lock (_gate)
        {
            var values = Read();
            if (values.Remove(providerId)) Write(values);
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var encrypted = File.ReadAllBytes(_path);
            var plain = Protected(encrypted, false);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { log.Error("Unable to read Q credentials", ex); return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Write(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(values);
            var encrypted = Protected(plain, true);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, true);
        }
        catch (Exception ex) { log.Error("Unable to save Q credentials", ex); }
    }

    private static byte[] Protected(byte[] input, bool protect)
    {
            var data = new DataBlob(input);
            var output = new DataBlob();
            try
            {
                var ok = protect
                ? CryptProtectData(ref data.Native, null, nint.Zero, nint.Zero, nint.Zero, 0, ref output.Native)
                : CryptUnprotectData(ref data.Native, nint.Zero, nint.Zero, nint.Zero, nint.Zero, 0, ref output.Native);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return output.ToArray();
        }
        finally { data.Dispose(); output.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeBlob { public int cbData; public nint pbData; }
    private sealed class DataBlob : IDisposable
    {
        private byte[]? _bytes;
        public NativeBlob Native;
        public DataBlob() { }
        public DataBlob(byte[] bytes) { _bytes = bytes; Native = new NativeBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) }; Marshal.Copy(bytes, 0, Native.pbData, bytes.Length); }
        public byte[] ToArray() { var bytes = new byte[Native.cbData]; if (Native.pbData != nint.Zero) Marshal.Copy(Native.pbData, bytes, 0, bytes.Length); return bytes; }
        public void Dispose() { if (Native.pbData != nint.Zero) { Marshal.FreeHGlobal(Native.pbData); Native.pbData = nint.Zero; } _bytes = null; }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref NativeBlob dataIn, string? description, nint entropy, nint reserved, nint prompt, int flags, ref NativeBlob dataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref NativeBlob dataIn, nint description, nint entropy, nint reserved, nint prompt, int flags, ref NativeBlob dataOut);
}
