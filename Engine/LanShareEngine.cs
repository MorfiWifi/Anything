using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

// ============================================================================
// LanShareEngine — the primary mode: share a folder on the networks this
// machine is already on (SMB + HTTP + scoped firewall). No IP, NAT, DHCP or
// routing changes, so Stop only removes what Start created.
// ============================================================================
internal static class LanShareEngine
{
    private static HttpFileServer? _http;

    public static bool LanActive { get; private set; }
    public static HttpFileServer? Http => _http;

    public static LanResult Start(LanShareConfig cfg)
    {
        var result = new LanResult
        {
            Folder = cfg.Folder, ShareName = cfg.ShareName, UserName = cfg.ShareUserName,
            HttpPort = cfg.HttpPort,
        };
        try
        {
            if (SharingEngine.SubnetActive || StateStore.Exists)
                throw new InvalidOperationException("A subnet-mode session is active. Stop it before starting LAN-mode sharing.");
            // Re-sharing with new settings is a reconfigure: tear down first.
            if (LanActive || LanStateStore.Exists) Stop();

            ValidateConfig(cfg);
            Directory.CreateDirectory(cfg.Folder);
            LanNat.Require(Sys.PowerShell(
                $"if (Get-SmbShare -Name {Sys.PwshQuote(cfg.ShareName)} -ErrorAction SilentlyContinue) {{ throw 'The selected SMB share name already exists. Choose a different share name.' }}"),
                "Check shared folder name");

            var all = Adapters.ListAll();
            List<string> interfaces;
            if (cfg.Interfaces.Count > 0)
            {
                foreach (string name in cfg.Interfaces)
                    if (!all.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"Interface '{name}' not found.");
                interfaces = cfg.Interfaces;
            }
            else
            {
                interfaces = Adapters.PhysicalUp(all).Select(a => a.Name).ToList();
                if (interfaces.Count == 0)
                    throw new InvalidOperationException("No connected physical network adapter with an IPv4 address. Connect to a network first.");
            }
            Log.Info("Sharing on: " + string.Join(", ", interfaces));

            Log.Step("Starting file sharing services and the SMB share");
            Sharing.EnsureFirewallAndServices();
            if (!Sharing.EnsureShare(cfg.Folder, cfg.ShareName, cfg.ShareUserName, cfg.SharePassword))
                throw new InvalidOperationException("SMB account/share setup failed; see the log.");
            if (!Sharing.ConfigureLanRules(interfaces.ToArray(), true))
                Log.Warn("Firewall rules failed; clients may not reach the share.");

            _http?.Dispose();
            _http = null;
            if (cfg.Http)
            {
                Sharing.OpenHttpPort(cfg.HttpPort, true, interfaces.ToArray());
                _http = new HttpFileServer(cfg.Folder, cfg.HttpPort, "*");
                result.HttpOn = _http.Start(out string message);
                if (result.HttpOn) Log.Ok("HTTP file server: " + message);
                else { Log.Warn(message); _http.Dispose(); _http = null; }
            }

            var state = new LanState
            {
                Folder = cfg.Folder, ShareName = cfg.ShareName, ShareUserName = cfg.ShareUserName,
                HttpPort = cfg.HttpPort, HttpOn = result.HttpOn,
                Interfaces = interfaces, StartedUtc = DateTimeOffset.UtcNow,
            };
            LanStateStore.Save(state);
            LanActive = true;
            result.AccessUrls = GetAccessUrls(state);
            result.Success = true;
            Log.Ok($"LAN sharing active: \\\\{Environment.MachineName}\\{cfg.ShareName} <- {cfg.Folder}");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            Log.Error("LAN share failed: " + ex.Message);
            return result;
        }
    }

    // Pure request validation, separate from Start so tests can exercise it
    // without touching OS state.
    internal static void ValidateConfig(LanShareConfig cfg)
    {
        Sharing.ValidateShareCredentials(cfg.ShareUserName, cfg.SharePassword);
        if (cfg.HttpPort is < 1 or > 65535)
            throw new ArgumentException("HTTP port must be 1-65535.");
    }

    public static bool Stop()
    {
        var state = LanStateStore.Load();
        _http?.Dispose();
        _http = null;
        LanActive = false;
        if (state is null)
        {
            Log.Info("No LAN-mode session to stop.");
            return true;
        }
        Log.Step("Stopping LAN share");
        Sharing.OpenHttpPort(state.HttpPort, false);
        Sharing.ConfigureLanRules(state.Interfaces.ToArray(), false);
        Sharing.RemoveShare(state.ShareName);
        LanStateStore.Clear();
        Log.Ok($"LAN sharing stopped. Folder kept: {state.Folder}");
        return true;
    }

    // Every address a client can use, per shared interface.
    public static List<string> GetAccessUrls(LanState state)
    {
        var urls = new List<string>();
        var all = Adapters.ListAll();
        foreach (string name in state.Interfaces)
        {
            var adapter = all.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) continue;
            foreach (string ip in adapter.IPv4.Where(ip => !ip.StartsWith("169.254.")))
            {
                urls.Add($"\\\\{ip}\\{state.ShareName}");
                if (state.HttpOn) urls.Add($"http://{ip}:{state.HttpPort}/");
            }
        }
        return urls;
    }
}