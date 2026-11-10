using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

// What LAN-mode created, persisted so Stop removes exactly those objects.
// LAN-mode never changes IP/DNS/routing, so there is nothing else to restore.
internal sealed class LanState
{
    public string Folder { get; set; } = "";
    public string ShareName { get; set; } = "";
    public string ShareUserName { get; set; } = "";
    public int HttpPort { get; set; }
    public bool HttpOn { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public DateTimeOffset StartedUtc { get; set; }
}

internal static class LanStateStore
{
    public static string Path => System.IO.Path.Combine(AppData.Dir, "lan-state.json");

    public static void Save(LanState s)
    {
        AppData.EnsureDir();
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path + ".tmp", json, Encoding.UTF8);
        File.Move(Path + ".tmp", Path, overwrite: true);
    }

    public static LanState? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<LanState>(File.ReadAllText(Path, Encoding.UTF8));
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { }
    }

    public static bool Exists => File.Exists(Path);
}