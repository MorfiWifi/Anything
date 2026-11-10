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

// Per-machine files shared by every build and the Windows service, so a
// session started from one executable can always be stopped from another.
internal static class AppData
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Anything");
    public static string StatePath => Path.Combine(Dir, "anything-state.json");
    public static string ProfilePath => Path.Combine(Dir, "profile.json");
    public static string LeasePath => Path.Combine(Dir, "dhcp-leases.json");
    public static string LogPath => Path.Combine(Dir, "Anything.log");

    private static string LegacyDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WifiShare");

    public static void EnsureDir()
    {
        MigrateLegacy();
        Directory.CreateDirectory(Dir);
    }

    // One-time adoption of a pre-rename (WifiShare) installation: move the data
    // folder, rename state/log files, and copy the managed-account registry
    // markers so the existing SMB account stays recognized after the upgrade.
    private static void MigrateLegacy()
    {
        try
        {
            if (!Directory.Exists(Dir) && Directory.Exists(LegacyDir))
                Directory.Move(LegacyDir, Dir);
            if (Directory.Exists(Dir))
            {
                RenameFile("wifishare-state.json", "anything-state.json");
                RenameFile("wifishare-state.json.tmp", "anything-state.json.tmp");
                RenameFile("WifiShare.log", "Anything.log");
            }
            MigrateLegacyRegistry();
        }
        catch { /* best-effort: a locked folder or non-admin caller just skips migration */ }
    }

    private static void RenameFile(string oldName, string newName)
    {
        string oldPath = Path.Combine(Dir, oldName), newPath = Path.Combine(Dir, newName);
        if (File.Exists(oldPath) && !File.Exists(newPath)) File.Move(oldPath, newPath);
    }

    private static void MigrateLegacyRegistry()
    {
        // The share script runs in 64-bit PowerShell, so the markers live in
        // the 64-bit view regardless of this process's bitness.
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var old = baseKey.OpenSubKey(@"SOFTWARE\WifiShare\ShareAccounts");
        if (old is null) return;
        using var current = baseKey.CreateSubKey(@"SOFTWARE\Anything\ShareAccounts");
        foreach (string name in old.GetValueNames())
            if (old.GetValue(name) is { } value) current.SetValue(name, value);
        baseKey.DeleteSubKeyTree(@"SOFTWARE\WifiShare", throwOnMissingSubKey: false);
    }
}