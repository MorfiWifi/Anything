using System.Collections.Generic;

// LAN-mode start request: share a folder on the networks this machine is
// already on. No adapter addressing, NAT or DHCP is touched.
internal sealed class LanShareConfig
{
    public string Folder { get; set; } = Defaults.SharePath;
    public string ShareName { get; set; } = Defaults.ShareName;
    public string ShareUserName { get; set; } = Defaults.ShareUserName;
    public string SharePassword { get; set; } = Defaults.SharePassword;
    public bool Http { get; set; } = true;
    public int HttpPort { get; set; } = Defaults.HttpPort;
    // Empty = every physical, connected, addressed adapter (Wi-Fi + LAN + ...).
    public List<string> Interfaces { get; set; } = new();
}

internal sealed class LanResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Folder { get; set; } = "";
    public string ShareName { get; set; } = "";
    public string UserName { get; set; } = "";
    public bool HttpOn { get; set; }
    public int HttpPort { get; set; }
    public List<string> AccessUrls { get; set; } = new();
}