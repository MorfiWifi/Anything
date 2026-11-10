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
// WifiShareApp — interactive menu + CLI flags. Prompts live here; all network
// orchestration lives in SharingEngine so the Windows service can reuse it.
// ============================================================================
internal static class WifiShareApp
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var argSet = new HashSet<string>(args.Select(a => a.ToLowerInvariant()));

        if (argSet.Contains("--help") || argSet.Contains("-h") || argSet.Contains("/?"))
        {
            PrintHelp();
            return 0;
        }

        if (!Sys.IsWindows())
        {
            Log.Warn("This tool manages Windows networking (WinNAT / DHCP / netsh).");
            Log.Warn($"You are on {RuntimeInformation.OSDescription}; sharing actions are disabled.");
            Ui.Banner();
            var all = Adapters.ListAll(); // cross-platform part still works
            Ui.Section("Local adapters (read-only view)");
            Adapters.PrintTable(all);
            return 2;
        }

        // Read-only diagnostics are useful even before elevation is available.
        if (argSet.Contains("--status")) { Ui.Banner(); ShowStatus(); return 0; }

        // Must be admin: offer to relaunch elevated, preserving CLI args.
        if (!Sys.IsAdmin())
        {
            Ui.Banner();
            Log.Warn("Administrator rights are required (IP, NAT, firewall and shares).");
            string joined = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            if (Ui.PromptYesNo("Relaunch as Administrator now?", true))
            {
                if (Sys.RelaunchAsAdmin(joined)) return 0;
                Log.Error("Could not relaunch elevated. Right-click > 'Run as administrator'.");
                return 1;
            }
            Log.Error("Continuing without admin will fail. Exiting.");
            return 1;
        }

        // Update credentials on a running share without stopping NAT/DHCP.
        if (argSet.Contains("--setup-share"))
        {
            var session = StateStore.Load();
            if (session is not null)
            {
                Settings.SharePath = session.SharePath;
                Settings.ShareName = session.ShareName;
                Settings.ShareUserName = session.ShareUserName;
            }
            if (!Sharing.EnsureShare(Settings.SharePath, Settings.ShareName, Settings.ShareUserName, Settings.SharePassword)) return 1;
            Sharing.PrintShareLogin(session?.HostIp is { Length: > 0 } address ? address : Defaults.HostIp,
                Settings.ShareName, Settings.ShareUserName, Settings.SharePassword);
            return 0;
        }

        // Ctrl+C: try to leave the machine clean instead of abandoning NAT+IP.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Warn("Ctrl+C pressed — restoring network settings before exit...");
            try { SharingEngine.StopSubnet(); } catch { }
            Environment.Exit(130);
        };

        // Non-interactive flags (scripting friendly).
        if (argSet.Contains("--start"))
        {
            Ui.Banner(); StartSharing();
            if (!SharingEngine.SubnetActive) return 1;
            MenuLoop(); // keep the DHCP/HTTP workers alive until the user exits
            return 0;
        }
        if (argSet.Contains("--stop")) { Ui.Banner(); StopSharing(); return 0; }

        // If a previous session is still active, resume that knowledge.
        var saved = StateStore.Load();
        if (saved is not null)
        {
            Settings.SharePath = saved.SharePath;
            Settings.ShareName = saved.ShareName;
            Settings.ShareUserName = saved.ShareUserName;
            Settings.DnsSuffix = saved.DnsSuffix;
            Settings.HttpPort = saved.HttpPort;
            Log.Warn("Saved network session found. DHCP/HTTP workers are stopped; use Stop/Restore before a new Start.");
        }

        MenuLoop();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("WifiShare — share laptop Wi-Fi over Ethernet (Windows, admin required).");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  WifiShare                 interactive menu");
        Console.WriteLine("  WifiShare --start         configure sharing, then keep the menu/services running");
        Console.WriteLine("  WifiShare --stop          stop sharing and restore original settings");
        Console.WriteLine("  WifiShare --setup-share   create/update SMB login without restarting networking");
        Console.WriteLine("  WifiShare --status        show adapters, IPs and sharing state");
        Console.WriteLine("  WifiShare --help          this text");
    }

    // ---------------- interactive menu ----------------
    private static void MenuLoop()
    {
        while (true)
        {
            Ui.Banner();
            Console.WriteLine($"Status: {(SharingEngine.SubnetActive ? "SHARING ACTIVE" : "idle")}");
            Console.WriteLine();
            Console.WriteLine("  1. Start Sharing (custom-subnet NAT + client DHCP + share folder)");
            Console.WriteLine("  2. Stop / Restore original network settings");
            Console.WriteLine("  3. Change settings (folder, share name, client DNS suffix, HTTP port)");
            Console.WriteLine("  4. Show current status (adapters, IPs, sharing state)");
            Console.WriteLine("  5. Exit");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("Select [1-5]: ");
            Console.ResetColor();
            string? choice = null;
            try { choice = Console.ReadLine()?.Trim(); } catch { }
            if (choice is null)
            {
                if (SharingEngine.SubnetActive) StopSharing(quiet: true);
                return;
            }

            switch (choice)
            {
                case "1": StartSharing(); Ui.Pause(); break;
                case "2": StopSharing(); Ui.Pause(); break;
                case "3": ChangeSettings(); Ui.Pause(); break;
                case "4": ShowStatus(); Ui.Pause(); break;
                case "5":
                    if (SharingEngine.SubnetActive)
                    {
                        Ui.Section("Sharing is still active");
                        if (Ui.PromptYesNo("Stop sharing and restore settings before exit?", true))
                            StopSharing();
                        else
                            Log.Warn("Leaving sharing active. Run option 2 later to restore.");
                    }
                    Log.Info("Bye.");
                    return;
                default:
                    Log.Warn("Please type 1-5.");
                    Thread.Sleep(700);
                    break;
            }
        }
    }

    // ---------------- option 1: START (prompts, then hand off to the engine) ----------------
    private static void StartSharing()
    {
        if (SharingEngine.SubnetActive || StateStore.Exists)
        {
            Log.Warn("A saved sharing session exists. Use option 2 (Stop/Restore) before starting again.");
            return;
        }
        SubnetConfig cfg;
        try { cfg = PromptSubnetConfig(); }
        catch (Exception ex) { Log.Error("Start cancelled: " + ex.Message); return; }
        var result = SharingEngine.StartSubnet(cfg);
        if (!result.Success) return; // engine logged the error and rolled back
        ClientHelp.Print(result.HostIp, result.ShareName, result.HttpPort, result.HttpOn,
            result.Dns1, result.Dns2, result.DhcpOn, result.ClientIp, result.ClientIp, result.DnsSuffix);
        if (result.SmbOn) Sharing.PrintShareLogin(result.HostIp, result.ShareName, cfg.ShareUserName, cfg.SharePassword);
    }

    private static SubnetConfig PromptSubnetConfig()
    {
        Ui.Section("Select the internet and client adapters");
        var all = Adapters.ListAll();
        Adapters.PrintTable(all);
        var wifi = Adapters.DetectWifi(all)
            ?? throw new InvalidOperationException("Connect Wi-Fi first; it needs an IPv4 address and a default gateway.");
        var eth = Adapters.DetectEthernet(all)
            ?? throw new InvalidOperationException("No physical Ethernet adapter found. Check the cable and adapter.");
        Log.Info($"Internet: '{wifi.Name}'; client cable: '{eth.Name}'.");
        if (!Ui.PromptYesNo("Use these adapters?", true))
        {
            string w = Ui.Prompt("Internet Wi-Fi adapter name", wifi.Name);
            string e = Ui.Prompt("Client Ethernet adapter name", eth.Name);
            wifi = all.FirstOrDefault(a => a.Name.Equals(w, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Wi-Fi adapter name not found.");
            eth = all.FirstOrDefault(a => a.Name.Equals(e, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Ethernet adapter name not found.");
        }
        var cfg = new SubnetConfig
        {
            UplinkAdapter = wifi.Name,
            DownlinkAdapter = eth.Name,
            SharePath = Settings.SharePath,
            ShareName = Settings.ShareName,
            ShareUserName = Settings.ShareUserName,
            SharePassword = Settings.SharePassword,
            LeaseHours = Settings.LeaseHours,
            HttpPort = Settings.HttpPort,
        };
        Ui.Section("Client address and laptop gateway");
        cfg.ClientIp = Ui.Prompt("CLIENT IPv4 address (the PC at the other end of the cable)", Defaults.ClientIp);
        cfg.HostIp = Ui.Prompt("LAPTOP Ethernet IPv4 / client's gateway (must be different)", Defaults.HostIp);
        string dns1 = Ui.Prompt("Client primary DNS (from Wi-Fi)", wifi.Dns.FirstOrDefault() ?? Defaults.DnsPrimary);
        string dns2 = Ui.Prompt("Client secondary DNS (type none to omit)", wifi.Dns.Skip(1).FirstOrDefault() ?? "none");
        cfg.Dns.Add(dns1);
        if (!dns2.Equals("none", StringComparison.OrdinalIgnoreCase)) cfg.Dns.Add(dns2);
        Settings.DnsSuffix = Ui.Prompt("Client DNS suffix (type none to omit)", Settings.DnsSuffix);
        if (Settings.DnsSuffix.Equals("none", StringComparison.OrdinalIgnoreCase)) Settings.DnsSuffix = "";
        cfg.DnsSuffix = Settings.DnsSuffix;
        cfg.Dhcp = Ui.PromptYesNo($"Offer {cfg.ClientIp} automatically to the single Ethernet client via DHCP?", true);
        cfg.Http = Settings.StartHttpServer && Ui.PromptYesNo($"Start HTTP file server on port {Settings.HttpPort}?", true);
        return cfg;
    }

    // ---------------- option 2: STOP / RESTORE ----------------
    private static void StopSharing(bool quiet = false)
    {
        SharingEngine.StopSubnet();
        if (!quiet) Ui.Pause();
    }

    // ---------------- option 3: SETTINGS ----------------
    private static void ChangeSettings()
    {
        Ui.Section("Settings (applied on next Start; folder applies live if active)");
        Settings.SharePath = Ui.Prompt("Shared folder path", Settings.SharePath);
        Settings.ShareName = Ui.Prompt("SMB share name", Settings.ShareName);
        string userName = Ui.Prompt("SMB username", Settings.ShareUserName);
        string password = Ui.PromptPassword(Settings.SharePassword);
        try
        {
            Sharing.ValidateShareCredentials(userName, password);
            Settings.ShareUserName = userName;
            Settings.SharePassword = password;
        }
        catch (ArgumentException ex) { Log.Warn(ex.Message + " Keeping the previous SMB credentials."); }
        Settings.DnsSuffix = Ui.Prompt("Primary DNS suffix", Settings.DnsSuffix);
        string port = Ui.Prompt("HTTP port", Settings.HttpPort.ToString());
        if (int.TryParse(port, out int p) && p is > 0 and < 65536) Settings.HttpPort = p;
        else Log.Warn("Invalid port, keeping previous.");
        Settings.StartHttpServer = Ui.PromptYesNo("Offer HTTP file server on Start?", Settings.StartHttpServer);
        string lh = Ui.Prompt("DHCP lease time (hours)", Settings.LeaseHours.ToString());
        if (int.TryParse(lh, out int h) && h >= 1 && h <= 720) Settings.LeaseHours = h;
        else Log.Warn("Invalid lease time, keeping previous.");
        Log.Ok("Settings updated.");

        // Live-apply the folder if we are currently sharing.
        var state = StateStore.Load();
        if (SharingEngine.SubnetActive && state is not null)
        {
            Log.Step("Sharing is active — re-pointing the share to the new folder...");
            Sharing.RemoveShare(state.ShareName);
            if (Sharing.EnsureShare(Settings.SharePath, Settings.ShareName, Settings.ShareUserName, Settings.SharePassword))
            {
                state.SharePath = Settings.SharePath;
                state.ShareName = Settings.ShareName;
                state.ShareUserName = Settings.ShareUserName;
                StateStore.Save(state);
                Sharing.PrintShareLogin(state.HostIp, state.ShareName, Settings.ShareUserName, Settings.SharePassword);
                SharingEngine.RestartHttp(Settings.SharePath, state.HttpPort, state.HostIp);
            }
        }
    }

    // ---------------- option 4: STATUS ----------------
    private static void ShowStatus()
    {
        Ui.Section("Adapters & IPs");
        var all = Adapters.ListAll();
        Adapters.PrintTable(all);

        Ui.Section("Internet Connection Sharing");
        var entries = Ics.GetStatus(out string? icsError);
        if (icsError is not null) Log.Warn("ICS status: " + icsError);
        if (entries.Count == 0)
            Log.Info("(ICS unavailable or no connections enumerated.)");
        foreach (var e in entries)
        {
            string flag = e.Enabled ? $"SHARED [{e.Kind}]" : "not shared";
            Console.WriteLine($"  {e.Name}  ({e.Device})  ->  {flag}");
        }

        Ui.Section("SMB shares");
        Console.WriteLine(Sharing.GetShareTable());

        Ui.Section("LAN routing and DHCP diagnostics");
        var diagnostic = Sys.PowerShell(
            "Get-NetNat | Format-Table Name,InternalIPInterfaceAddressPrefix,Active; " +
            "Get-Service SharedAccess,Dhcp | Format-Table Name,Status; " +
            "Get-NetConnectionProfile | Format-Table InterfaceAlias,NetworkCategory,IPv4Connectivity; " +
            "Get-NetUDPEndpoint -LocalPort 67 -ErrorAction SilentlyContinue | Format-Table LocalAddress,LocalPort,OwningProcess");
        Console.WriteLine(diagnostic.StdOut);
        if (!diagnostic.Success) Log.Warn(diagnostic.StdErr);

        Ui.Section("DHCP server (plug-and-play)");
        var dhcp = SharingEngine.Dhcp;
        if (dhcp?.Running == true)
        {
            Console.WriteLine($"  Running: pool {dhcp.PoolSummary}  ({dhcp.LeaseSummary})");
            var leases = dhcp.GetLeases();
            if (leases.Count == 0) Console.WriteLine("  No active leases yet (plug the client in).");
            foreach (var l in leases)
                Console.WriteLine($"  lease {l.Ip,-15} {l.Mac,-22} {l.HostName,-20} until {l.ExpiryUtc:u}");
        }
        else Console.WriteLine("  Stopped.");

        Ui.Section("HTTP file server");
        var http = SharingEngine.Http;
        Console.WriteLine(http?.Running == true
            ? $"  Running: {http.Url}  (share: {Settings.SharePath})"
            : "  Stopped.");

        Ui.Section("Saved session");
        var s = StateStore.Load();
        if (s is null) Console.WriteLine("  (none)");
        else
        {
            Console.WriteLine($"  Started (UTC): {s.StartedUtc:u}");
            Console.WriteLine($"  Public:  {s.PublicAdapterName}");
            Console.WriteLine($"  Private: {s.PrivateAdapterName}  NAT={s.NatName ?? "legacy ICS"}  Host={s.HostIp}");
            Console.WriteLine($"  Orig IP: {s.OrigIp ?? "DHCP/none"}  Orig DNS: [{string.Join(",", s.OrigDns)}]  Orig suffix: '{s.OrigSuffix}'");
            Console.WriteLine($"  Share:   \\\\{Environment.MachineName}\\{s.ShareName}  <-  {s.SharePath}");
        }

        Ui.Section("Identity notes");
        Console.WriteLine("  Description / Manufacturer / Driver version: read-only (driver INF).");
        Console.WriteLine("  Link speed: real negotiated speed (cannot be spoofed).");
        Console.WriteLine("  Encryption flag: N/A — Ethernet is unencrypted at L2 by nature.");
    }
}