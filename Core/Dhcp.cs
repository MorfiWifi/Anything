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
// DHCP — single-client address assignment for the WinNAT subnet.
// A wildcard bind receives initial broadcasts; IP_PKTINFO restricts input to
// the selected adapter and IP_UNICAST_IF selects the outgoing Windows interface.
// ICS must be off. An exclusive UDP 67 bind detects competing DHCP servers.
// ============================================================================
// Fixed configuration for one DHCP scope (validated in the constructor).
internal sealed class DhcpScope
{
    public IPAddress ServerIp { get; }
    public IPAddress Mask { get; }
    public IPAddress Router { get; }
    public List<IPAddress> Dns { get; }
    public string Domain { get; }
    public IPAddress PoolStart { get; }
    public IPAddress PoolEnd { get; }
    public int LeaseSeconds { get; }
    public IPAddress Broadcast { get; }

    public DhcpScope(IPAddress serverIp, IPAddress mask, IPAddress router,
                     List<IPAddress> dns, string domain,
                     IPAddress poolStart, IPAddress poolEnd, int leaseSeconds)
    {
        if (dns is null || dns.Count == 0) throw new ArgumentException("At least one DNS server is required.");
        if (leaseSeconds < 60) throw new ArgumentException("Lease time must be >= 60 seconds.");
        ServerIp = serverIp; Mask = mask; Router = router;
        Dns = dns; Domain = domain ?? "";
        PoolStart = poolStart; PoolEnd = poolEnd; LeaseSeconds = leaseSeconds;

        if (dns.Count > 63 || Encoding.ASCII.GetByteCount(Domain) > 253)
            throw new ArgumentException("DHCP DNS list or domain is too long.");
        foreach (var address in dns.Append(router)) DhcpPacket.ToUInt(address);
        uint m = DhcpPacket.ToUInt(mask);
        uint inverse = ~m;
        if (m == 0 || (inverse & (inverse + 1)) != 0)
            throw new ArgumentException("DHCP subnet mask must be contiguous and nonzero.");
        uint net = DhcpPacket.ToUInt(serverIp) & m;
        Broadcast = DhcpPacket.ToIp(net | ~m);
        // Pool must be a sane range inside our own subnet.
        if (DhcpPacket.ToUInt(poolStart) > DhcpPacket.ToUInt(poolEnd))
            throw new ArgumentException("DHCP pool start must be <= pool end.");
        foreach (var p in new[] { poolStart, poolEnd })
            if ((DhcpPacket.ToUInt(p) & m) != net)
                throw new ArgumentException($"Pool address {p} is outside {serverIp}/{PrefixOf(mask)}.");
    }

    private static int PrefixOf(IPAddress mask)
    {
        // Count the 1-bits: 255.255.255.0 -> 24.
        uint m = DhcpPacket.ToUInt(mask);
        int n = 0;
        while (m != 0) { n += (int)(m & 1); m >>= 1; }
        return n;
    }
}

// One granted lease, persisted to dhcp-leases.json so restarts are stable.
internal sealed class DhcpLeaseRecord
{
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime ExpiryUtc { get; set; }
    public string HostName { get; set; } = "";
}

// Raw DHCP packet codec: 236-byte header + magic cookie + TLV options.
internal static class DhcpPacket
{
    // DHCP message types (option 53).
    public const byte Discover = 1, Offer = 2, Request = 3, Decline = 4;
    public const byte Ack = 5, Nak = 6, Release = 7, Inform = 8;

    public sealed class Message
    {
        public byte Op;                 // 1 = BOOTREQUEST (client -> server)
        public uint Xid;                // transaction id, echoed in replies
        public ushort Flags;            // bit 15: client wants broadcast reply
        public IPAddress CIAddr = IPAddress.Any;  // client IP (renewals)
        public byte[] ChAddr = Array.Empty<byte>(); // client MAC (hlen bytes)
        public Dictionary<byte, byte[]> Options = new();
        public byte MsgType;            // option 53, 0 when absent
    }

    // Big-endian IP <-> uint helpers (pool arithmetic needs integers).
    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) throw new ArgumentException("IPv4 only.");
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress ToIp(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    private static uint ReadU32(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static void WriteU32(List<byte> out_, uint v)
    {
        out_.Add((byte)(v >> 24)); out_.Add((byte)(v >> 16));
        out_.Add((byte)(v >> 8)); out_.Add((byte)v);
    }

    // Parse + validate. Returns null for anything that is not a well-formed
    // client DHCP packet (wrong size, bad magic cookie, not BOOTREQUEST...).
    public static Message? Parse(byte[] buf, int len)
    {
        if (buf is null || len < 240 || buf.Length < len) return null;
        if (buf[0] != 1 || buf[1] != 1 || buf[2] != 6) return null; // op/htype/hlen
        if (ReadU32(buf, 24) != 0) return null; // dedicated LAN only; no DHCP relays
        if (buf[236] != 99 || buf[237] != 130 || buf[238] != 83 || buf[239] != 99) return null;
        var m = new Message
        {
            Op = buf[0],
            Xid = ReadU32(buf, 4),
            Flags = (ushort)((buf[10] << 8) | buf[11]),
            CIAddr = new IPAddress(new[] { buf[12], buf[13], buf[14], buf[15] }),
            ChAddr = buf.Skip(28).Take(6).ToArray(),
        };
        int i = 240;
        while (i < len)
        {
            byte code = buf[i++];
            if (code == 0) continue;   // pad
            if (code == 255) break;    // end
            if (i >= len) return null; // truncated length byte
            byte optLen = buf[i++];
            if (i + optLen > len) return null; // truncated value
            m.Options[code] = buf.Skip(i).Take(optLen).ToArray();
            i += optLen;
        }
        if (m.Options.TryGetValue(53, out var t) && t.Length == 1) m.MsgType = t[0];
        return m;
    }

    private static void AddOpt(List<byte> out_, byte code, byte[] value)
    {
        out_.Add(code); out_.Add((byte)value.Length);
        out_.AddRange(value);
    }

    // Build OFFER / ACK / NAK. Echoes xid + broadcast flag + chaddr so the
    // (still address-less) client recognises its own transaction.
    public static byte[] Build(byte msgType, Message req, IPAddress yiaddr,
                               DhcpScope scope, string? nakText = null)
    {
        var p = new List<byte>(300);
        p.Add(2); p.Add(1); p.Add(6); p.Add(0);            // op=reply, eth, maclen, hops
        WriteU32(p, req.Xid);                             // xid
        p.Add(0); p.Add(0);                               // secs
        p.Add((byte)(req.Flags >> 8)); p.Add((byte)req.Flags); // flags echo
        p.AddRange(req.CIAddr.GetAddressBytes());          // ciaddr echo
        p.AddRange(yiaddr.GetAddressBytes());              // yiaddr (0.0.0.0 for NAK/INFORM)
        p.AddRange(scope.ServerIp.GetAddressBytes());      // siaddr = us
        p.AddRange(new byte[4]);                           // giaddr = 0
        var ch = new byte[16];                             // chaddr padded to 16
        Array.Copy(req.ChAddr, ch, Math.Min(req.ChAddr.Length, 16));
        p.AddRange(ch);
        p.AddRange(new byte[64]);                          // sname (unused)
        p.AddRange(new byte[128]);                         // file (unused)
        p.Add(99); p.Add(130); p.Add(83); p.Add(99);       // magic cookie

        var sid = scope.ServerIp.GetAddressBytes();
        AddOpt(p, 53, new[] { msgType });
        AddOpt(p, 54, sid);
        if (msgType == Offer || msgType == Ack)
        {
            var tmp = new List<byte>();
            WriteU32(tmp, (uint)scope.LeaseSeconds); AddOpt(p, 51, tmp.ToArray());
            AddOpt(p, 1, scope.Mask.GetAddressBytes());
            AddOpt(p, 3, scope.Router.GetAddressBytes());
            var dns = new List<byte>();
            foreach (var d in scope.Dns) dns.AddRange(d.GetAddressBytes());
            AddOpt(p, 6, dns.ToArray());
            if (!string.IsNullOrEmpty(scope.Domain))
                AddOpt(p, 15, Encoding.ASCII.GetBytes(scope.Domain));
            AddOpt(p, 28, scope.Broadcast.GetAddressBytes());
            tmp.Clear(); WriteU32(tmp, (uint)(scope.LeaseSeconds / 2)); AddOpt(p, 58, tmp.ToArray());
            tmp.Clear(); WriteU32(tmp, (uint)(scope.LeaseSeconds * 7 / 8)); AddOpt(p, 59, tmp.ToArray());
        }
        else if (msgType == Nak && !string.IsNullOrEmpty(nakText))
        {
            AddOpt(p, 56, Encoding.ASCII.GetBytes(nakText)); // human-readable reason
        }
        p.Add(255);                                        // end
        while (p.Count < 300) p.Add(0);                    // BOOTP minimum size
        return p.ToArray();
    }
}
