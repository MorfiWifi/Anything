using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// ============================================================================
// CliMain — entry-point verb dispatch. The exe is a thin client: 'install'
// and 'uninstall' need admin once; every sharing verb talks to the service
// over the named pipe, so any signed-in user can drive it. '--service' is
// the SCM entry point; 'console' is the legacy in-process interactive menu.
// ============================================================================
internal static class CliMain
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) return Menu();
        string verb = args[0].ToLowerInvariant();
        if (!Sys.IsWindows() && verb is not ("help" or "--help" or "-h" or "/?" or "adapters" or "console"))
        {
            Log.Error("Anything manages Windows networking; this command is unavailable on this OS.");
            return 2;
        }
        try
        {
            switch (verb)
            {
                case "--service":
                    return ScmHost.Run(ServiceInstaller.ServiceName, AnythingService.Start, AnythingService.Stop);
                case "install": return ServiceInstaller.Install() ? 0 : 1;
                case "uninstall": return ServiceInstaller.Uninstall() ? 0 : 1;
                case "help": case "--help": case "-h": case "/?":
                    PrintHelp(); return 0;
                case "adapters":
                    Adapters.PrintTable(Adapters.ListAll()); return 0;
                case "console":
                    return AnythingApp.Run(args.Skip(1).ToArray());
                case "share": return Share(args);
                case "stop": return PrintResult(PipeClient.Send(new PipeRequest { Cmd = "stop" }, PipeClient.CommandTimeoutMs));
                case "status": return Status();
                case "leases": return Leases();
                case "subnet": return Subnet(args);
                default:
                    Log.Error($"Unknown command '{args[0]}'.");
                    PrintHelp();
                    return 1;
            }
        }
        catch (ArgumentException ex) { Log.Error(ex.Message); return 1; }
    }

    // ---------------- option parsing ----------------
    private sealed class Options
    {
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "no-http", "no-dhcp", "no-uplink" };
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = new();

        public static Options Parse(string[] args, int start)
        {
            var o = new Options();
            for (int i = start; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    o.Positional.Add(args[i]);
                    continue;
                }
                string name = args[i][2..];
                if (Flags.Contains(name)) { o._values[name] = null; continue; }
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Option '--{name}' needs a value.");
                o._values[name] = args[++i];
            }
            return o;
        }

        public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;
        public bool HasFlag(string name) => _values.ContainsKey(name);
        public void RejectUnknown(params string[] known)
        {
            foreach (string name in _values.Keys)
                if (!known.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Unknown option '--{name}'.");
            if (Positional.Count > 0 && !known.Contains("_positional"))
                throw new ArgumentException($"Unexpected argument '{Positional[0]}'.");
        }
    }

    private static int ParsePort(string? text, int fallback)
    {
        if (text is null) return fallback;
        if (int.TryParse(text, out int p) && p is > 0 and < 65536) return p;
        throw new ArgumentException($"Invalid port '{text}' (must be 1-65535).");
    }

    // ---------------- share ----------------
    private static int Share(string[] args)
    {
        var opts = Options.Parse(args, 1);
        opts.RejectUnknown("_positional", "name", "user", "password", "port", "no-http", "interfaces");
        if (opts.Positional.Count > 1) throw new ArgumentException("share takes at most one folder argument.");
        var profile = ProfileStore.Load();
        var cfg = new LanShareConfig
        {
            Folder = opts.Positional.FirstOrDefault() ?? profile?.Folder ?? Defaults.SharePath,
            ShareName = opts.Get("name") ?? profile?.ShareName ?? Defaults.ShareName,
            ShareUserName = opts.Get("user") ?? profile?.ShareUserName ?? Defaults.ShareUserName,
            Http = !opts.HasFlag("no-http") && (profile?.Http ?? true),
            HttpPort = ParsePort(opts.Get("port"), profile?.HttpPort ?? Defaults.HttpPort),
            Interfaces = opts.Get("interfaces") is { } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : profile?.Interfaces ?? new List<string>(),
        };
        cfg.SharePassword = opts.Get("password")
            ?? (profile is not null && cfg.ShareUserName == profile.ShareUserName
                ? profile.GetPassword() : Defaults.SharePassword);
        var response = PipeClient.Send(new PipeRequest { Cmd = "share", Lan = cfg }, PipeClient.CommandTimeoutMs);
        return PrintResult(response);
    }

    // ---------------- status / leases ----------------
    private static int Status()
    {
        var response = PipeClient.Send(new PipeRequest { Cmd = "status" }, PipeClient.QueryTimeoutMs);
        if (response is null) { ServiceNotRunning(); return 1; }
        if (!response.Ok || response.Status is not { } s) { Log.Error(response.Message); return 1; }
        Console.WriteLine($"Anything service v{s.Version}");
        Console.WriteLine($"  LAN sharing:     {(s.LanActive ? "ACTIVE" : "idle")}");
        Console.WriteLine($"  Subnet mode:     {(s.SubnetActive ? "ACTIVE" : "idle")}");
        Console.WriteLine($"  Boot auto-start: {(s.AutoStartEnabled ? "enabled" : "disabled")}");
        Console.WriteLine($"  Service log:     {s.LogPath}");
        if (s.Lan is { } lan)
        {
            Console.WriteLine();
            Console.WriteLine($"  Folder:    {lan.Folder}");
            Console.WriteLine($"  SMB:       \\\\{Environment.MachineName}\\{lan.ShareName}  (user '{lan.ShareUserName}')");
            Console.WriteLine($"  HTTP:      {(lan.HttpOn ? $"port {lan.HttpPort}" : "off")}");
            Console.WriteLine($"  Interfaces: {string.Join(", ", lan.Interfaces)}");
            Console.WriteLine($"  Started:   {lan.StartedUtc:u}");
        }
        if (s.AccessUrls.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Client access:");
            foreach (string url in s.AccessUrls) Console.WriteLine("    " + url);
        }
        if (s.SubnetSummary is { } summary)
        {
            Console.WriteLine();
            Console.WriteLine("  Subnet session: " + summary);
        }
        return 0;
    }

    private static int Leases()
    {
        var response = PipeClient.Send(new PipeRequest { Cmd = "leases" }, PipeClient.QueryTimeoutMs);
        if (response is null) { ServiceNotRunning(); return 1; }
        if (!response.Ok) { Log.Error(response.Message); return 1; }
        var leases = response.Leases ?? new List<DhcpLeaseRecord>();
        if (leases.Count == 0) { Console.WriteLine("No active DHCP leases."); return 0; }
        foreach (var l in leases)
            Console.WriteLine($"  lease {l.Ip,-15} {l.Mac,-22} {l.HostName,-20} until {l.ExpiryUtc:u}");
        return 0;
    }

    // ---------------- subnet mode ----------------
    private static int Subnet(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Usage: Anything subnet start|stop [options]");
        switch (args[1].ToLowerInvariant())
        {
            case "stop":
                return PrintResult(PipeClient.Send(new PipeRequest { Cmd = "subnet-stop" }, PipeClient.CommandTimeoutMs));
            case "start": return SubnetStart(args);
            default: throw new ArgumentException($"Unknown subnet command '{args[1]}' (start|stop).");
        }
    }

    private static int SubnetStart(string[] args)
    {
        var opts = Options.Parse(args, 2);
        opts.RejectUnknown("uplink", "downlink", "no-uplink", "host-ip", "client-ip", "dns", "suffix",
            "no-dhcp", "lease-hours", "folder", "name", "user", "password", "port", "no-http");
        var all = Adapters.ListAll();
        string uplink;
        if (opts.HasFlag("no-uplink"))
        {
            if (opts.Get("uplink") is not null)
                throw new ArgumentException("--no-uplink and --uplink are mutually exclusive.");
            uplink = ""; // isolated subnet: DHCP + files only, no NAT/forwarding
        }
        else
        {
            uplink = opts.Get("uplink") ?? Adapters.DetectWifi(all)?.Name
                ?? throw new ArgumentException("No uplink detected; pass --uplink <adapter name>, or --no-uplink for an isolated subnet.");
        }
        string downlink = opts.Get("downlink") ?? Adapters.DetectEthernet(all)?.Name
            ?? throw new ArgumentException("No Ethernet downlink detected; pass --downlink <adapter name>.");
        var cfg = new SubnetConfig
        {
            UplinkAdapter = uplink,
            DownlinkAdapter = downlink,
            HostIp = opts.Get("host-ip") ?? Defaults.HostIp,
            ClientIp = opts.Get("client-ip") ?? Defaults.ClientIp,
            DnsSuffix = opts.Get("suffix") ?? Defaults.DnsSuffix,
            Dhcp = !opts.HasFlag("no-dhcp"),
            SharePath = opts.Get("folder") ?? Defaults.SharePath,
            ShareName = opts.Get("name") ?? Defaults.ShareName,
            ShareUserName = opts.Get("user") ?? Defaults.ShareUserName,
            SharePassword = opts.Get("password") ?? Defaults.SharePassword,
            Http = !opts.HasFlag("no-http"),
            HttpPort = ParsePort(opts.Get("port"), Defaults.HttpPort),
        };
        if (cfg.DnsSuffix.Equals("none", StringComparison.OrdinalIgnoreCase)) cfg.DnsSuffix = "";
        if (opts.Get("dns") is { } dns)
            cfg.Dns = dns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (opts.Get("lease-hours") is { } hours)
        {
            if (!int.TryParse(hours, out int h) || h is < 1 or > 720)
                throw new ArgumentException($"Invalid lease hours '{hours}' (must be 1-720).");
            cfg.LeaseHours = h;
        }
        var response = PipeClient.Send(new PipeRequest { Cmd = "subnet-start", Subnet = cfg }, PipeClient.CommandTimeoutMs);
        int code = PrintResult(response);
        if (response?.Subnet is { Success: true } r)
        {
            ClientHelp.Print(r.HostIp, r.ShareName, r.HttpPort, r.HttpOn,
                r.Dns1, r.Dns2, r.DhcpOn, r.ClientIp, r.ClientIp, r.DnsSuffix, isolated: !cfg.HasUplink);
            if (r.SmbOn) Sharing.PrintShareLogin(r.HostIp, r.ShareName, cfg.ShareUserName, cfg.SharePassword);
        }
        return code;
    }

    // ---------------- shared output helpers ----------------
    private static int PrintResult(PipeResponse? response)
    {
        if (response is null) { ServiceNotRunning(); return 1; }
        if (response.Ok) Log.Ok(response.Message.Length > 0 ? response.Message : "Done.");
        else Log.Error(response.Message);
        if (response.Lan is { Success: true } lan)
            foreach (string url in lan.AccessUrls) Console.WriteLine("  " + url);
        return response.Ok ? 0 : 1;
    }

    private static void ServiceNotRunning()
    {
        Log.Error("The Anything service is not reachable (not installed or not running).");
        Log.Info("One-time setup from an elevated terminal:  Anything install");
        Log.Info("After that, any signed-in user can run:    Anything share <folder> | status | stop");
    }

    // ---------------- no-args interactive menu ----------------
    private static int Menu()
    {
        while (true)
        {
            Ui.Banner();
            Console.WriteLine("  Folder sharing by IP address — background service client");
            Console.WriteLine();
            Console.WriteLine("  1. Share a folder on the current network");
            Console.WriteLine("  2. Stop sharing");
            Console.WriteLine("  3. Show status");
            Console.WriteLine("  4. Install the background service (admin, one time)");
            Console.WriteLine("  5. Uninstall the service (admin)");
            Console.WriteLine("  6. Subnet mode menu (dedicated NAT/DHCP network for a cabled client)");
            Console.WriteLine("  7. Exit");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("Select [1-7]: ");
            Console.ResetColor();
            string? choice = null;
            try { choice = Console.ReadLine()?.Trim(); } catch { }
            if (choice is null) return 0;
            try
            {
                switch (choice)
                {
                    case "1": MenuShare(); Ui.Pause(); break;
                    case "2": PrintResult(PipeClient.Send(new PipeRequest { Cmd = "stop" }, PipeClient.CommandTimeoutMs)); Ui.Pause(); break;
                    case "3": Status(); Ui.Pause(); break;
                    case "4": ServiceInstaller.Install(); Ui.Pause(); break;
                    case "5": ServiceInstaller.Uninstall(); Ui.Pause(); break;
                    case "6": AnythingApp.Run(Array.Empty<string>()); break;
                    case "7": return 0;
                    default: Log.Warn("Please type 1-7."); break;
                }
            }
            catch (ArgumentException ex) { Log.Error(ex.Message); Ui.Pause(); }
        }
    }

    private static void MenuShare()
    {
        var profile = ProfileStore.Load();
        var cfg = new LanShareConfig
        {
            Folder = Ui.Prompt("Folder to share", profile?.Folder ?? Defaults.SharePath),
            ShareName = Ui.Prompt("SMB share name", profile?.ShareName ?? Defaults.ShareName),
            ShareUserName = Ui.Prompt("SMB username", profile?.ShareUserName ?? Defaults.ShareUserName),
            HttpPort = ParsePort(Ui.Prompt("HTTP port", (profile?.HttpPort ?? Defaults.HttpPort).ToString()),
                profile?.HttpPort ?? Defaults.HttpPort),
        };
        cfg.SharePassword = Ui.PromptPassword(profile is not null && cfg.ShareUserName == profile.ShareUserName
            ? profile.GetPassword() : Defaults.SharePassword);
        cfg.Http = Ui.PromptYesNo("Also offer the folder over HTTP?", profile?.Http ?? true);
        PrintResult(PipeClient.Send(new PipeRequest { Cmd = "share", Lan = cfg }, PipeClient.CommandTimeoutMs));
    }

    // ---------------- help ----------------
    private static void PrintHelp()
    {
        Console.WriteLine("Anything — share any folder by IP address, as a background Windows service.");
        Console.WriteLine();
        Console.WriteLine("Usage: Anything <command> [options]   (no command = interactive menu)");
        Console.WriteLine();
        Console.WriteLine("SERVICE SETUP (Administrator, one time)");
        Console.WriteLine("  install                  install + start the service (LocalSystem, auto-start at boot)");
        Console.WriteLine("  uninstall                stop + remove the service (settings are kept)");
        Console.WriteLine("    Anything install");
        Console.WriteLine("    Anything uninstall");
        Console.WriteLine();
        Console.WriteLine("SHARING (any signed-in user, through the service)");
        Console.WriteLine("  share [folder]           share a folder on the current network(s); re-share = reconfigure");
        Console.WriteLine("      --name NAME          SMB share name (default: Shared)");
        Console.WriteLine("      --user NAME          SMB username (default: admin)");
        Console.WriteLine("      --password PASS      SMB password (visible in shell history; prefer the menu)");
        Console.WriteLine("      --port N             HTTP port (default: 8080)");
        Console.WriteLine("      --no-http            SMB only, no HTTP file server");
        Console.WriteLine("      --interfaces A,B     limit to these adapters (default: all connected physical)");
        Console.WriteLine("    Anything share D:\\Data");
        Console.WriteLine("    Anything share D:\\Data --name Media --user guest --password \"s3cret\" --port 9000");
        Console.WriteLine("    Anything share --no-http --interfaces \"Ethernet\"");
        Console.WriteLine("    Anything share                          (re-apply the saved profile / defaults)");
        Console.WriteLine("  stop                     stop sharing, remove share/rules, disable boot auto-start");
        Console.WriteLine("    Anything stop");
        Console.WriteLine("  status                   service state, shared folder and client access URLs");
        Console.WriteLine("    Anything status");
        Console.WriteLine("  leases                   DHCP leases (subnet mode)");
        Console.WriteLine("    Anything leases");
        Console.WriteLine();
        Console.WriteLine("SUBNET MODE (dedicated network for one cabled client)");
        Console.WriteLine("  subnet start [options]");
        Console.WriteLine("      --uplink NAME        internet adapter (default: detected Wi-Fi)");
        Console.WriteLine("      --no-uplink          isolated subnet: DHCP + files only, no NAT/internet");
        Console.WriteLine("      --downlink NAME      client adapter (default: detected Ethernet)");
        Console.WriteLine("      --host-ip IP         this machine's cable address (default: 172.20.10.1)");
        Console.WriteLine("      --client-ip IP       DHCP offer / client address (default: 172.20.10.185)");
        Console.WriteLine("      --dns IP[,IP]        client DNS servers (default: uplink DNS)");
        Console.WriteLine("      --suffix NAME|none   DNS suffix (default: mydomain.net; none = empty)");
        Console.WriteLine("      --no-dhcp            client configures a static IP instead");
        Console.WriteLine("      --lease-hours N      DHCP lease length, 1-720 (default: 24)");
        Console.WriteLine("      --folder PATH        folder to share (default: D:\\SharedWithClient)");
        Console.WriteLine("      --name/--user/--password, --port N, --no-http   share options (as above)");
        Console.WriteLine("    Anything subnet start");
        Console.WriteLine("    Anything subnet start --uplink \"Wi-Fi\" --downlink \"Ethernet\" --dns 1.1.1.1,8.8.8.8");
        Console.WriteLine("    Anything subnet start --no-uplink --host-ip 172.20.10.1 --client-ip 172.20.10.185 --suffix mydomain.net --dns 172.16.61.20,172.16.61.10");
        Console.WriteLine("    Anything subnet start --no-dhcp --folder D:\\Drop --no-http");
        Console.WriteLine("  subnet stop              stop subnet mode and restore original network settings");
        Console.WriteLine("    Anything subnet stop");
        Console.WriteLine();
        Console.WriteLine("OTHER");
        Console.WriteLine("  adapters                 list local adapters (read-only, no service needed)");
        Console.WriteLine("    Anything adapters");
        Console.WriteLine("  console                  legacy interactive menu (engines run in this process; admin)");
        Console.WriteLine("    Anything console --start | --stop | --status | --setup-share");
        Console.WriteLine("  help                     this text");
        Console.WriteLine();
        Console.WriteLine($"Settings and logs: {AppData.Dir}");
    }
}