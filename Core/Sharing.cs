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
// Sharing — firewall rules, helper services, SMB share, HTTP firewall port.
// ============================================================================
internal static class Sharing
{
    public static void EnsureFirewallAndServices()
    {
        Log.Step("Starting file sharing and Network Discovery services...");
        // ConfigureLanRules installs interface-scoped rules; do not enable
        // global built-in groups on unrelated Wi-Fi or VPN networks.

        // Helper services file sharing depends on. 'sc config/start' works
        // on every Windows edition without extra APIs.
        foreach (var svc in new[] { "LanmanServer", "SSDPSRV", "fdPHost", "FDResPub" })
        {
            var c = Sys.Run("sc", $"config {svc} start= auto", 30_000);
            var s = Sys.Run("sc", $"start {svc}", 30_000);
            bool running = s.Success || s.StdOut.Contains("START_PENDING") ||
                           s.StdOut.Contains("RUNNING") || s.StdOut.Contains("1056");
            Log.Info($"Service {svc}: config={c.ExitCode} start={(running ? "running" : "FAILED: " + s.StdOut)}");
            if (!running) Log.Warn($"Service {svc} is not running; file sharing may fail.");
        }
    }

    public static void ValidateShareCredentials(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 20 ||
            userName.EndsWith('.') || userName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            throw new ArgumentException("Share username must be 1-20 letters, digits, dots, underscores or hyphens, and must not end in a dot.");
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("The SMB account needs a non-empty password.");
    }

    // Only a SID recorded by this app may have its password reset on later runs.
    // Never adopt an unrelated local account just because its name matches.
    internal static string BuildShareScript(string folder, string shareName, string userName, string password)
    {
        ValidateShareCredentials(userName, password);
        return $$"""
            $folder = {{Sys.PwshQuote(Path.GetFullPath(folder))}}
            $shareName = {{Sys.PwshQuote(shareName)}}
            $userName = {{Sys.PwshQuote(userName)}}
            $existing = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
            if ($existing -and [IO.Path]::GetFullPath($existing.Path).TrimEnd('\') -ne $folder.TrimEnd('\')) {
                throw 'The share name already points to a different folder. Choose a different share name.'
            }
            $registryPath = 'HKLM:\SOFTWARE\Anything\ShareAccounts'
            $knownSid = Get-ItemPropertyValue -LiteralPath $registryPath -Name $userName -ErrorAction SilentlyContinue
            $user = Get-LocalUser -Name $userName -ErrorAction SilentlyContinue
            if ($user -and $user.SID.Value -ne $knownSid) {
                throw "Local account '$userName' already exists and is not managed by Anything. Choose another share username in Settings."
            }
            $secret = ConvertTo-SecureString {{Sys.PwshQuote(password)}} -AsPlainText -Force
            try {
                if ($user) {
                    $isAdmin = Get-LocalGroupMember -SID 'S-1-5-32-544' | Where-Object { $_.SID.Value -eq $user.SID.Value }
                    if ($isAdmin) { throw 'The managed share account has administrator privileges. Choose a different standard account name.' }
                    Set-LocalUser -SID $user.SID -Password $secret -PasswordNeverExpires $true
                    Enable-LocalUser -SID $user.SID
                } else {
                    $user = New-LocalUser -Name $userName -Password $secret -PasswordNeverExpires -AccountNeverExpires -Description 'Anything SMB access account'
                    try {
                        New-Item -Path $registryPath -Force | Out-Null
                        New-ItemProperty -LiteralPath $registryPath -Name $userName -Value $user.SID.Value -PropertyType String -Force | Out-Null
                    } catch {
                        Remove-LocalUser -SID $user.SID
                        throw
                    }
                }
            } finally { $secret.Dispose() }
            $members = @(Get-LocalGroupMember -SID 'S-1-5-32-545')
            if (-not ($members | Where-Object { $_.SID.Value -eq $user.SID.Value })) {
                Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
            }
            $account = "$env:COMPUTERNAME\$userName"
            $admins = ([Security.Principal.SecurityIdentifier]'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value
            $everyone = ([Security.Principal.SecurityIdentifier]'S-1-1-0').Translate([Security.Principal.NTAccount]).Value
            [IO.Directory]::CreateDirectory($folder) | Out-Null
            & icacls.exe $folder /grant ("*" + $user.SID.Value + ':(OI)(CI)M') /T /Q
            if ($LASTEXITCODE -ne 0) { throw "NTFS access grant failed (icacls exit $LASTEXITCODE)." }
            if ($existing) {
                Grant-SmbShareAccess -Name $shareName -AccountName $account -AccessRight Change -Force | Out-Null
                Grant-SmbShareAccess -Name $shareName -AccountName $admins -AccessRight Full -Force | Out-Null
                Revoke-SmbShareAccess -Name $shareName -AccountName $everyone -Force | Out-Null
            } else {
                New-SmbShare -Name $shareName -Path $folder -ChangeAccess $account -FullAccess $admins -FolderEnumerationMode AccessBased | Out-Null
            }
            """;
    }

    public static bool EnsureShare(string folder, string shareName, string userName, string password)
    {
        try
        {
            var result = Sys.PowerShell(BuildShareScript(folder, shareName, userName, password));
            if (!result.Success)
            {
                Log.Error("SMB account/share setup failed: " + result.StdErr);
                return false;
            }
            Log.Ok($"SMB share ready: \\\\{Environment.MachineName}\\{shareName}; user {Environment.MachineName}\\{userName}");
            return true;
        }
        catch (Exception ex) { Log.Error("SMB setup failed: " + ex.Message); return false; }
    }

    public static void PrintShareLogin(string hostIp, string shareName, string userName, string password)
    {
        Console.WriteLine($"SMB username: {Environment.MachineName}\\{userName}");
        Console.WriteLine($"SMB password: {password}");
        Console.WriteLine($"Client command: net use \\\\{hostIp}\\{shareName} /user:{Environment.MachineName}\\{userName} *");
        Console.WriteLine("Enter the password at the prompt. If Windows cached another login, disconnect that share and reconnect.");
    }

    public static void RemoveShare(string shareName)
    {
        var r = Sys.Run("net", $"share {shareName} /delete /y", 30_000);
        Log.Info($"net share delete -> exit {r.ExitCode} (0 = removed, 2 = did not exist)");
    }

    public static string GetShareTable()
    {
        var r = Sys.Run("net", "share", 30_000);
        return r.Success ? r.StdOut : "(could not query shares)";
    }

    public static bool ConfigureLanRules(string adapterName, bool open) => ConfigureLanRules(new[] { adapterName }, open);

    // -InterfaceAlias accepts an array, so one rule set can cover several LANs.
    public static bool ConfigureLanRules(string[] adapterNames, bool open)
    {
        string script = "Get-NetFirewallRule -Name 'Anything-Smb','Anything-DiscoveryUdp','Anything-DiscoveryTcp','Anything-Ping' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; ";
        if (open)
        {
            string aliases = string.Join(",", adapterNames.Select(Sys.PwshQuote));
            string scope = $" -InterfaceAlias {aliases} -Profile Any -Direction Inbound -Action Allow -RemoteAddress LocalSubnet ";
            script += "New-NetFirewallRule -Name 'Anything-Smb' -DisplayName 'Anything LAN files' -Protocol TCP -LocalPort 445" + scope + "| Out-Null; " +
                      "New-NetFirewallRule -Name 'Anything-DiscoveryUdp' -DisplayName 'Anything LAN discovery UDP' -Protocol UDP -LocalPort 3702" + scope + "| Out-Null; " +
                      "New-NetFirewallRule -Name 'Anything-DiscoveryTcp' -DisplayName 'Anything LAN discovery TCP' -Protocol TCP -LocalPort 5357,5358" + scope + "| Out-Null; " +
                      "New-NetFirewallRule -Name 'Anything-Ping' -DisplayName 'Anything LAN ping' -Protocol ICMPv4 -IcmpType 8" + scope + "| Out-Null";
        }
        var result = Sys.PowerShell(script);
        if (!result.Success) Log.Warn("LAN firewall rules: " + result.StdErr);
        return result.Success;
    }

    public static void OpenHttpPort(int port, bool open, string[]? adapterNames = null)
    {
        string rule = $"Anything HTTP {port}";
        if (open)
        {
            if (adapterNames is { Length: > 0 })
            {
                string aliases = string.Join(",", adapterNames.Select(Sys.PwshQuote));
                var scoped = Sys.PowerShell(
                    $"Get-NetFirewallRule -Name 'Anything-Http-{port}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
                    $"New-NetFirewallRule -Name 'Anything-Http-{port}' -DisplayName {Sys.PwshQuote(rule)} " +
                    $"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -InterfaceAlias {aliases} -RemoteAddress LocalSubnet -Profile Any | Out-Null");
                if (!scoped.Success) Log.Warn("HTTP firewall: " + scoped.StdErr);
                return;
            }
            var r = Sys.Netsh($"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow " +
                              $"protocol=TCP localport={port} profile=private");
            Log.Info($"HTTP firewall rule -> exit {r.ExitCode}");
        }
        else
        {
            Sys.PowerShell($"Get-NetFirewallRule -Name 'Anything-Http-{port}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
            Sys.Netsh($"advfirewall firewall delete rule name=\"{rule}\" protocol=TCP localport={port}");
        }
    }

    // DHCP listens on UDP 67 (server) and answers to UDP 68 (client).
    // Inbound 67 must be open on the Private profile; outbound answers to a
    // broadcast address are allowed by default, but we open 68 too so a
    // locked-down firewall cannot break renewals.
    public static bool OpenDhcpPort(bool open, string? adapterName = null)
    {
        string script = "Get-NetFirewallRule -Name 'Anything-Dhcp-In','Anything-Dhcp-Out' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; ";
        if (open)
        {
            if (string.IsNullOrWhiteSpace(adapterName)) return false;
            string nic = Sys.PwshQuote(adapterName);
            script += $"New-NetFirewallRule -Name 'Anything-Dhcp-In' -DisplayName 'Anything DHCP receive' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 67 -RemotePort 68 -InterfaceAlias {nic} -Profile Any | Out-Null; " +
                      $"New-NetFirewallRule -Name 'Anything-Dhcp-Out' -DisplayName 'Anything DHCP reply' -Direction Outbound -Action Allow -Protocol UDP -LocalPort 67 -RemotePort 68 -InterfaceAlias {nic} -Profile Any | Out-Null";
        }
        var r = Sys.PowerShell(script);
        if (!r.Success) Log.Error("DHCP firewall: " + r.StdErr);
        if (!open) Sys.Netsh("advfirewall firewall delete rule name=\"WifiShare DHCP\" protocol=UDP localport=67"); // legacy rule from pre-rename builds
        return r.Success;
    }
}
