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
// Ics — Internet Connection Sharing via the documented HNetCfg COM API
// (the same engine behind the "Allow other network users to connect"
// checkbox). No NuGet: plain ComImport interfaces, early-bound in IDL order.
// ============================================================================
internal enum SharingConnectionType : int { Public = 0, Private = 1 }
internal enum SharingConnectionEnumFlags : int { Default = 0, Public = 1, Private = 2, All = 3 }

[ComImport, Guid("5C63C1AD-3956-4FF8-8486-40034758315B")]
internal class NetSharingManager { }

// GUIDs and vtable order must match the Windows SDK NetCon.h declarations.
[ComImport, Guid("C08956B7-1CD3-11D1-B1C5-00805FC1270E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetSharingManager
{
    bool SharingInstalled { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [return: MarshalAs(UnmanagedType.Interface)]
    object get_EnumPublicConnections(SharingConnectionEnumFlags flags);
    [return: MarshalAs(UnmanagedType.Interface)]
    object get_EnumPrivateConnections(SharingConnectionEnumFlags flags);
    INetSharingConfiguration get_INetSharingConfigurationForINetConnection(
        [MarshalAs(UnmanagedType.Interface)] INetConnection connection);
    INetSharingEveryConnectionCollection get_EnumEveryConnection();
    INetConnectionProps get_NetConnectionProps(
        [MarshalAs(UnmanagedType.Interface)] INetConnection connection);
}

[ComImport, Guid("33C4643C-7811-46FA-A89A-768597BD7223")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetSharingEveryConnectionCollection
{
    [DispId(-4)] IEnumerator GetEnumerator(); // _NewEnum: standard tlbimp pattern
    int Count { get; }
}

[ComImport, Guid("C08956A1-1CD3-11D1-B1C5-00805FC1270E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INetConnection
{
    // Opaque connection handle: properties are obtained through the manager.
    // No INetConnection methods are invoked by this application.
}

[ComImport, Guid("F4277C95-CE5B-463D-8167-5662D9BCAA72")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetConnectionProps
{
    string Guid { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string DeviceName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    int Status { get; }
    int MediaType { get; }
    uint Characteristics { get; }
}

[ComImport, Guid("C08956B6-1CD3-11D1-B1C5-00805FC1270E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetSharingConfiguration
{
    bool SharingEnabled { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    SharingConnectionType SharingConnectionType { get; }
    void DisableSharing();
    void EnableSharing(SharingConnectionType type);
}

internal sealed record IcsEntry(string Name, string Device, bool Enabled, string Kind);

internal static class Ics
{
    private static string NormId(string? id) =>
        (id ?? "").Trim().Trim('{', '}').ToLowerInvariant();

    private static bool IsMatch(string? name, string? device, Guid id, string wantName, string? wantId)
    {
        if (!string.IsNullOrEmpty(name) &&
            name.Equals(wantName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(wantId) && NormId(id.ToString()) == NormId(wantId)) return true;
        return false;
    }

    // Read one connection defensively: a single failing property must not
    // abort the whole enumeration.
    private static (string? name, string? device, Guid id) Describe(INetSharingManager mgr, INetConnection c)
    {
        string? n = null, d = null;
        Guid g = Guid.Empty;
        INetConnectionProps? props = null;
        try
        {
            props = mgr.get_NetConnectionProps(c);
            try { n = props.Name; } catch { }
            try { d = props.DeviceName; } catch { }
            try { Guid.TryParse(props.Guid, out g); } catch { }
        }
        catch { /* a connection may disappear during enumeration */ }
        finally
        {
            if (props is not null)
                try { Marshal.ReleaseComObject(props); } catch { }
        }
        return (n, d, g);
    }

    public static bool TryEnable(string publicName, string? publicId,
                                 string privateName, string? privateId,
                                 out string message)
    {
        message = "";
        if (!Sys.IsWindows()) { message = "ICS requires Windows."; return false; }

        INetSharingManager? mgr = null;
        var rcws = new List<object>(); // every RCW we touch, released in finally
        try
        {
            mgr = (INetSharingManager)new NetSharingManager();
            rcws.Add(mgr);

            if (!mgr.SharingInstalled)
            {
                message = "ICS is not installed on this Windows edition.";
                return false;
            }

            var every = mgr.get_EnumEveryConnection();
            rcws.Add(every);

            INetConnection? pub = null, prv = null;
            Log.Info("Enumerating network connections visible to ICS:");
            foreach (object? o in every)
            {
                if (o is not INetConnection c) continue;
                rcws.Add(c);
                var (n, d, g) = Describe(mgr, c);
                Log.Info($"  Name='{n ?? "?"}'  Device='{d ?? "?"}'  Id={g}");
                if (pub is null && IsMatch(n, d, g, publicName, publicId)) pub = c;
                if (prv is null && IsMatch(n, d, g, privateName, privateId)) prv = c;
            }

            if (pub is null) { message = $"Public (Wi-Fi) connection '{publicName}' not found by ICS."; return false; }
            if (prv is null) { message = $"Private (Ethernet) connection '{privateName}' not found by ICS."; return false; }
            if (ReferenceEquals(pub, prv)) { message = "Public and private resolved to the SAME connection."; return false; }

            // Clear any existing sharing first (handles "already shared" and
            // the one-shared-connection-at-a-time Windows limit).
            foreach (object? o in every)
            {
                if (o is not INetConnection c) continue;
                try
                {
                    var cfg = mgr.get_INetSharingConfigurationForINetConnection(c);
                    rcws.Add(cfg);
                    if (cfg.SharingEnabled)
                    {
                        var (n, _, _) = Describe(mgr, c);
                        Log.Step($"Disabling old sharing on '{n}'...");
                        cfg.DisableSharing();
                        Thread.Sleep(800);
                    }
                }
                catch (Exception ex) { Log.Warn("DisableSharing note: " + ex.Message); }
            }

            Log.Step($"Enabling ICS: public='{publicName}' private='{privateName}'...");
            var pubCfg = mgr.get_INetSharingConfigurationForINetConnection(pub);
            rcws.Add(pubCfg);
            pubCfg.EnableSharing(SharingConnectionType.Public);
            Thread.Sleep(1500);

            var prvCfg = mgr.get_INetSharingConfigurationForINetConnection(prv);
            rcws.Add(prvCfg);
            prvCfg.EnableSharing(SharingConnectionType.Private);
            Thread.Sleep(1500);

            // Verify both ends actually report sharing.
            bool pubOk = SafeEnabled(mgr, pub, SharingConnectionType.Public, rcws);
            bool prvOk = SafeEnabled(mgr, prv, SharingConnectionType.Private, rcws);
            if (pubOk && prvOk)
            {
                message = "ICS enabled.";
                return true;
            }
            message = $"ICS partially applied (public={pubOk}, private={prvOk}).";
            return false;
        }
        catch (COMException ex)
        {
            message = $"ICS COM error 0x{ex.ErrorCode:X8}: {ex.Message}. " +
                      "Fallback: open ncpa.cpl > Wi-Fi adapter > Properties > Sharing tab > " +
                      "check 'Allow other network users...' and pick the Ethernet adapter.";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
        finally
        {
            foreach (var r in rcws)
                try { Marshal.ReleaseComObject(r); } catch { }
        }
    }

    private static bool SafeEnabled(INetSharingManager mgr, INetConnection c,
                                    SharingConnectionType expected, List<object> rcws)
    {
        try
        {
            var cfg = mgr.get_INetSharingConfigurationForINetConnection(c);
            rcws.Add(cfg);
            return cfg.SharingEnabled && cfg.SharingConnectionType == expected;
        }
        catch { return false; }
    }

    public static bool TryDisable(out string message)
    {
        message = "";
        if (!Sys.IsWindows()) { message = "ICS requires Windows."; return false; }
        INetSharingManager? mgr = null;
        var rcws = new List<object>();
        try
        {
            mgr = (INetSharingManager)new NetSharingManager();
            rcws.Add(mgr);
            if (!mgr.SharingInstalled) { message = "ICS not installed; nothing to disable."; return true; }
            var every = mgr.get_EnumEveryConnection();
            rcws.Add(every);
            int count = 0;
            int failures = 0;
            foreach (object? o in every)
            {
                if (o is not INetConnection c) continue;
                rcws.Add(c);
                try
                {
                    var cfg = mgr.get_INetSharingConfigurationForINetConnection(c);
                    rcws.Add(cfg);
                    if (cfg.SharingEnabled)
                    {
                        var (n, _, _) = Describe(mgr, c);
                        Log.Step($"Disabling sharing on '{n}'...");
                        cfg.DisableSharing();
                        count++;
                        Thread.Sleep(500);
                    }
                }
                catch (Exception ex) { failures++; Log.Warn("DisableSharing note: " + ex.Message); }
            }
            message = $"Disabled sharing on {count} connection(s); {failures} failure(s).";
            return failures == 0;
        }
        catch (Exception ex)
        {
            message = "Disable ICS failed: " + ex.Message;
            return false;
        }
        finally
        {
            foreach (var r in rcws)
                try { Marshal.ReleaseComObject(r); } catch { }
        }
    }

    public static List<IcsEntry> GetStatus() => GetStatus(out _);

    public static List<IcsEntry> GetStatus(out string? error)
    {
        error = null;
        var result = new List<IcsEntry>();
        if (!Sys.IsWindows()) return result;
        INetSharingManager? mgr = null;
        var rcws = new List<object>();
        try
        {
            mgr = (INetSharingManager)new NetSharingManager();
            rcws.Add(mgr);
            if (!mgr.SharingInstalled) return result;
            var every = mgr.get_EnumEveryConnection();
            rcws.Add(every);
            foreach (object? o in every)
            {
                if (o is not INetConnection c) continue;
                rcws.Add(c);
                var (n, d, _) = Describe(mgr, c);
                bool en = false;
                string kind = "-";
                try
                {
                    var cfg = mgr.get_INetSharingConfigurationForINetConnection(c);
                    rcws.Add(cfg);
                    en = cfg.SharingEnabled;
                    if (en) kind = cfg.SharingConnectionType == SharingConnectionType.Public ? "PUBLIC" : "PRIVATE";
                }
                catch (Exception ex) { error = ex.Message; }
                result.Add(new IcsEntry(n ?? "?", d ?? "?", en, kind));
            }
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            foreach (var r in rcws)
                try { Marshal.ReleaseComObject(r); } catch { }
        }
        return result;
    }
}
