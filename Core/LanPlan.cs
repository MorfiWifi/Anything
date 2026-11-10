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
// LanPlan — one dedicated Ethernet client, with separate host/client addresses.
// WinNAT covers exactly this subnet; no off-subnet gateway is put on the host.
internal sealed class LanPlan
{
    public string HostIp { get; }
    public string ClientIp { get; }
    public string Prefix { get; }
    public const string Mask = "255.255.255.0";

    public LanPlan(string hostIp, string clientIp)
    {
        uint host = DhcpPacket.ToUInt(IPAddress.Parse(hostIp));
        uint client = DhcpPacket.ToUInt(IPAddress.Parse(clientIp));
        if (host == client) throw new ArgumentException("Laptop and client must have different IP addresses.");
        if ((host & 0xffffff00) != (client & 0xffffff00))
            throw new ArgumentException("Laptop and client must be in the same /24 subnet.");
        foreach (uint ip in new[] { host, client })
            if ((ip & 255) is 0 or 255 || (ip >> 24) is 0 or 127 or >= 224 ||
                (ip >> 16) == 0xa9fe)
                throw new ArgumentException("Use usable unicast addresses, not network, broadcast or link-local addresses.");
        HostIp = IPAddress.Parse(hostIp).ToString();
        ClientIp = IPAddress.Parse(clientIp).ToString();
        Prefix = DhcpPacket.ToIp(host & 0xffffff00) + "/24";
    }

    public bool Overlaps(AdapterSnapshot adapter)
    {
        uint target = DhcpPacket.ToUInt(IPAddress.Parse(HostIp)) & 0xffffff00;
        for (int i = 0; i < adapter.IPv4.Count; i++)
        {
            if (i >= adapter.Masks.Count || !IPAddress.TryParse(adapter.Masks[i], out var mask)) continue;
            uint other = DhcpPacket.ToUInt(IPAddress.Parse(adapter.IPv4[i]));
            uint commonMask = DhcpPacket.ToUInt(mask) & 0xffffff00;
            if ((target & commonMask) == (other & commonMask)) return true;
        }
        return false;
    }
}
