using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// ============================================================================
// ControlPipeServer — local command channel for the service. The DACL is the
// security boundary: any signed-in user (Interactive) may control sharing,
// per the product decision for personal machines. The pipe is local-only.
// Engine operations are serialized: one sharing change at a time.
// ============================================================================
internal sealed class ControlPipeServer
{
    // Also held by AnythingService.Stop so shutdown cannot race a client command.
    internal static readonly object EngineGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log.Info($"Control pipe ready: \\\\.\\pipe\\{PipeProtocol.PipeName}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(3000); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeProtocol.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (Exception ex)
            {
                Log.Warn("Pipe accept failed: " + ex.Message);
                pipe?.Dispose();
                // A non-SYSTEM creator (tests) lacks CreateNewInstance on the
                // existing pipe object until the last instance closes; back off
                // instead of spinning.
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(pipe!, ct), CancellationToken.None);
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            PipeResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<PipeRequest>(line ?? "");
                response = request is null || request.Cmd.Length == 0
                    ? new PipeResponse { Ok = false, Message = "Bad request: empty request." }
                    : Execute(request);
            }
            catch (Exception ex)
            {
                response = new PipeResponse { Ok = false, Message = "Bad request: " + ex.Message };
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Pipe client error: " + ex.Message); }
        finally { pipe.Dispose(); }
    }

    // Single entry point for pipe clients and boot auto-start alike, so every
    // engine mutation is serialized behind one lock.
    internal static PipeResponse Execute(PipeRequest request)
    {
        lock (EngineGate)
        {
            try
            {
                switch (request.Cmd)
                {
                    case "ping":
                        return new PipeResponse { Ok = true, Message = "pong" };
                    case "status":
                        return new PipeResponse { Ok = true, Status = BuildStatus() };
                    case "share":
                    {
                        if (request.Lan is null) throw new ArgumentException("share requires a Lan config.");
                        LanResult result = LanShareEngine.Start(request.Lan);
                        if (result.Success)
                            ProfileStore.Save(ProfileStore.FromLanConfig(request.Lan, enabled: true));
                        return new PipeResponse { Ok = result.Success, Message = result.Message, Lan = result };
                    }
                    case "stop":
                    {
                        LanShareEngine.Stop();
                        SharingEngine.StopSubnet();
                        var profile = ProfileStore.Load();
                        if (profile is not null) { profile.Enabled = false; ProfileStore.Save(profile); }
                        return new PipeResponse { Ok = true, Message = "Sharing stopped." };
                    }
                    case "subnet-start":
                    {
                        if (request.Subnet is null) throw new ArgumentException("subnet-start requires a Subnet config.");
                        SubnetResult result = SharingEngine.StartSubnet(request.Subnet);
                        return new PipeResponse { Ok = result.Success, Message = result.Message, Subnet = result };
                    }
                    case "subnet-stop":
                    {
                        bool ok = SharingEngine.StopSubnet();
                        return new PipeResponse { Ok = ok, Message = ok ? "Subnet session stopped." : "Restore incomplete; see the service log." };
                    }
                    case "leases":
                        return new PipeResponse
                        {
                            Ok = true,
                            Leases = SharingEngine.Dhcp?.GetLeases() ?? new List<DhcpLeaseRecord>(),
                        };
                    default:
                        return new PipeResponse { Ok = false, Message = $"Unknown command '{request.Cmd}'." };
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Command '{request.Cmd}' failed: " + ex.Message);
                return new PipeResponse { Ok = false, Message = ex.Message };
            }
        }
    }

    private static string DescribeSubnet(ShareState s)
    {
        string path = s.PublicAdapterName.Length > 0
            ? $"{s.PublicAdapterName} -> {s.PrivateAdapterName}"
            : $"{s.PrivateAdapterName} (isolated, no uplink)";
        string nat = s.NatName ?? (s.IcsEnabledByUs ? "legacy ICS" : "none");
        return $"{path}, host {s.HostIp}, NAT {nat}";
    }

    private static ServiceStatus BuildStatus()
    {
        var profile = ProfileStore.Load();
        var lanState = LanStateStore.Load();
        var subnetState = StateStore.Load();
        return new ServiceStatus
        {
            LanActive = LanShareEngine.LanActive,
            SubnetActive = SharingEngine.SubnetActive,
            AutoStartEnabled = profile?.Enabled ?? false,
            Lan = lanState,
            AccessUrls = lanState is not null ? LanShareEngine.GetAccessUrls(lanState) : new List<string>(),
            SubnetSummary = subnetState is not null ? DescribeSubnet(subnetState) : null,
            LogPath = Log.FilePath,
            Version = typeof(ControlPipeServer).Assembly.GetName().Version?.ToString() ?? "",
        };
    }
}