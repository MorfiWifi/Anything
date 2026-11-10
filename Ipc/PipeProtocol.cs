using System.Collections.Generic;

// JSON-lines protocol over the named pipe: one request line, one response
// line, then disconnect. Kept trivial on purpose — no framing beyond newline.
internal static class PipeProtocol
{
    public const string PipeName = "WifiShare.Control.v1";
}

internal sealed class PipeRequest
{
    public string Cmd { get; set; } = "";
    public LanShareConfig? Lan { get; set; }
    public SubnetConfig? Subnet { get; set; }
}

internal sealed class ServiceStatus
{
    public bool LanActive { get; set; }
    public bool SubnetActive { get; set; }
    public bool AutoStartEnabled { get; set; }
    public LanState? Lan { get; set; }
    public List<string> AccessUrls { get; set; } = new();
    public string? SubnetSummary { get; set; }
    public string LogPath { get; set; } = "";
    public string Version { get; set; } = "";
}

internal sealed class PipeResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public ServiceStatus? Status { get; set; }
    public LanResult? Lan { get; set; }
    public SubnetResult? Subnet { get; set; }
    public List<DhcpLeaseRecord>? Leases { get; set; }
}