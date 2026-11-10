using System;
using System.Collections.Generic;

// Fully-specified subnet-mode start request. The interactive CLI gathers these
// values via prompts; the control pipe delivers them from remote clients.
// The engine never reads the console.
internal sealed class SubnetConfig
{
    public string UplinkAdapter { get; set; } = "";
    public string DownlinkAdapter { get; set; } = "";
    public string HostIp { get; set; } = Defaults.HostIp;
    public string ClientIp { get; set; } = Defaults.ClientIp;
    public List<string> Dns { get; set; } = new(); // empty = inherit from the uplink
    public string DnsSuffix { get; set; } = Defaults.DnsSuffix;
    public bool Dhcp { get; set; } = true;
    public int LeaseHours { get; set; } = Defaults.LeaseHours;
    public string SharePath { get; set; } = Defaults.SharePath;
    public string ShareName { get; set; } = Defaults.ShareName;
    public string ShareUserName { get; set; } = Defaults.ShareUserName;
    public string SharePassword { get; set; } = Defaults.SharePassword;
    public bool Http { get; set; } = true;
    public int HttpPort { get; set; } = Defaults.HttpPort;
}

// Outcome of a subnet start, with everything the client needs to print help.
internal sealed class SubnetResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string HostIp { get; set; } = "";
    public string ClientIp { get; set; } = "";
    public string Dns1 { get; set; } = "";
    public string Dns2 { get; set; } = "";
    public string DnsSuffix { get; set; } = "";
    public string ShareName { get; set; } = "";
    public int HttpPort { get; set; }
    public bool SmbOn { get; set; }
    public bool HttpOn { get; set; }
    public bool DhcpOn { get; set; }
}