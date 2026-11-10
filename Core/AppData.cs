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

// Per-machine files shared by every build and the Windows service, so a
// session started from one executable can always be stopped from another.
internal static class AppData
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WifiShare");
    public static string StatePath => Path.Combine(Dir, "wifishare-state.json");
    public static string ProfilePath => Path.Combine(Dir, "profile.json");
    public static string LeasePath => Path.Combine(Dir, "dhcp-leases.json");
    public static string LogPath => Path.Combine(Dir, "WifiShare.log");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);
}