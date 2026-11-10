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

// The server itself: one UDP socket, one background loop, in-memory leases
// with JSON persistence. Handles DISCOVER/OFFER, REQUEST/ACK/NAK,
// DECLINE, RELEASE and INFORM for a single-cable client setup.
internal sealed class DhcpServer : IDisposable
{
    private readonly DhcpScope _scope;
    private readonly int _listenPort;      // 67 in production
    private readonly int _peerPort;        // 68 in production
    private readonly bool _broadcastReplies; // false = unicast to sender (tests)
    private readonly string _leaseFile;
    private int _interfaceIndex;
    private Socket? _sock;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Dictionary<string, DhcpLeaseRecord> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ip, DateTime until)> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _declined = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    public bool Running { get; private set; }
    public string PoolSummary => $"{_scope.PoolStart} - {_scope.PoolEnd}";
    public string LeaseSummary => $"{_scope.LeaseSeconds / 3600} h lease, router {_scope.Router}, " +
                                  $"dns {string.Join(",", _scope.Dns)}, domain '{_scope.Domain}'";

    public DhcpServer(DhcpScope scope, string? leaseFile = null,
                      int listenPort = 67, int peerPort = 68, bool broadcastReplies = true)
    {
        _scope = scope;
        _leaseFile = leaseFile ?? AppData.LeasePath;
        _listenPort = listenPort; _peerPort = peerPort; _broadcastReplies = broadcastReplies;
    }

    public bool Start(out string message)
    {
        message = "";
        try
        {
            LoadLeases();
            Stop(); // idempotent
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sock = sock; // ensure failed setup is disposed as well
            // Exclusive bind: fail loudly on conflict instead of fighting another server.
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            sock.ReceiveTimeout = 1000; // lets the loop notice cancellation promptly
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(_scope.ServerIp)))
                ?? throw new InvalidOperationException("The DHCP server address is not assigned to a local adapter.");
            _interfaceIndex = nic.GetIPProperties().GetIPv4Properties()!.Index;
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            if (OperatingSystem.IsWindows())
                sock.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, // Windows IP_UNICAST_IF
                    IPAddress.HostToNetworkOrder(_interfaceIndex));
            // Address-less clients send to 255.255.255.255. A unicast-only bind
            // misses those requests. Packet information restricts input to LAN.
            sock.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Running = true;
            _loop = Task.Run(() => RecvLoop(sock, token));
            message = $"serving {PoolSummary} on {_scope.ServerIp}:{_listenPort}";
            return true;
        }
        catch (SocketException ex)
        {
            Stop();
            message = $"DHCP socket failed ({ex.SocketErrorCode}): {ex.Message}. " +
                      "Check for another DHCP server or Mobile hotspot holding UDP 67.";
            return false;
        }
        catch (Exception ex) { Stop(); message = ex.Message; return false; }
    }

    public void Stop()
    {
        Running = false;
        try { _cts?.Cancel(); } catch { }
        try { _sock?.Close(); } catch { }
        _sock = null;
        try { _loop?.Wait(2000); } catch { }
        _cts?.Dispose(); _cts = null;
    }

    public List<DhcpLeaseRecord> GetLeases()
    {
        lock (_gate)
        {
            PruneLocked();
            return _leases.Values
                .OrderBy(l => DhcpPacket.ToUInt(IPAddress.Parse(l.Ip)))
                .Select(l => new DhcpLeaseRecord { Mac = l.Mac, Ip = l.Ip, ExpiryUtc = l.ExpiryUtc, HostName = l.HostName })
                .ToList();
        }
    }

    // ---- receive loop -------------------------------------------------------
    private void RecvLoop(Socket socket, CancellationToken ct)
    {
        var buf = new byte[1500];
        while (!ct.IsCancellationRequested)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                SocketFlags flags = SocketFlags.None;
                n = socket.ReceiveMessageFrom(buf, 0, buf.Length, ref flags, ref remote, out var info);
                if (!AcceptInterface(info.Interface, _interfaceIndex)) continue;
                if (_listenPort == 67 && ((IPEndPoint)remote).Port != 68) continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { continue; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }
            catch { break; }
            try { Handle(buf, n, remote); }
            catch (Exception ex) { Log.Warn("DHCP handler error: " + ex.Message); }
        }
        Running = false;
    }

    internal static bool AcceptInterface(int received, int selected) => selected > 0 && received == selected;

    private void Handle(byte[] buf, int n, EndPoint remote)
    {
        var msg = DhcpPacket.Parse(buf, n);
        if (msg is null || msg.MsgType == 0) return; // not a client DHCP message
        string mac = MacKey(msg);
        string host = HostNameOf(msg);
        switch (msg.MsgType)
        {
            case DhcpPacket.Discover: OnDiscover(msg, mac, host, remote); break;
            case DhcpPacket.Request: OnRequest(msg, mac, host, remote); break;
            case DhcpPacket.Decline: OnDecline(msg, mac); break;
            case DhcpPacket.Release: OnRelease(msg, mac); break;
            case DhcpPacket.Inform: OnInform(msg, mac, remote); break;
        }
    }

    // Lease key = MAC (+ DHCP client-id option 61 when the client sends one).
    private static string MacKey(DhcpPacket.Message msg)
    {
        string mac = BitConverter.ToString(msg.ChAddr).Replace("-", "");
        if (msg.Options.TryGetValue(61, out var cid) && cid.Length > 0)
            mac += "|" + BitConverter.ToString(cid).Replace("-", "");
        return mac;
    }

    private static string HostNameOf(DhcpPacket.Message msg)
    {
        if (!msg.Options.TryGetValue(12, out var raw) || raw.Length == 0) return "";
        var s = Encoding.ASCII.GetString(raw);
        return new string(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
    }

    private static string? OptIp(DhcpPacket.Message msg, byte code)
    {
        if (msg.Options.TryGetValue(code, out var v) && v.Length == 4)
            return new IPAddress(v).ToString();
        return null;
    }

    // ---- DISCOVER ------------------------------------------------------------
    private void OnDiscover(DhcpPacket.Message msg, string mac, string host, EndPoint remote)
    {
        string? want = OptIp(msg, 50); // client may request a specific IP
        string? ip;
        lock (_gate)
        {
            PruneLocked();
            ip = FindForMacLocked(mac)                                   // stable IP: same client, same IP
                 ?? (want is not null && IsOfferableLocked(want, mac) ? want : null)
                 ?? AllocateNewLocked(mac);
            if (ip is not null) _offers[mac] = (ip, DateTime.UtcNow.AddSeconds(60));
        }
        if (ip is null) { Log.Warn($"DHCP pool exhausted for {mac} ({host})."); return; }
        Send(DhcpPacket.Offer, msg, IPAddress.Parse(ip), remote);
        Log.Info($"DHCP OFFER {ip} -> {mac} ({host})");
    }

    // ---- REQUEST --------------------------------------------------------------
    private void OnRequest(DhcpPacket.Message msg, string mac, string host, EndPoint remote)
    {
        string? serverId = OptIp(msg, 54);
        string? want = OptIp(msg, 50);
        bool fromOurs = serverId is null || serverId == _scope.ServerIp.ToString();
        if (!fromOurs) return; // SELECTING against another server: stay silent.

        lock (_gate)
        {
            PruneLocked();
            // Case A: SELECTING — client accepted our OFFER (server-id present).
            if (serverId is not null)
            {
                if (_offers.TryGetValue(mac, out var pend) &&
                    (want is null || want == pend.ip) && IsOfferableLocked(pend.ip, mac))
                { GrantLocked(mac, host, pend.ip); Send(DhcpPacket.Ack, msg, IPAddress.Parse(pend.ip), remote); return; }
                if (want is not null && IsOfferableLocked(want, mac))
                { GrantLocked(mac, host, want); Send(DhcpPacket.Ack, msg, IPAddress.Parse(want), remote); return; }
                Send(DhcpPacket.Nak, msg, IPAddress.Any, remote, "address unavailable");
                Log.Warn($"DHCP NAK -> {mac} ({host}): requested {want ?? pend.ip} unavailable");
                return;
            }
            // Case B: INIT-REBOOT — client reboots with a previous address.
            if (want is not null)
            {
                if (!InSubnet(want))
                {
                    // Wrong network (e.g. a stale 192.168.137.x lease from the ICS
                    // allocator): NAK so the client restarts discovery at once.
                    Send(DhcpPacket.Nak, msg, IPAddress.Any, remote, "wrong network");
                    Log.Info($"DHCP NAK -> {mac}: {want} is outside our subnet (re-discover)");
                    return;
                }
                if (FindForMacLocked(mac) == want || IsOfferableLocked(want, mac))
                { GrantLocked(mac, host, want); Send(DhcpPacket.Ack, msg, IPAddress.Parse(want), remote); return; }
                return; // taken by someone else: stay silent per RFC 2131.
            }
            // Case C: RENEW/REBIND — ciaddr set, client keeps using its address.
            string ci = msg.CIAddr.ToString();
            if (FindForMacLocked(mac) == ci)
            { GrantLocked(mac, host, ci); Send(DhcpPacket.Ack, msg, IPAddress.Parse(ci), remote); return; }
            // Unknown or expired binding: NAK to force a fast fresh DISCOVER.
            Send(DhcpPacket.Nak, msg, IPAddress.Any, remote, "lease expired");
            Log.Info($"DHCP NAK -> {mac}: no binding for {ci} (re-discover)");
        }
    }

    // ---- DECLINE / RELEASE / INFORM --------------------------------------------
    private void OnDecline(DhcpPacket.Message msg, string mac)
    {
        string? bad = OptIp(msg, 50);
        if (bad is null) return;
        lock (_gate)
        {
            foreach (var kv in _leases.Where(kv => kv.Value.Ip == bad).ToList()) _leases.Remove(kv.Key);
            foreach (var kv in _offers.Where(kv => kv.Value.ip == bad).ToList()) _offers.Remove(kv.Key);
            _declined[bad] = DateTime.UtcNow.AddMinutes(10); // quarantine, then reusable
            PersistLocked();
        }
        Log.Warn($"DHCP DECLINE from {mac}: {bad} quarantined 10 min (possible IP conflict)");
    }

    private void OnRelease(DhcpPacket.Message msg, string mac)
    {
        lock (_gate)
        {
            string ci = msg.CIAddr.ToString();
            if (_leases.TryGetValue(mac, out var l) && l.Ip == ci)
            {
                _leases.Remove(mac);
                PersistLocked();
                Log.Info($"DHCP RELEASE {ci} <- {mac}");
            }
        }
    }

    private void OnInform(DhcpPacket.Message msg, string mac, EndPoint remote)
    {
        // Client has an IP, only wants options: ACK with config, no lease.
        Send(DhcpPacket.Ack, msg, IPAddress.Any, remote);
        Log.Info($"DHCP INFORM -> {mac}: options sent");
    }

    // ---- lease bookkeeping (caller must hold _gate) ------------------------------
    private void PruneLocked()
    {
        foreach (var kv in _leases.Where(kv => kv.Value.ExpiryUtc <= DateTime.UtcNow).ToList())
            _leases.Remove(kv.Key);
        foreach (var kv in _offers.Where(kv => kv.Value.until <= DateTime.UtcNow).ToList())
            _offers.Remove(kv.Key);
        foreach (var kv in _declined.Where(kv => kv.Value <= DateTime.UtcNow).ToList())
            _declined.Remove(kv.Key);
    }

    private string? FindForMacLocked(string mac) =>
        _leases.TryGetValue(mac, out var l) && l.ExpiryUtc > DateTime.UtcNow ? l.Ip : null;

    private bool InSubnet(string ip)
    {
        uint m = DhcpPacket.ToUInt(_scope.Mask);
        return (DhcpPacket.ToUInt(IPAddress.Parse(ip)) & m) == (DhcpPacket.ToUInt(_scope.ServerIp) & m);
    }

    private bool IsOfferableLocked(string ip, string forMac)
    {
        if (!InSubnet(ip)) return false;
        uint v = DhcpPacket.ToUInt(IPAddress.Parse(ip));
        if (v < DhcpPacket.ToUInt(_scope.PoolStart) || v > DhcpPacket.ToUInt(_scope.PoolEnd)) return false;
        uint net = DhcpPacket.ToUInt(_scope.ServerIp) & DhcpPacket.ToUInt(_scope.Mask);
        uint bcast = DhcpPacket.ToUInt(_scope.Broadcast);
        if (v == net || v == bcast) return false;                 // network/broadcast
        if (ip == _scope.ServerIp.ToString()) return false;       // ourselves
        if (ip == _scope.Router.ToString() && ip != _scope.ServerIp.ToString()) return false;
        if (_declined.ContainsKey(ip)) return false;              // quarantined
        if (_leases.TryGetValue(forMac, out var own) && own.Ip == ip) return true;
        if (_leases.Values.Any(l => l.Ip == ip)) return false;    // held by another client
        // A pending offer to ANOTHER client blocks; our own pending offer
        // must never block us (SELECTING/INIT-REBOOT would NAK itself).
        if (_offers.Any(kv => kv.Value.ip == ip &&
            !kv.Key.Equals(forMac, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private string? AllocateNewLocked(string forMac)
    {
        uint from = DhcpPacket.ToUInt(_scope.PoolStart);
        uint to = DhcpPacket.ToUInt(_scope.PoolEnd);
        for (uint v = from; v <= to; v++)
        {
            string ip = DhcpPacket.ToIp(v).ToString();
            if (!IsOfferableLocked(ip, forMac)) continue;
            if (IsAliveOnWire(ip)) { Log.Warn($"DHCP: {ip} answers ping, skipping (in use)"); continue; }
            return ip;
        }
        return null;
    }

    // Address-conflict detection: never offer an IP that already answers.
    // A failed ping (firewalled host, no permission) counts as "free".
    private static bool IsAliveOnWire(string ip)
    {
        try
        {
            using var ping = new Ping();
            var r = ping.Send(IPAddress.Parse(ip), 350);
            return r?.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private void GrantLocked(string mac, string host, string ip)
    {
        _leases[mac] = new DhcpLeaseRecord
        {
            Mac = mac, Ip = ip, HostName = host,
            ExpiryUtc = DateTime.UtcNow.AddSeconds(_scope.LeaseSeconds),
        };
        _offers.Remove(mac);
        PersistLocked();
        Log.Ok($"DHCP ACK {ip} -> {mac} ({host}), lease {_scope.LeaseSeconds / 3600} h");
    }

    private void Send(byte msgType, DhcpPacket.Message req, IPAddress yiaddr,
                      EndPoint remote, string? nakText = null)
    {
        try
        {
            byte[] pkt = DhcpPacket.Build(msgType, req, yiaddr, _scope, nakText);
            // Production: broadcast so the address-less client hears us.
            // Tests run with broadcastReplies=false: unicast back to the sender.
            EndPoint dest = _broadcastReplies
                ? new IPEndPoint(IPAddress.Broadcast, _peerPort)
                : remote;
            _sock?.SendTo(pkt, dest);
        }
        catch (Exception ex) { Log.Warn("DHCP send failed: " + ex.Message); }
    }

    // ---- persistence -------------------------------------------------------------
    private void PersistLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_leaseFile)!);
            var json = JsonSerializer.Serialize(_leases.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_leaseFile, json, Encoding.UTF8);
        }
        catch { /* leases are best-effort persistence, never fatal */ }
    }

    private void LoadLeases()
    {
        try
        {
            if (!File.Exists(_leaseFile)) return;
            var list = JsonSerializer.Deserialize<List<DhcpLeaseRecord>>(
                File.ReadAllText(_leaseFile, Encoding.UTF8));
            if (list is null) return;
            lock (_gate)
            {
                foreach (var l in list)
                    if (!string.IsNullOrEmpty(l.Mac) && IPAddress.TryParse(l.Ip, out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork && IsOfferableLocked(l.Ip, l.Mac))
                        _leases[l.Mac] = l;
                PruneLocked();
            }
            Log.Info($"DHCP: loaded {list.Count} saved lease(s).");
        }
        catch { /* corrupt file: start fresh */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
