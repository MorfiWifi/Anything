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
// ClientHelp — what to do on the CLIENT pc, printed after Start + in README.
// ============================================================================
internal static class ClientHelp
{
    public static string SuggestClientIp(string hostIp)
    {
        try
        {
            var parts = hostIp.Split('.').Select(int.Parse).ToArray();
            int last = parts[3] < 254 ? parts[3] + 1 : parts[3] - 1;
            if (last is 0 or 255) last = 100;
            return $"{parts[0]}.{parts[1]}.{parts[2]}.{last}";
        }
        catch { return "172.20.10.186"; }
    }

    public static void Print(string hostIp, string shareName, int httpPort, bool httpOn,
                             string dns1, string dns2,
                             bool dhcpOn, string poolStart, string poolEnd, string domain,
                             bool isolated = false)
    {
        string clientIp = poolStart;
        Ui.Section("CLIENT SETUP (do this on the client PC)");
        Console.WriteLine("1. Plug the Ethernet cable laptop <-> client.");
        if (dhcpOn)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("2. PLUG-AND-PLAY: leave the client on automatic (DHCP). It will receive:");
            Console.WriteLine($"     IP address:      {poolStart} - {poolEnd} (automatic)");
            Console.WriteLine($"     Subnet mask:     255.255.255.0");
            Console.WriteLine($"     Default gateway: {hostIp}");
            Console.WriteLine($"     DNS:             {dns1}  (alt. {dns2})");
            Console.WriteLine($"     DNS suffix:      {domain}");
            Console.ResetColor();
            Console.WriteLine("   No manual TCP/IP setup needed. If DHCP ever fails, fall back to:");
        }
        else
        {
            Console.WriteLine("2. DHCP is OFF, so set a STATIC IP on the client (Control Panel > Network):");
        }
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"     IP address:      {clientIp}");
        Console.WriteLine($"     Subnet mask:     255.255.255.0");
        Console.WriteLine($"     Default gateway: {hostIp}");
        Console.WriteLine($"     DNS:             {dns1}  (alt. {dns2})");
        Console.ResetColor();
        Console.WriteLine("3. Open the shared files with one of:");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"     \\\\{hostIp}\\{shareName}      (File Explorer address bar)");
        if (httpOn) Console.WriteLine($"     http://{hostIp}:{httpPort}/   (any browser)");
        Console.ResetColor();
        Console.WriteLine("4. On the client, run ipconfig /all and check the address, gateway and DNS above.");
        Console.WriteLine("   For DHCP, set IPv4 AND DNS to automatic, then run ipconfig /renew on the client.");
        Console.WriteLine("   A 169.254.x.x address means the client did not obtain a DHCP lease.");
        if (isolated)
        {
            Console.WriteLine($"5. Test: ping {hostIp}, then open the share or HTTP link above.");
            Console.WriteLine("   ISOLATED subnet (no uplink): there is no internet beyond this machine;");
            Console.WriteLine("   the gateway/DNS entries serve the local link only. File access is the goal.");
        }
        else
        {
            Console.WriteLine($"5. Test: ping {hostIp}, then ping 1.1.1.1, then nslookup example.com.");
            Console.WriteLine("   ICMP can be blocked; also try the HTTP link and a website in a browser.");
        }
        Console.WriteLine("   File Explorer Network discovery is separate: use the direct share address above.");
        Console.WriteLine("Keep this app open while DHCP or HTTP service is needed.");
    }
}
