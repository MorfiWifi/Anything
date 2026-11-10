using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

// ============================================================================
// ShareState — snapshot of the ORIGINAL settings, persisted to JSON so we
// can restore them later (even if the program was closed and reopened).
// ============================================================================
internal sealed class ShareState
{
    public string PublicAdapterName { get; set; } = "";
    public string? PublicAdapterId { get; set; }
    public string PrivateAdapterName { get; set; } = "";
    public string? PrivateAdapterId { get; set; }
    public bool HadStaticIp { get; set; }
    public bool? OrigDnsAutomatic { get; set; }
    public string HostIp { get; set; } = "";
    public string HostMask { get; set; } = "";
    public string? NatName { get; set; }
    public bool OrigPublicForwarding { get; set; }
    public bool OrigPrivateForwarding { get; set; }
    public string? OrigIp { get; set; }
    public string? OrigMask { get; set; }
    public string? OrigGateway { get; set; }
    public List<string> OrigDns { get; set; } = new();
    public string OrigSuffix { get; set; } = "";
    // null = no NetworkAddress override existed; otherwise the old value.
    public string? OrigMacOverride { get; set; } = null;
    public bool OrigMacOverrideExisted { get; set; }
    public string SharePath { get; set; } = "";
    public string ShareName { get; set; } = "";
    public bool? ShareCreatedByUs { get; set; }
    public string ShareUserName { get; set; } = Defaults.ShareUserName;
    public string DnsSuffix { get; set; } = "";
    public int HttpPort { get; set; }
    public bool IcsEnabledByUs { get; set; }
    public bool MacSpoofedByUs { get; set; }
    public bool DhcpStartedByUs { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
}

internal static class StateStore
{
    // Per-machine session file (ProgramData), shared by all builds and the service.
    public static string Path => AppData.StatePath;
    // Older builds saved the session next to the exe; still honored for recovery.
    internal static string LegacyPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "wifishare-state.json");

    public static void Save(ShareState s)
    {
        AppData.EnsureDir();
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path + ".tmp", json, Encoding.UTF8);
        File.Move(Path + ".tmp", Path, overwrite: true);
        TryDelete(LegacyPath);
    }

    public static ShareState? Load()
    {
        try
        {
            string path = File.Exists(Path) ? Path : LegacyPath;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ShareState>(json);
        }
        catch { return null; }
    }

    public static void Clear()
    {
        TryDelete(Path);
        TryDelete(LegacyPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static bool Exists => File.Exists(Path) || File.Exists(LegacyPath);
}
