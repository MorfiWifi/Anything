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
// Sys — thin wrapper around external tools (netsh, powershell, net, sc,
// icacls). Using the OS tools keeps us dependency-free (no NuGet) and is
// the most reliable way to drive Windows networking from .NET.
// ============================================================================
internal sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

internal static class Sys
{
    // Run any process, capture stdout/stderr, never throw.
    public static CommandResult Run(string fileName, string arguments, int timeoutMs = 90_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return new CommandResult(-1, "", "could not start process");
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return new CommandResult(-1, "", "timed out");
            }
            return new CommandResult(p.ExitCode, outTask.Result.Trim(), errTask.Result.Trim());
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, "", ex.Message);
        }
    }

    public static CommandResult Netsh(string args) => Run("netsh", args);

    // Quote a PowerShell single-quoted string safely (' -> '').
    public static string PwshQuote(string s) => "'" + s.Replace("'", "''") + "'";

    // Run PowerShell via -EncodedCommand (Base64 UTF-16) so we never fight
    // with nested quoting of interface names like "Wi-Fi".
    public static CommandResult PowerShell(string script, int timeoutMs = 90_000)
    {
        if (!OperatingSystem.IsWindows())
            return new CommandResult(-1, "", "PowerShell is only available on Windows.");
        script = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; try { " +
                 script + "; exit 0 } catch { " +
                 "[Console]::Error.WriteLine(('{0} [ErrorId: {1}]' -f $_.Exception.Message, $_.FullyQualifiedErrorId)); exit 1 }";
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Run("powershell",
            "-NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + b64,
            timeoutMs);
    }

    public static bool IsWindows() => OperatingSystem.IsWindows();

    public static bool IsAdmin()
    {
        if (!IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Relaunch ourselves elevated ("runas" verb). Returns true if relaunch ok.
    public static bool RelaunchAsAdmin(string args)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                Verb = "runas",          // triggers the UAC prompt
                UseShellExecute = true,  // required for "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Elevation failed: " + ex.Message);
            return false;
        }
    }
}
