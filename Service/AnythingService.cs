using System;
using System.Threading;
using System.Threading.Tasks;

// ============================================================================
// AnythingService — service lifetime. Start brings up the control pipe and
// re-applies the saved profile (boot auto-start); Stop tears sharing down so
// the machine is left clean. The profile stays Enabled across service stops,
// so the next boot shares again; the pipe 'stop' command disables it.
// ============================================================================
internal static class AnythingService
{
    private static ControlPipeServer? _pipe;
    private static Task? _autoStart;
    private static CancellationTokenSource? _lifetime;

    public static void Start()
    {
        AppData.EnsureDir();
        Log.ConsoleEnabled = false; // session 0 has no console
        Log.FilePath = AppData.LogPath;
        Log.Info($"Anything service starting (PID {Environment.ProcessId}).");

        _lifetime = new CancellationTokenSource();
        _pipe = new ControlPipeServer();
        _pipe.Start();

        var profile = ProfileStore.Load();
        if (profile?.Enabled == true)
        {
            Log.Info("Boot auto-start: re-applying the saved LAN share.");
            var lifetime = _lifetime;
            _autoStart = Task.Run(() =>
            {
                try
                {
                    if (lifetime.IsCancellationRequested) return;
                    var response = ControlPipeServer.Execute(new PipeRequest { Cmd = "share", Lan = profile.ToLanConfig() });
                    if (!response.Ok) Log.Error("Boot auto-start failed: " + response.Message);
                }
                catch (Exception ex) { Log.Error("Boot auto-start failed: " + ex.Message); }
            });
        }
        Log.Ok("Anything service running.");
    }

    public static void Stop()
    {
        Log.Info("Anything service stopping.");
        _lifetime?.Cancel();
        _pipe?.Stop();
        _pipe = null;
        // Let a queued/running auto-start finish before tearing the engines down,
        // otherwise it could re-share after we stop.
        try { _autoStart?.Wait(15_000); } catch { }
        _autoStart = null;
        lock (ControlPipeServer.EngineGate)
        {
            try { LanShareEngine.Stop(); }
            catch (Exception ex) { Log.Error("LAN stop during shutdown: " + ex.Message); }
            try { SharingEngine.StopSubnet(); }
            catch (Exception ex) { Log.Error("Subnet stop during shutdown: " + ex.Message); }
        }
        _lifetime?.Dispose();
        _lifetime = null;
        Log.Ok("Anything service stopped.");
    }
}