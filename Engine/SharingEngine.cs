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
// SharingEngine — subnet-mode orchestration (NAT + DHCP + files), free of any
// console interaction so the Windows service can drive it from the pipe.
// Failed setup rolls back; failed cleanup keeps the recovery file for retry.
// ============================================================================
internal static class SharingEngine
{
    private static HttpFileServer? _http;
    private static DhcpServer? _dhcp;

    public static bool SubnetActive { get; private set; }
    public static DhcpServer? Dhcp => _dhcp;
    public static HttpFileServer? Http => _http;

    public static SubnetResult StartSubnet(SubnetConfig cfg)
    {
        var result = new SubnetResult
        {
            HostIp = cfg.HostIp, ClientIp = cfg.ClientIp, DnsSuffix = cfg.DnsSuffix,
            ShareName = cfg.ShareName, HttpPort = cfg.HttpPort,
        };
        if (SubnetActive || StateStore.Exists)
        {
            result.Message = "A saved sharing session exists. Stop/Restore before starting again.";
            Log.Warn(result.Message);
            return result;
        }
        if (LanShareEngine.LanActive || LanStateStore.Exists)
        {
            result.Message = "LAN-mode sharing is active. Stop it before starting a subnet session.";
            Log.Warn(result.Message);
            return result;
        }
        ShareState? state = null;
        try
        {
            Log.Step(cfg.HasUplink ? "Step 1/5 — Validate the internet and client adapters"
                                   : "Step 1/5 — Validate the client adapter (isolated subnet, no uplink)");
            var all = Adapters.ListAll();
            AdapterSnapshot? uplink = null;
            if (cfg.HasUplink)
            {
                uplink = all.FirstOrDefault(a => a.Name.Equals(cfg.UplinkAdapter, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Internet adapter '{cfg.UplinkAdapter}' not found.");
            }
            var downlink = all.FirstOrDefault(a => a.Name.Equals(cfg.DownlinkAdapter, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Client adapter '{cfg.DownlinkAdapter}' not found.");
            if (downlink.Type != NetworkInterfaceType.Ethernet ||
                (uplink is not null && (uplink.Id == downlink.Id || uplink.Type != NetworkInterfaceType.Wireless80211)))
                throw new ArgumentException("Select distinct Wi-Fi and Ethernet adapters.");
            if (downlink.Status != OperationalStatus.Up || uplink is { Status: not OperationalStatus.Up })
                throw new InvalidOperationException("The adapters must be connected. Check that the client is powered on and the Ethernet cable is plugged in.");
            // A just-stopped session can briefly show the old lease plus a new
            // address; wait for the stack to settle before rejecting the adapter.
            if (downlink.IPv4.Count(ip => !ip.StartsWith("169.254.")) > 1)
                downlink = Adapters.WaitFor(downlink.Id, a => a.IPv4.Count(ip => !ip.StartsWith("169.254.")) <= 1)
                    ?? throw new InvalidOperationException("The client adapter has multiple IPv4 addresses. Use a dedicated client adapter.");

            Log.Step("Step 2/5 — Validate the client subnet and DNS");
            var plan = new LanPlan(cfg.HostIp, cfg.ClientIp);
            var overlap = all.FirstOrDefault(a => a.Id != downlink.Id && plan.Overlaps(a));
            if (overlap is not null)
                throw new InvalidOperationException($"The LAN subnet overlaps '{overlap.Name}'. Use a different subnet or disconnect the conflicting network.");
            var dnsStrings = ResolveDnsServers(cfg, uplink);
            var dnsServers = dnsStrings.Select(IPAddress.Parse).ToList();
            if (dnsServers.Any(d => d.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(d) || d.Equals(IPAddress.Any) || d.ToString() == cfg.HostIp))
                throw new ArgumentException("Use reachable upstream IPv4 DNS servers; the laptop does not run a DNS proxy in this mode.");
            result.Dns1 = dnsStrings[0];
            result.Dns2 = dnsStrings.Count > 1 ? dnsStrings[1] : "";
            Sharing.ValidateShareCredentials(cfg.ShareUserName, cfg.SharePassword);
            var scope = new DhcpScope(IPAddress.Parse(cfg.HostIp), IPAddress.Parse(LanPlan.Mask), IPAddress.Parse(cfg.HostIp),
                dnsServers, cfg.DnsSuffix, IPAddress.Parse(cfg.ClientIp), IPAddress.Parse(cfg.ClientIp), cfg.LeaseHours * 3600);
            LanNat.Preflight(uplink, downlink, cfg.Dhcp);
            LanNat.Require(Sys.PowerShell(
                $"if (Get-SmbShare -Name {Sys.PwshQuote(cfg.ShareName)} -ErrorAction SilentlyContinue) {{ throw 'The selected SMB share name already exists. Choose a different share name.' }}"),
                "Check shared folder name");

            // Persist recovery information before changing addresses or routing.
            int originalIpIndex = downlink.IPv4.FindIndex(ip => !ip.StartsWith("169.254."));
            state = new ShareState
            {
                PublicAdapterName = uplink?.Name ?? "", PublicAdapterId = uplink?.Id,
                PrivateAdapterName = downlink.Name, PrivateAdapterId = downlink.Id,
                HadStaticIp = Adapters.ShouldRestoreStatic(downlink),
                OrigIp = originalIpIndex >= 0 ? downlink.IPv4[originalIpIndex] : null,
                OrigMask = originalIpIndex >= 0 ? downlink.Masks[originalIpIndex] : null,
                OrigGateway = downlink.Gateways.FirstOrDefault(), OrigDns = new List<string>(downlink.Dns),
                OrigDnsAutomatic = IpConfig.IsDnsAutomatic(downlink.Id),
                OrigSuffix = IpConfig.GetConnectionSuffix(downlink.Id),
                // Forwarding is only touched when a NAT is created; isolated
                // sessions leave it alone, so there is nothing to snapshot.
                OrigPublicForwarding = uplink is not null && LanNat.GetForwarding(uplink.Name),
                OrigPrivateForwarding = cfg.HasUplink && LanNat.GetForwarding(downlink.Name),
                SharePath = cfg.SharePath, ShareName = cfg.ShareName, ShareCreatedByUs = false,
                ShareUserName = cfg.ShareUserName,
                DnsSuffix = cfg.DnsSuffix, HttpPort = cfg.HttpPort,
                HostIp = cfg.HostIp, HostMask = LanPlan.Mask,
                NatName = cfg.HasUplink ? LanNat.CreateName() : null,
                StartedUtc = DateTimeOffset.UtcNow,
            };
            StateStore.Save(state);

            Log.Step("Step 3/5 — Configure the laptop and subnet routing");
            // The laptop keeps the uplink's default route. The client adapter gets no gateway.
            if (!IpConfig.SetStaticIp(downlink.Name, cfg.HostIp, LanPlan.Mask, null))
                throw new InvalidOperationException("Could not assign the laptop's client-adapter address.");
            LanNat.Require(Sys.Netsh($"interface ip set dnsservers name=\"{downlink.Name}\" source=dhcp"), "Clear stale client-adapter DNS");
            bool addressReady = false;
            for (int attempt = 0; attempt < 15; attempt++)
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == downlink.Id);
                addressReady = nic?.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.ToString() == cfg.HostIp && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred) == true;
                if (addressReady) break;
                Thread.Sleep(1000);
            }
            if (!addressReady) throw new InvalidOperationException("The laptop's LAN address did not become ready. Check for a duplicate IP on the cable.");
            if (Adapters.WaitFor(downlink.Id, a => a.Gateways.Count == 0) is null)
                throw new InvalidOperationException("A stale default gateway remains on the client adapter. It must have no default gateway.");
            if (cfg.HasUplink)
            {
                LanNat.Create(state, plan);
                Log.Ok($"NAT active for {plan.Prefix}; laptop gateway {cfg.HostIp}; client {cfg.ClientIp}.");
            }
            else Log.Ok($"Isolated subnet {plan.Prefix} ready (no uplink, no NAT); laptop {cfg.HostIp}; client {cfg.ClientIp}.");
            IpConfig.SetPrivateProfile(downlink.Name);

            Log.Step("Step 4/5 — Client address assignment");
            if (cfg.Dhcp)
            {
                if (!Sharing.OpenDhcpPort(true, downlink.Name)) throw new InvalidOperationException("Could not open the client-adapter DHCP firewall rules.");
                _dhcp = new DhcpServer(scope);
                if (!_dhcp.Start(out string message)) throw new InvalidOperationException(message);
                state.DhcpStartedByUs = true;
                StateStore.Save(state);
                result.DhcpOn = true;
                Log.Ok(message);
                Log.Info("Waiting for the client: DHCP OFFER and ACK messages appear in the log.");
            }
            else Log.Info($"Static client setup required: {cfg.ClientIp}/24, gateway {cfg.HostIp}, DNS {result.Dns1}.");

            Log.Step("Step 5/5 — Shared files");
            Sharing.EnsureFirewallAndServices();
            Sharing.ConfigureLanRules(downlink.Name, true);
            state.ShareCreatedByUs = true;
            StateStore.Save(state);
            result.SmbOn = Sharing.EnsureShare(cfg.SharePath, cfg.ShareName, cfg.ShareUserName, cfg.SharePassword);
            if (!result.SmbOn)
                Log.Warn("SMB setup failed; network routing remains available.");
            _http?.Dispose();
            _http = null;
            if (cfg.Http)
            {
                Sharing.OpenHttpPort(cfg.HttpPort, true, new[] { downlink.Name });
                _http = new HttpFileServer(cfg.SharePath, cfg.HttpPort, cfg.HostIp);
                result.HttpOn = _http.Start(out string message);
                if (result.HttpOn) Log.Ok(message);
                else { Log.Warn(message); _http.Dispose(); _http = null; }
            }
            SubnetActive = true;
            StateStore.Save(state);
            result.Success = true;
            Log.Ok("Host configuration ready. Client connectivity still needs the checks in the client setup guide.");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            Log.Error("Start failed: " + ex.Message);
            if (state is not null && StateStore.Exists)
            {
                Log.Warn("Restoring the saved network settings after the failed start...");
                StopSubnet();
            }
            return result;
        }
    }

    // Explicit DNS wins; otherwise inherit the uplink's; an isolated subnet
    // has nothing to inherit, so it falls back to the configured default.
    internal static List<string> ResolveDnsServers(SubnetConfig cfg, AdapterSnapshot? uplink) =>
        cfg.Dns.Count > 0 ? cfg.Dns : new List<string> { uplink?.Dns.FirstOrDefault() ?? Defaults.DnsPrimary };

    // Stop / restore. Every step is independent and tolerant of objects that
    // are already gone; the recovery file is kept until everything succeeds.
    public static bool StopSubnet()
    {
        var state = StateStore.Load();
        _http?.Dispose(); _http = null;
        _dhcp?.Dispose(); _dhcp = null;
        SubnetActive = false;
        if (state is null)
        {
            Log.Warn("No saved session. No system network configuration was changed.");
            return true;
        }
        Log.Step("Stopping share & restoring originals");
        bool ok = Sharing.OpenDhcpPort(false);
        ok &= Sharing.ConfigureLanRules(state.PrivateAdapterName, false);
        Sharing.OpenHttpPort(state.HttpPort, false);
        if (state.NatName is not null)
            ok &= LanNat.Restore(state); // stepwise: a failed step no longer aborts the rest
        else if (state.IcsEnabledByUs)
        {
            if (Ics.TryDisable(out string message)) Log.Ok(message);
            else { Log.Error(message); ok = false; }
        }
        if (state.MacSpoofedByUs)
        {
            string? key = IpConfig.FindAdapterRegKey(state.PrivateAdapterId, "");
            if (key is not null)
            {
                IpConfig.ClearMacOverride(key, state.OrigMacOverrideExisted, state.OrigMacOverride);
                IpConfig.RestartAdapter(state.PrivateAdapterName);
            }
            else ok = false;
        }
        // Resolve the adapter by GUID: it may have been renamed since Start.
        string privateName = Adapters.CurrentName(state.PrivateAdapterId, state.PrivateAdapterName);
        if (!Adapters.IsPresent(state.PrivateAdapterId, state.PrivateAdapterName))
        {
            Log.Warn($"Adapter '{state.PrivateAdapterName}' is absent; address/DNS restore deferred until it returns.");
            ok = false;
        }
        else
        {
            // Older builds accidentally saved APIPA as static. Restore those to DHCP,
            // never recreate the invalid link-local IP + off-subnet gateway pairing.
            bool restoreStatic = state.HadStaticIp && !string.IsNullOrEmpty(state.OrigIp) &&
                                 !state.OrigIp.StartsWith("169.254.", StringComparison.Ordinal);
            if (restoreStatic)
                ok &= IpConfig.SetStaticIp(privateName, state.OrigIp!,
                    string.IsNullOrEmpty(state.OrigMask) ? LanPlan.Mask : state.OrigMask, state.OrigGateway);
            else
                ok &= IpConfig.SetDhcp(privateName);
            bool automaticDns = state.OrigDnsAutomatic ?? (!restoreStatic || state.OrigDns.Count == 0);
            if (!automaticDns && state.OrigDns.Count > 0)
                ok &= IpConfig.SetDns(privateName, state.OrigDns[0],
                    state.OrigDns.Count > 1 ? state.OrigDns[1] : null);
            else
                ok &= Sys.Netsh($"interface ip set dnsservers name=\"{privateName}\" source=dhcp").Success;
            if (state.NatName is null) // New NAT sessions don't change host suffix or MAC.
                ok &= IpConfig.SetConnectionSuffix(privateName, state.PrivateAdapterId, state.OrigSuffix);
        }
        if (state.ShareCreatedByUs != false && !string.IsNullOrEmpty(state.ShareName)) Sharing.RemoveShare(state.ShareName);
        Log.Info($"Shared folder kept: {state.SharePath}");
        if (ok)
        {
            StateStore.Clear();
            Log.Ok("Routing stopped; original client-adapter address/DNS restored.");
        }
        else Log.Error("Restore was incomplete. Saved recovery information kept; retry Stop/Restore.");
        return ok;
    }

    // Live re-point of the HTTP server after a settings change.
    public static bool RestartHttp(string root, int port, string hostIp)
    {
        try { _http?.Dispose(); } catch { }
        _http = new HttpFileServer(root, port, hostIp);
        if (_http.Start(out string message)) { Log.Ok("HTTP server restarted: " + message); return true; }
        Log.Warn("HTTP restart failed: " + message);
        _http = null;
        return false;
    }
}