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

internal static class LanNat
{
    // Windows on the target host rejects the old 42-character name with error
    // 122. This short ASCII form was verified with the actual WinNAT provider.
    public static string CreateName() => "WS-" + Guid.NewGuid().ToString("N")[..8];

    public static void Require(CommandResult result, string operation)
    {
        if (!result.Success)
            throw new InvalidOperationException(operation + ": " + result.StdErr + " " + result.StdOut);
    }

    public static void Preflight(AdapterSnapshot wifi, AdapterSnapshot ethernet, bool dhcp)
    {
        var sharing = Ics.GetStatus(out string? error);
        if (error is not null) throw new InvalidOperationException("Cannot inspect ICS: " + error);
        if (sharing.Any(e => e.Enabled))
            throw new InvalidOperationException("ICS is already enabled. Stop the existing sharing session or disable " +
                "the Wi-Fi Sharing checkbox / Mobile hotspot first. ICS and this custom-subnet NAT must not run together.");
        Require(Sys.PowerShell(
            "Get-Command New-NetNat -ErrorAction Stop | Out-Null; " +
            "if (@(Get-NetNat -ErrorAction Stop).Count -ne 0) { throw 'An existing Windows NAT is configured. " +
            "WifiShare will not replace another app or VM network.' }; " +
            "$route = Find-NetRoute -RemoteIPAddress '1.1.1.1' | Select-Object -First 1; " +
            $"$wifi = Get-NetAdapter -Name {Sys.PwshQuote(wifi.Name)}; " +
            "if ($route.InterfaceIndex -ne $wifi.ifIndex) { throw 'The default internet route does not use the selected Wi-Fi. Check VPN/default routes first.' }"),
            "Custom subnet routing is unavailable (requires Windows WinNAT)");
        if (dhcp)
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.ExclusiveAddressUse = true;
            try { probe.Bind(new IPEndPoint(IPAddress.Any, 67)); }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("Cannot listen for DHCP broadcasts on UDP 67. " +
                    "Stop the conflicting DHCP server / Mobile hotspot, or choose static client configuration. " + ex.Message);
            }
        }
    }

    public static bool GetForwarding(string name)
    {
        var r = Sys.PowerShell($"(Get-NetIPInterface -InterfaceAlias {Sys.PwshQuote(name)} " +
                               "-AddressFamily IPv4).Forwarding.ToString()");
        Require(r, "Read forwarding for " + name);
        return r.StdOut.Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase);
    }

    public static void Create(ShareState state, LanPlan plan)
    {
        Log.Step($"Enable IPv4 forwarding on '{state.PrivateAdapterName}'");
        Require(Sys.PowerShell(
            $"Set-NetIPInterface -InterfaceAlias {Sys.PwshQuote(state.PrivateAdapterName)} -AddressFamily IPv4 -Forwarding Enabled"),
            "Set-NetIPInterface (Ethernet forwarding)");
        Log.Step($"Enable IPv4 forwarding on '{state.PublicAdapterName}'");
        Require(Sys.PowerShell(
            $"Set-NetIPInterface -InterfaceAlias {Sys.PwshQuote(state.PublicAdapterName)} -AddressFamily IPv4 -Forwarding Enabled"),
            "Set-NetIPInterface (Wi-Fi forwarding)");
        Log.Step($"Create NAT '{state.NatName}' for {plan.Prefix}");
        Require(Sys.PowerShell(
            $"New-NetNat -Name {Sys.PwshQuote(state.NatName!)} -InternalIPInterfaceAddressPrefix {Sys.PwshQuote(plan.Prefix)} | Out-Null"),
            $"New-NetNat (name='{state.NatName}', prefix='{plan.Prefix}')");
        Require(Sys.PowerShell(
            $"$nat = Get-NetNat -Name {Sys.PwshQuote(state.NatName!)}; " +
            $"if (-not $nat.Active -or $nat.InternalIPInterfaceAddressPrefix -ne {Sys.PwshQuote(plan.Prefix)}) {{ throw 'NAT did not become active with the requested subnet.' }}"),
            "Verify NAT state");
    }

    // Each object is restored independently: an already-removed NAT or an
    // absent adapter must not block the remaining steps, so retries converge.
    public static bool Restore(ShareState state)
    {
        bool ok = RemoveNat(state.NatName);
        ok &= RestoreForwarding(state.PrivateAdapterId, state.PrivateAdapterName, state.OrigPrivateForwarding);
        ok &= RestoreForwarding(state.PublicAdapterId, state.PublicAdapterName, state.OrigPublicForwarding);
        return ok;
    }

    private static bool RemoveNat(string? name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        var r = Sys.PowerShell(
            $"Get-NetNat -Name {Sys.PwshQuote(name)} -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false");
        if (!r.Success) Log.Error($"Remove NAT '{name}': " + r.StdErr);
        return r.Success;
    }

    private static bool RestoreForwarding(string? adapterId, string savedName, bool original)
    {
        if (!Adapters.IsPresent(adapterId, savedName))
        {
            Log.Warn($"Adapter '{savedName}' is absent; forwarding restore deferred until it returns.");
            return false;
        }
        string name = Adapters.CurrentName(adapterId, savedName);
        var r = Sys.PowerShell(
            $"Set-NetIPInterface -InterfaceAlias {Sys.PwshQuote(name)} -AddressFamily IPv4 -Forwarding {(original ? "Enabled" : "Disabled")}");
        if (!r.Success) Log.Error($"Restore forwarding on '{name}': " + r.StdErr);
        return r.Success;
    }
}
