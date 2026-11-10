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
// Adapters — discovery + status via System.Net.NetworkInformation (built in).
// ============================================================================
internal sealed record AdapterSnapshot(
    string Name,                    // connection name, e.g. "Wi-Fi", "Ethernet"
    string Description,             // driver description, e.g. "Realtek PCIe GbE..."
    NetworkInterfaceType Type,
    OperationalStatus Status,
    string Id,                      // NetCfgInstanceId GUID string
    string Mac,
    long SpeedBps,
    List<string> IPv4,
    List<string> Masks,
    List<string> Gateways,
    List<string> Dns,
    bool DhcpEnabled);

internal static class Adapters
{
    public static List<AdapterSnapshot> ListAll()
    {
        var list = new List<AdapterSnapshot>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            List<string> ips = new(), masks = new(), gws = new(), dns = new();
            bool dhcp = false;
            try
            {
                var props = nic.GetIPProperties();
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(u.Address)) continue;
                    ips.Add(u.Address.ToString());
                    masks.Add(u.IPv4Mask?.ToString() ?? "");
                }
                foreach (var g in props.GatewayAddresses)
                {
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        gws.Add(g.Address.ToString());
                }
                foreach (var d in props.DnsAddresses)
                {
                    if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        dns.Add(d.ToString());
                }
                dhcp = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
            }
            catch { /* one bad adapter must not hide the others */ }

            string mac = "";
            try
            {
                var bytes = nic.GetPhysicalAddress()?.GetAddressBytes();
                if (bytes is { Length: 6 })
                    mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
            catch { }

            long speed = 0;
            try { speed = nic.Speed; } catch { }

            list.Add(new AdapterSnapshot(
                nic.Name, nic.Description, nic.NetworkInterfaceType,
                nic.OperationalStatus, nic.Id, mac, speed,
                ips, masks, gws, dns, dhcp));
        }
        return list;
    }

    // Adapter names can change between sessions; the GUID is stable.
    public static AdapterSnapshot? FindById(string? id) =>
        string.IsNullOrEmpty(id) ? null : ListAll().FirstOrDefault(a =>
            a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string CurrentName(string? id, string savedName) =>
        FindById(id)?.Name ?? savedName;

    public static bool IsPresent(string? id, string savedName) =>
        FindById(id) is not null ||
        ListAll().Any(a => a.Name.Equals(savedName, StringComparison.OrdinalIgnoreCase));

    // Poll until the adapter meets a condition (address/route settling after
    // netsh changes) instead of failing on a transient intermediate state.
    public static AdapterSnapshot? WaitFor(string id, Func<AdapterSnapshot, bool> condition, int timeoutSeconds = 10)
    {
        for (int attempt = 0; attempt < timeoutSeconds; attempt++)
        {
            var a = ListAll().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (a is not null && condition(a)) return a;
            Thread.Sleep(1000);
        }
        return null;
    }

    private static bool IsApipa(string ip) => ip.StartsWith("169.254.", StringComparison.Ordinal);

    // Score Wi-Fi candidates: Up + default gateway + usable (non-APIPA) IPv4.
    public static AdapterSnapshot? DetectWifi(List<AdapterSnapshot> all)
    {
        AdapterSnapshot? best = null;
        int bestScore = -1;
        foreach (var a in all.Where(a => a.Type == NetworkInterfaceType.Wireless80211))
        {
            if (a.Status != OperationalStatus.Up || !a.IPv4.Any(ip => !IsApipa(ip)) ||
                !a.Gateways.Any(g => g != "0.0.0.0")) continue;
            int score = 0;
            if (a.Status == OperationalStatus.Up) score += 2;
            if (a.Gateways.Any(g => g != "0.0.0.0")) score += 2;
            if (a.IPv4.Any(ip => !IsApipa(ip))) score += 1;
            if (score > bestScore) { bestScore = score; best = a; }
        }
        return bestScore > 0 ? best : null;
    }

    // Score Ethernet candidates: prefer Realtek GbE hint, Up, named Ethernet.
    public static AdapterSnapshot? DetectEthernet(List<AdapterSnapshot> all)
    {
        AdapterSnapshot? best = null;
        int bestScore = -1;
        foreach (var a in all.Where(a => a.Type == NetworkInterfaceType.Ethernet))
        {
            if (IsVirtual(a)) continue;
            int score = 0;
            if (a.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (a.Description.Contains("GbE", StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains("PCIe", StringComparison.OrdinalIgnoreCase)) score += 1;
            if (a.Status == OperationalStatus.Up) score += 2;
            if (a.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) score += 1;
            if (score > bestScore) { bestScore = score; best = a; }
        }
        return bestScore >= 0 ? best : null;
    }

    internal static bool IsVirtual(AdapterSnapshot a) =>
        new[] { "virtual", "VMware", "Hyper-V", "TAP-", "TUN", "VPN", "Filter", "Miniport", "Kernel Debug" }
            .Any(s => a.Description.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                      a.Name.Contains(s, StringComparison.OrdinalIgnoreCase));

    // Physical, connected, addressed adapters — the networks LAN-mode shares on.
    public static List<AdapterSnapshot> PhysicalUp(List<AdapterSnapshot> all) =>
        all.Where(a => !IsVirtual(a) && a.Status == OperationalStatus.Up &&
                       a.Type != NetworkInterfaceType.Loopback &&
                       a.IPv4.Any(ip => !IsApipa(ip))).ToList();

    public static bool ShouldRestoreStatic(AdapterSnapshot a) =>
        !a.DhcpEnabled && a.IPv4.Any(ip => !IsApipa(ip));

    public static string FormatSpeed(long bps)
    {
        if (bps <= 0) return "unknown";
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000.0:0.#} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.#} Mbps";
        return $"{bps / 1_000.0:0.#} Kbps";
    }

    public static void PrintTable(List<AdapterSnapshot> all)
    {
        Console.WriteLine($"{"Name",-14} {"Type",-10} {"Status",-9} {"MAC",-19} {"Speed",-11} IPs");
        Console.WriteLine(new string('-', 100));
        foreach (var a in all)
        {
            string ips = a.IPv4.Count > 0 ? string.Join(",", a.IPv4) : "(none)";
            Console.WriteLine($"{Trunc(a.Name, 14),-14} {Trunc(a.Type.ToString(), 10),-10} " +
                              $"{a.Status,-9} {a.Mac,-19} {FormatSpeed(a.SpeedBps),-11} {ips}");
            Console.WriteLine($"    Desc: {a.Description}");
            if (a.Gateways.Count > 0) Console.WriteLine($"    GW:   {string.Join(",", a.Gateways)}");
            if (a.Dns.Count > 0) Console.WriteLine($"    DNS:  {string.Join(",", a.Dns)}");
        }
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s[..(n - 1)] + "…";
}
