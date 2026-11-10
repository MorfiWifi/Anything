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
// Defaults — every spec value from the task lives here in one place.
// ============================================================================
internal static class Defaults
{
    public const string HostIp = "172.20.10.1";
    public const string ClientIp = "172.20.10.185";
    public const string Mask = "255.255.255.0";
    public const int PrefixLength = 24;
    public const string Gateway = ""; // The host's default route belongs to Wi-Fi.
    public const string DnsPrimary = "172.16.61.20";
    public const string DnsSecondary = "172.16.61.10";
    public const string DnsSuffix = "mydomain.net";
    public const string TargetMac = "88:11:1E:34:F6:41";
    // Plug-and-play DHCP pool served to the client (must sit inside 172.20.10.0/24
    // and must NOT contain the host IP .185). Lease time in hours.
    public const string PoolStart = ClientIp;
    public const string PoolEnd = ClientIp;
    public const int LeaseHours = 24;
    public const string SharePath = @"D:\SharedWithClient";
    public const string ShareName = "Shared";
    public const string ShareUserName = "admin";
    public const string SharePassword = "123456";
    public const int HttpPort = 8080;
    public const string ExpectedAdapterHint = "Realtek PCIe GbE Family Controller";
}

// Runtime-tweakable settings (menu option 3 edits these).
internal static class Settings
{
    public static string SharePath = Defaults.SharePath;
    public static string ShareName = Defaults.ShareName;
    public static string ShareUserName = Defaults.ShareUserName;
    public static string SharePassword = Defaults.SharePassword;
    public static string DnsSuffix = Defaults.DnsSuffix;
    public static int HttpPort = Defaults.HttpPort;
    public static bool StartHttpServer = true;
    public static bool SpoofMac = false;
    // Plug-and-play DHCP (option 5 in subst: offered on Start, see DhcpServer).
    public static bool StartDhcp = true;
    public static string PoolStart = Defaults.PoolStart;
    public static string PoolEnd = Defaults.PoolEnd;
    public static int LeaseHours = Defaults.LeaseHours;
}
