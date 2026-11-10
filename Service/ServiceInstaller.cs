using System;
using System.IO;

// ============================================================================
// ServiceInstaller — one-time elevated setup via sc.exe (in-box on every
// Windows edition). The service runs as LocalSystem with auto-start, so it
// comes up at boot regardless of who signs in; afterwards any signed-in user
// controls it through the named pipe.
// ============================================================================
internal static class ServiceInstaller
{
    public const string ServiceName = "WifiShare";

    public static bool Exists()
    {
        var r = Sys.Run("sc", $"query {ServiceName}", 30_000);
        return r.Success;
    }

    public static bool Install()
    {
        if (!Sys.IsAdmin())
        {
            Log.Error("Installing the service requires Administrator rights (one time only).");
            return false;
        }
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) ||
            Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("Install from the published WifiShare.exe, not through 'dotnet run'/'dotnet <dll>'.");
            return false;
        }
        if (Exists())
        {
            Log.Error($"Service '{ServiceName}' is already installed. Uninstall first to replace the binary.");
            return false;
        }
        AppData.EnsureDir();
        // binPath value must keep literal quotes around the exe for the SCM.
        var create = Sys.Run("sc",
            $"create {ServiceName} binPath= \"\\\"{exe}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"WifiShare Folder Sharing\"");
        if (!create.Success)
        {
            Log.Error("sc create failed: " + create.StdOut + " " + create.StdErr);
            return false;
        }
        Sys.Run("sc", $"description {ServiceName} \"Shares folders over SMB/HTTP on the current network; optional dedicated-subnet NAT/DHCP for a cabled client.\"");
        // Restart on crash: 5 s, 10 s, then 30 s; failure count resets daily.
        Sys.Run("sc", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Log.Ok($"Service '{ServiceName}' installed (LocalSystem, auto-start): {exe}");
        var start = Sys.Run("sc", $"start {ServiceName}", 60_000);
        if (!start.Success && !start.StdOut.Contains("START_PENDING"))
        {
            Log.Error("Service installed but did not start: " + start.StdOut + " " + start.StdErr);
            return false;
        }
        Log.Ok("Service started. Control it from any terminal: WifiShare status | share | stop.");
        return true;
    }

    public static bool Uninstall()
    {
        if (!Sys.IsAdmin())
        {
            Log.Error("Uninstalling the service requires Administrator rights.");
            return false;
        }
        if (!Exists())
        {
            Log.Info($"Service '{ServiceName}' is not installed.");
            return true;
        }
        // Stop-Service waits for the graceful teardown (share/rules removed).
        Sys.PowerShell($"Stop-Service -Name {ServiceName} -Force -ErrorAction SilentlyContinue");
        var delete = Sys.Run("sc", $"delete {ServiceName}");
        if (!delete.Success)
        {
            Log.Error("sc delete failed: " + delete.StdOut + " " + delete.StdErr);
            return false;
        }
        Log.Ok($"Service '{ServiceName}' removed. Settings in {AppData.Dir} were kept.");
        return true;
    }
}