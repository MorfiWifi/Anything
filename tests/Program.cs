using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

if (args.Contains("--nat-integration")) return RunNatIntegration();
if (args.Contains("--smb-integration")) return RunSmbIntegration();

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (ArgumentException) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}
AdapterSnapshot Adapter(string name, string description, NetworkInterfaceType type,
    string ip, string mask, bool dhcp = true, OperationalStatus status = OperationalStatus.Up) =>
    new(name, description, type, status, name, "", 1_000_000_000,
        new() { ip }, new() { mask }, new() { "10.10.3.1" }, new() { "10.10.3.1" }, dhcp);

var plan = new LanPlan(Defaults.HostIp, Defaults.ClientIp);
Check(Defaults.ShareUserName == "admin" && Defaults.SharePassword == "123456", "Requested simple SMB login defaults");
Reject(() => Sharing.ValidateShareCredentials("PC\\admin", "123456"), "Reject domain-qualified names when creating local account");
Reject(() => Sharing.ValidateShareCredentials("admin", ""), "Reject empty SMB password");
Reject(() => Sharing.ValidateShareCredentials("admin' ; exit", "123456"), "Reject invalid local-account characters");
var unusualPassword = "word'with$dollar;and space";
var script = Sharing.BuildShareScript(Path.Combine(Path.GetTempPath(), "Anything 'test'"), "Shared", "admin", unusualPassword);
Check(script.Contains(Sys.PwshQuote(unusualPassword)), "Quote password as PowerShell literal without expanding punctuation");
Check(!JsonSerializer.Serialize(new ShareState()).Contains("Password", StringComparison.OrdinalIgnoreCase),
    "Recovery state contains no SMB password");
var natNames = Enumerable.Range(0, 100).Select(_ => LanNat.CreateName()).ToArray();
Check(natNames.All(n => n.Length == 11 && n.StartsWith("WS-") && n[3..].All(Uri.IsHexDigit)),
    "Generated NAT names stay within the short format verified on Windows");
Check(natNames.Distinct().Count() == natNames.Length, "Independent sessions get distinct NAT names");
Check(plan.HostIp == "172.20.10.1" && plan.ClientIp == "172.20.10.185" && plan.Prefix == "172.20.10.0/24",
    "Laptop and required client have distinct addresses in the NAT subnet");
Reject(() => new LanPlan("172.20.10.185", "172.20.10.185"), "Reject duplicate host/client IP");
Reject(() => new LanPlan("172.20.10.1", "192.168.1.185"), "Reject client outside NAT subnet");
Reject(() => new LanPlan("172.20.10.0", "172.20.10.185"), "Reject network address as gateway");
Reject(() => new LanPlan("169.254.10.1", "169.254.10.185"), "Reject APIPA LAN plan");
Check(plan.Overlaps(Adapter("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, "172.20.0.2", "255.255.0.0")),
    "Detect overlap with a broader upstream network");
Check(!plan.Overlaps(Adapter("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, "10.10.3.63", "255.255.255.0")),
    "Allow a separate upstream subnet");
var apipa = Adapter("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, "169.254.247.54", "255.255.0.0", false);
Check(!Adapters.ShouldRestoreStatic(apipa), "Legacy APIPA snapshot restores to DHCP");
Check(!Adapters.ShouldRestoreStatic(apipa with { IPv4 = new() { "10.0.0.2" }, DhcpEnabled = true }),
    "Leased IPv4 is not restored as static");
var physical = apipa with { IPv4 = new() { "10.0.0.2" } };
Check(Adapters.ShouldRestoreStatic(physical), "Valid manual IPv4 remains static on restore");
var virtualNic = physical with { Name = "VMnet8", Description = "VMware Virtual Ethernet Adapter" };
Check(Adapters.DetectEthernet(new() { virtualNic, physical }) == physical, "Do not choose VMware as client cable");
var wifi = Adapter("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, "10.10.3.63", "255.255.255.0");
Check(Adapters.DetectWifi(new() { wifi with { Status = OperationalStatus.Down } }) is null,
    "Do not use disconnected Wi-Fi with stale addresses");
Check(!DhcpServer.AcceptInterface(16, 20) && !DhcpServer.AcceptInterface(0, 20) && DhcpServer.AcceptInterface(20, 20),
    "Only selected Ethernet interface may receive leases");

byte[] Request(byte type, byte mac = 1, string? wanted = null, string? server = null)
{
    var b = new byte[240];
    b[0] = 1; b[1] = 1; b[2] = 6;
    b[4] = 0x12; b[5] = 0x34; b[6] = 0x56; b[7] = 0x78;
    b[10] = 0x80; b[28] = 2; b[33] = mac;
    b[236] = 99; b[237] = 130; b[238] = 83; b[239] = 99;
    var p = b.ToList(); p.AddRange(new byte[] { 53, 1, type });
    if (wanted != null) { p.AddRange(new byte[] { 50, 4 }); p.AddRange(IPAddress.Parse(wanted).GetAddressBytes()); }
    if (server != null) { p.AddRange(new byte[] { 54, 4 }); p.AddRange(IPAddress.Parse(server).GetAddressBytes()); }
    p.Add(255); return p.ToArray();
}
byte[] Option(byte[] packet, byte code)
{
    for (int i = 240; i < packet.Length;)
    {
        byte c = packet[i++]; if (c == 255) break; if (c == 0) continue;
        int n = packet[i++]; if (c == code) return packet.Skip(i).Take(n).ToArray(); i += n;
    }
    return Array.Empty<byte>();
}
var malformed = Request(DhcpPacket.Discover);
malformed[24] = 1;
Check(DhcpPacket.Parse(malformed, malformed.Length) is null, "Reject relayed DHCP on direct cable");
Check(DhcpPacket.Parse(new byte[239], 239) is null, "Reject truncated DHCP packet");

// Real UDP exchanges on unprivileged loopback ports. No system network changes.
using var portProbe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
portProbe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
int port = ((IPEndPoint)portProbe.LocalEndPoint!).Port;
portProbe.Close();
var scope = new DhcpScope(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), IPAddress.Loopback,
    new() { IPAddress.Parse("10.10.3.1") }, "mydomain.net",
    IPAddress.Parse("127.0.0.185"), IPAddress.Parse("127.0.0.185"), 3600);
string leaseFile = Path.Combine(Path.GetTempPath(), "Anything-Test-" + Guid.NewGuid().ToString("N") + ".json");
File.WriteAllText(leaseFile, JsonSerializer.Serialize(new[] {
    new DhcpLeaseRecord { Mac = "STALE", Ip = "172.20.10.185", ExpiryUtc = DateTime.UtcNow.AddDays(1) }
}));
try
{
    using var server = new DhcpServer(scope, leaseFile, port, 1068, false);
    Check(server.Start(out string message), "Start interface-filtered UDP listener: " + message);
    Check(server.GetLeases().Count == 0, "Discard leases from old subnet");
    using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    client.ReceiveTimeout = 2000;
    byte[] Exchange(byte[] packet)
    {
        client.SendTo(packet, new IPEndPoint(IPAddress.Loopback, port));
        byte[] buffer = new byte[1500]; int n = client.Receive(buffer); return buffer[..n];
    }
    byte[] offer = Exchange(Request(DhcpPacket.Discover, wanted: "127.0.0.185"));
    Check(Option(offer, 53).SequenceEqual(new byte[] { DhcpPacket.Offer }) &&
        new IPAddress(offer[16..20]).ToString() == "127.0.0.185", "DISCOVER receives required client address");
    Check(offer[4..8].SequenceEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }), "OFFER preserves transaction ID");
    byte[] ack = Exchange(Request(DhcpPacket.Request, wanted: "127.0.0.185", server: "127.0.0.1"));
    Check(Option(ack, 53).SequenceEqual(new byte[] { DhcpPacket.Ack }) && server.GetLeases().Count == 1,
        "REQUEST commits offered lease and returns ACK");
    Check(new IPAddress(Option(ack, 3)).Equals(IPAddress.Loopback) &&
        new IPAddress(Option(ack, 6)).ToString() == "10.10.3.1", "ACK advertises gateway and upstream DNS");
    byte[] wrongPool = Exchange(Request(DhcpPacket.Request, wanted: "127.0.0.99", server: "127.0.0.1"));
    Check(Option(wrongPool, 53).SequenceEqual(new byte[] { DhcpPacket.Nak }), "Never lease address outside pool");
    byte[] collision = Exchange(Request(DhcpPacket.Request, mac: 2, wanted: "127.0.0.185", server: "127.0.0.1"));
    Check(Option(collision, 53).SequenceEqual(new byte[] { DhcpPacket.Nak }), "Never give same address to second client");
    server.Stop();
    Check(!server.Running && server.Start(out _), "Stop releases socket for restart");
    Check(server.GetLeases().Count == 1, "Valid saved lease survives restart");
}
finally { File.Delete(leaseFile); }

// --- Service era: DPAPI, profile, pipe protocol, LAN validation, GUID fallback ---
string secretText = "p@ss'with$dollar;and space";
string protectedBlob = Secret.Protect(secretText);
Check(Secret.Unprotect(protectedBlob) == secretText, "DPAPI machine-scope round-trip preserves punctuation");
Check(protectedBlob != secretText && !protectedBlob.Contains(secretText), "DPAPI blob is not the plaintext");

var lanCfg = new LanShareConfig
{
    Folder = @"C:\TestShare", ShareName = "TestShare", ShareUserName = "admin",
    SharePassword = secretText, HttpPort = 9090, Http = false, Interfaces = new() { "Ethernet" },
};
var profile = ProfileStore.FromLanConfig(lanCfg, enabled: true);
string profileJson = JsonSerializer.Serialize(profile);
Check(!profileJson.Contains(secretText), "Saved profile never contains the plaintext SMB password");
var cfg2 = JsonSerializer.Deserialize<ShareProfile>(profileJson)!.ToLanConfig();
Check(cfg2.Folder == lanCfg.Folder && cfg2.ShareName == lanCfg.ShareName &&
    cfg2.SharePassword == secretText && cfg2.HttpPort == 9090 && !cfg2.Http &&
    cfg2.Interfaces.SequenceEqual(lanCfg.Interfaces), "Profile JSON round-trip preserves the full share intent");
Check(new ShareProfile().GetPassword() == Defaults.SharePassword, "Profile without a saved password falls back to the default");

Reject(() => LanShareEngine.ValidateConfig(new LanShareConfig { HttpPort = 0 }), "Reject LAN HTTP port 0");
Reject(() => LanShareEngine.ValidateConfig(new LanShareConfig { HttpPort = 65536 }), "Reject LAN HTTP port 65536");
Reject(() => LanShareEngine.ValidateConfig(new LanShareConfig { SharePassword = "" }), "Reject LAN config with empty SMB password");
LanShareEngine.ValidateConfig(new LanShareConfig { HttpPort = 65535 });
Check(true, "Accept LAN config with valid port and credentials");

Check(Adapters.FindById(null) is null && Adapters.FindById("") is null, "Adapter lookup by empty GUID is null");
Check(Adapters.CurrentName("{00000000-0000-0000-0000-000000000000}", "Saved Name") == "Saved Name",
    "Unknown adapter GUID falls back to the saved name");
Check(!Adapters.IsPresent(null, "Anything-Missing-" + Guid.NewGuid().ToString("N")), "Missing adapter is not present");
Check(LanShareEngine.GetAccessUrls(new LanState
    { Interfaces = new() { "Anything-Missing-" + Guid.NewGuid().ToString("N") } }).Count == 0,
    "No access URLs for vanished interfaces");

var request2 = JsonSerializer.Deserialize<PipeRequest>(JsonSerializer.Serialize(new PipeRequest { Cmd = "share", Lan = lanCfg }))!;
Check(request2.Cmd == "share" && request2.Lan!.SharePassword == secretText && request2.Lan.Interfaces[0] == "Ethernet",
    "Pipe request survives JSON round-trip");
var response2 = JsonSerializer.Deserialize<PipeResponse>(JsonSerializer.Serialize(new PipeResponse
    { Ok = true, Status = new ServiceStatus { LanActive = true, AccessUrls = new() { @"\\1.2.3.4\Shared" } } }))!;
Check(response2.Ok && response2.Status!.LanActive && response2.Status.AccessUrls[0] == @"\\1.2.3.4\Shared",
    "Pipe response survives JSON round-trip");

// Live pipe round-trip only when the real service is absent: the pipe name is
// fixed, so a running service would make the peer nondeterministic. The
// commands exercised here are read-only or rejected before any engine call.
if (PipeClient.Send(new PipeRequest { Cmd = "ping" }, 500) is null)
{
    var pipeServer = new ControlPipeServer();
    pipeServer.Start();
    try
    {
        Check(PipeClient.Send(new PipeRequest { Cmd = "ping" }, PipeClient.QueryTimeoutMs) is { Ok: true, Message: "pong" },
            "Live control pipe: ping -> pong");
        Check(PipeClient.Send(new PipeRequest { Cmd = "status" }, PipeClient.QueryTimeoutMs) is { Ok: true, Status: not null },
            "Live control pipe: status answers");
        Check(PipeClient.Send(new PipeRequest { Cmd = "no-such-command" }, PipeClient.QueryTimeoutMs) is { Ok: false },
            "Live control pipe rejects unknown commands");
        Check(PipeClient.Send(new PipeRequest { Cmd = "share" }, PipeClient.QueryTimeoutMs) is { Ok: false },
            "Live control pipe rejects share without a config");
    }
    finally { pipeServer.Stop(); }
}

// Service logging mode: console disabled, file sink still written.
string logFile = Path.Combine(Path.GetTempPath(), "Anything-Test-" + Guid.NewGuid().ToString("N") + ".log");
string oldLogPath = Log.FilePath; bool oldConsole = Log.ConsoleEnabled;
try
{
    Log.FilePath = logFile; Log.ConsoleEnabled = false;
    Log.Info("service-mode log probe");
    Check(File.ReadAllText(logFile).Contains("service-mode log probe"), "Service log mode writes file without a console");
}
finally
{
    Log.FilePath = oldLogPath; Log.ConsoleEnabled = oldConsole;
    File.Delete(logFile);
}

// --- Isolated subnet (no uplink): config contract and DNS fallback chain ---
Check(!new SubnetConfig().HasUplink && !new SubnetConfig { UplinkAdapter = "  " }.HasUplink,
    "Empty uplink means isolated subnet");
Check(new SubnetConfig { UplinkAdapter = "Wi-Fi" }.HasUplink, "Named uplink enables routing mode");
var upAdapter = Adapter("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, "10.10.3.63", "255.255.255.0");
Check(SharingEngine.ResolveDnsServers(new SubnetConfig { Dns = { "1.1.1.1" } }, upAdapter).SequenceEqual(new[] { "1.1.1.1" }),
    "Explicit DNS wins over uplink inheritance");
Check(SharingEngine.ResolveDnsServers(new SubnetConfig(), upAdapter).SequenceEqual(new[] { "10.10.3.1" }),
    "DNS inherits from the uplink when unspecified");
Check(SharingEngine.ResolveDnsServers(new SubnetConfig(), null).SequenceEqual(new[] { Defaults.DnsPrimary }),
    "Isolated subnet falls back to the default DNS");
Console.WriteLine($"{passed} checks passed. No adapter, NAT, firewall or sharing settings modified.");
return 0;

// Explicit opt-in integration test of the production create/restore code.
// Requires elevation; temporarily enables forwarding and creates a NAT object.
// It never changes IP/DNS, firewall, shares, or another application's NAT.
static int RunNatIntegration()
{
    if (!Sys.IsAdmin()) { Console.Error.WriteLine("Administrator required for --nat-integration."); return 2; }
    string recovery = Path.Combine(AppContext.BaseDirectory, "nat-integration-state.json");
    if (File.Exists(recovery)) { Console.Error.WriteLine("An integration recovery snapshot already exists: " + recovery); return 2; }
    ShareState? state = null;
    bool ok = false;
    try
    {
        var all = Adapters.ListAll();
        var wifi = Adapters.DetectWifi(all) ?? throw new Exception("No connected Wi-Fi.");
        var ethernet = Adapters.DetectEthernet(all) ?? throw new Exception("No Ethernet.");
        LanNat.Preflight(wifi, ethernet, false);
        state = new ShareState
        {
            PublicAdapterName = wifi.Name, PrivateAdapterName = ethernet.Name,
            OrigPublicForwarding = LanNat.GetForwarding(wifi.Name),
            OrigPrivateForwarding = LanNat.GetForwarding(ethernet.Name),
            NatName = LanNat.CreateName(),
        };
        File.WriteAllText(recovery, JsonSerializer.Serialize(state));
        LanNat.Create(state, new LanPlan(Defaults.HostIp, Defaults.ClientIp));
        Console.WriteLine("PASS: production NAT creation and Active/subnet verification on Windows.");
        ok = true;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); }
    finally
    {
        if (state is not null && File.Exists(recovery))
        {
            if (LanNat.Restore(state))
            {
                File.Delete(recovery);
                Console.WriteLine("PASS: test NAT removed and original forwarding restored.");
            }
            else { ok = false; Console.Error.WriteLine("Restore failed. Recovery snapshot: " + recovery); }
        }
    }
    return ok ? 0 : 1;
}

// A separate local logon validates the password and avoids cached SMB mappings.
// Run only after explicitly setting up the default share/account.
static int RunSmbIntegration()
{
    if (!SmbTestLogon.LogonUser(Defaults.ShareUserName, Environment.MachineName,
            Defaults.SharePassword, 2, 0, out var token))
    {
        Console.Error.WriteLine("Network logon token failed: " + Marshal.GetLastWin32Error());
        return 1;
    }
    using (token)
    {
        try
        {
            WindowsIdentity.RunImpersonated(token, () =>
            {
                string file = @"\\127.0.0.1\" + Defaults.ShareName + @"\.anything-auth-test-" + Guid.NewGuid().ToString("N") + ".tmp";
                bool created = false;
                try
                {
                    File.WriteAllText(file, "Authenticated SMB read-write test");
                    created = true;
                    if (File.ReadAllText(file) != "Authenticated SMB read-write test") throw new Exception("SMB content mismatch.");
                    File.Delete(file);
                    created = false;
                }
                finally { if (created) File.Delete(file); }
            });
            Console.WriteLine("PASS: default SMB credentials authenticate and can create, read and delete a file over SMB.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

internal static class SmbTestLogon
{
    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LogonUser(string user, string domain, string password,
        int logonType, int provider, out SafeAccessTokenHandle token);
}
