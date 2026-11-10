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
// IpConfig — static IP / DNS / suffix / MAC via netsh + PowerShell + registry.
// No WMI, no NuGet: everything here uses tools/APIs built into Windows.
// ============================================================================
internal static class IpConfig
{
    private static string Q(string iface) => $"\"{iface}\"";

    public static bool SetStaticIp(string iface, string ip, string mask, string? gateway)
    {
        string gwPart = string.IsNullOrEmpty(gateway) ? "none" : $"{gateway} 1";
        var r = Sys.Netsh($"interface ip set address name={Q(iface)} static {ip} {mask} {gwPart}");
        Log.Info($"netsh set address -> exit {r.ExitCode}: {FirstLine(r.StdOut)} {FirstLine(r.StdErr)}".Trim());
        return r.Success; // Restoration must not silently drop a saved gateway.
    }

    public static bool SetDhcp(string iface)
    {
        var a = Sys.Netsh($"interface ip set address name={Q(iface)} source=dhcp");
        var d = Sys.Netsh($"interface ip set dnsservers name={Q(iface)} source=dhcp");
        Log.Info($"netsh dhcp address -> {a.ExitCode}, dhcp dns -> {d.ExitCode}");
        return a.Success && d.Success;
    }

    public static bool SetDns(string iface, string primary, string? secondary)
    {
        // Clear stale entries first (ignored when the adapter uses DHCP).
        Sys.Netsh($"interface ip delete dnsservers name={Q(iface)} addr=all");
        var r1 = Sys.Netsh($"interface ip set dnsservers name={Q(iface)} static {primary} primary");
        Log.Info($"netsh set dns primary -> exit {r1.ExitCode}");
        bool ok = r1.Success;
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            var r2 = Sys.Netsh($"interface ip add dnsservers name={Q(iface)} {secondary} index=2");
            Log.Info($"netsh add dns secondary -> exit {r2.ExitCode}");
            ok = ok && r2.Success;
        }
        return ok;
    }

    public static void FlushDns()
    {
        var r = Sys.Run("ipconfig", "/flushdns", 30_000);
        Log.Info($"ipconfig /flushdns -> exit {r.ExitCode}");
    }

    // Best-effort: mark the Ethernet network as Private so discovery/sharing
    // rules and the "Unidentified network" prompt behave.
    public static void SetPrivateProfile(string iface)
    {
        var r = Sys.PowerShell(
            $"Set-NetConnectionProfile -InterfaceAlias {Sys.PwshQuote(iface)} " +
            "-NetworkCategory Private -ErrorAction Stop");
        if (r.Success) Log.Ok("Network profile set to Private.");
        else Log.Warn("Could not set Private profile: " + FirstLine(r.StdErr));
    }

    // ---- DNS suffix (connection-specific, per adapter) --------------------
    private const string TcpipIfaces =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public static bool IsDnsAutomatic(string adapterId)
    {
        using var key = Registry.LocalMachine.OpenSubKey($"{TcpipIfaces}\\{adapterId}");
        if (key is null) throw new InvalidOperationException("Cannot read original Ethernet DNS configuration.");
        return string.IsNullOrWhiteSpace(key.GetValue("NameServer") as string);
    }

    public static string GetConnectionSuffix(string? adapterId)
    {
        if (!Sys.IsWindows() || string.IsNullOrEmpty(adapterId)) return "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($"{TcpipIfaces}\\{adapterId}", false);
            return (key?.GetValue("Domain") as string) ?? "";
        }
        catch { return ""; }
    }

    public static bool SetConnectionSuffix(string iface, string? adapterId, string suffix)
    {
        bool ok = false;
        // Layer 1: registry value the TCP/IP stack actually reads.
        if (!string.IsNullOrEmpty(adapterId))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($"{TcpipIfaces}\\{adapterId}", true);
                if (key != null)
                {
                    if (string.IsNullOrEmpty(suffix)) key.DeleteValue("Domain", false);
                    else key.SetValue("Domain", suffix, RegistryValueKind.String);
                    ok = true;
                    Log.Info($"Registry Domain suffix {(string.IsNullOrEmpty(suffix) ? "cleared" : $"set to '{suffix}'")}.");
                }
            }
            catch (Exception ex) { Log.Warn("Registry suffix write failed: " + ex.Message); }
        }
        // Layer 2: documented PowerShell cmdlet (updates the live profile).
        var r = Sys.PowerShell(
            string.IsNullOrEmpty(suffix)
                ? $"Set-DnsClient -InterfaceAlias {Sys.PwshQuote(iface)} -ConnectionSpecificSuffix '' -ErrorAction Stop"
                : $"Set-DnsClient -InterfaceAlias {Sys.PwshQuote(iface)} -ConnectionSpecificSuffix {Sys.PwshQuote(suffix)} -UseSuffixWhenRegistering $true -ErrorAction Stop");
        if (r.Success) { ok = true; Log.Info("Set-DnsClient suffix applied."); }
        else Log.Warn("Set-DnsClient failed: " + FirstLine(r.StdErr));
        return ok;
    }

    // ---- MAC spoof via "NetworkAddress" registry value --------------------
    private const string NetClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static string? FindAdapterRegKey(string? adapterId, string description)
    {
        if (!Sys.IsWindows()) return null;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(NetClassKey, false);
            if (root is null) return null;
            string normId = (adapterId ?? "").Trim('{', '}').ToLowerInvariant();
            string? descFallback = null;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Length != 4) continue; // real adapters live in 0000, 0001, ...
                using var k = root.OpenSubKey(sub, false);
                if (k is null) continue;
                string cfgId = ((k.GetValue("NetCfgInstanceId") as string) ?? "").Trim('{', '}').ToLowerInvariant();
                if (!string.IsNullOrEmpty(normId) && cfgId == normId)
                    return $"{NetClassKey}\\{sub}";
                string desc = (k.GetValue("DriverDesc") as string) ?? "";
                if (!string.IsNullOrEmpty(description) &&
                    desc.Equals(description, StringComparison.OrdinalIgnoreCase))
                    descFallback = $"{NetClassKey}\\{sub}";
            }
            return descFallback;
        }
        catch (Exception ex)
        {
            Log.Warn("Adapter registry lookup failed: " + ex.Message);
            return null;
        }
    }

    // Returns (existed, oldValue). oldValue null when nothing was set.
    public static (bool existed, string? oldValue) GetMacOverride(string regKey)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(regKey, false);
            var v = k?.GetValue("NetworkAddress") as string;
            return (v is not null, v);
        }
        catch { return (false, null); }
    }

    public static bool ValidateMac(string mac, out string plain)
    {
        plain = new string(mac.Where(c => Uri.IsHexDigit(c)).ToArray()).ToUpperInvariant();
        if (plain.Length != 12) return false;
        // Reject multicast addresses (LSB of first octet set): drivers refuse.
        int first = Convert.ToInt32(plain[..2], 16);
        return (first & 1) == 0;
    }

    public static bool SetMacOverride(string regKey, string mac)
    {
        if (!ValidateMac(mac, out string plain))
        {
            Log.Error($"Refusing to write invalid/multicast MAC '{mac}'.");
            return false;
        }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(regKey, true);
            if (k is null) { Log.Error("Cannot open adapter registry key for writing."); return false; }
            k.SetValue("NetworkAddress", plain, RegistryValueKind.String);
            Log.Ok($"Registry NetworkAddress set to {plain} (shown as {mac}). Restarting adapter...");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("MAC registry write failed: " + ex.Message);
            return false;
        }
    }

    public static void ClearMacOverride(string regKey, bool existed, string? oldValue)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(regKey, true);
            if (k is null) return;
            if (existed && oldValue is not null) k.SetValue("NetworkAddress", oldValue, RegistryValueKind.String);
            else k.DeleteValue("NetworkAddress", false);
            Log.Ok("MAC override restored to original.");
        }
        catch (Exception ex) { Log.Warn("MAC restore failed: " + ex.Message); }
    }

    // Restart so IP/MAC/registry changes take effect; waits for link-up.
    public static void RestartAdapter(string iface)
    {
        Log.Step($"Restarting adapter '{iface}'...");
        var r = Sys.PowerShell(
            $"$a = Get-NetAdapter -Name {Sys.PwshQuote(iface)} -ErrorAction Stop; " +
            "Restart-NetAdapter -InputObject $a -Confirm:$false -ErrorAction Stop");
        if (!r.Success)
        {
            Log.Warn("Restart-NetAdapter failed, trying disable/enable: " + FirstLine(r.StdErr));
            Sys.Netsh($"interface set interface {Q(iface)} admin=disable");
            Thread.Sleep(3000);
            Sys.Netsh($"interface set interface {Q(iface)} admin=enable");
        }
        // Poll for the link to come back (up to ~25 s).
        for (int i = 0; i < 25; i++)
        {
            Thread.Sleep(1000);
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(iface, StringComparison.OrdinalIgnoreCase));
            if (nic?.OperationalStatus == OperationalStatus.Up)
            {
                Log.Ok($"Adapter '{iface}' is back up.");
                return;
            }
        }
        Log.Warn($"Adapter '{iface}' did not report Up within 25 s (it may still recover).");
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }
}
