using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

// The saved sharing intent. The service re-applies it at boot when Enabled,
// so folder sharing survives reboots with nobody signed in. The SMB password
// is stored DPAPI-encrypted (machine scope), never in plaintext.
internal sealed class ShareProfile
{
    public bool Enabled { get; set; }
    public string Folder { get; set; } = Defaults.SharePath;
    public string ShareName { get; set; } = Defaults.ShareName;
    public string ShareUserName { get; set; } = Defaults.ShareUserName;
    public string ProtectedPassword { get; set; } = "";
    public bool Http { get; set; } = true;
    public int HttpPort { get; set; } = Defaults.HttpPort;
    public List<string> Interfaces { get; set; } = new();

    public LanShareConfig ToLanConfig() => new()
    {
        Folder = Folder,
        ShareName = ShareName,
        ShareUserName = ShareUserName,
        SharePassword = GetPassword(),
        Http = Http,
        HttpPort = HttpPort,
        Interfaces = new List<string>(Interfaces),
    };

    public void SetPassword(string plain) => ProtectedPassword = Secret.Protect(plain);

    public string GetPassword() =>
        ProtectedPassword.Length == 0 ? Defaults.SharePassword : Secret.Unprotect(ProtectedPassword);
}

internal static class ProfileStore
{
    public static string Path => AppData.ProfilePath;

    public static void Save(ShareProfile profile)
    {
        AppData.EnsureDir();
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path + ".tmp", json, Encoding.UTF8);
        File.Move(Path + ".tmp", Path, overwrite: true);
    }

    public static ShareProfile? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<ShareProfile>(File.ReadAllText(Path, Encoding.UTF8));
        }
        catch { return null; }
    }

    public static ShareProfile FromLanConfig(LanShareConfig cfg, bool enabled)
    {
        var profile = new ShareProfile
        {
            Enabled = enabled,
            Folder = cfg.Folder,
            ShareName = cfg.ShareName,
            ShareUserName = cfg.ShareUserName,
            Http = cfg.Http,
            HttpPort = cfg.HttpPort,
            Interfaces = new List<string>(cfg.Interfaces),
        };
        profile.SetPassword(cfg.SharePassword);
        return profile;
    }
}