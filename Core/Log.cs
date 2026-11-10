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
// Log — console sink (colored) for the CLI, file sink for the service. The
// service disables the console sink: session 0 has no console to write to.
// ============================================================================
internal static class Log
{
    private static readonly object _lock = new();
    public static string FilePath { get; set; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "WifiShare.log");
    public static bool ConsoleEnabled { get; set; } = true;

    private static void Write(string level, string msg, ConsoleColor color)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
        lock (_lock)
        {
            if (ConsoleEnabled)
            {
                try
                {
                    var old = Console.ForegroundColor;
                    try
                    {
                        Console.ForegroundColor = color;
                        Console.WriteLine(line);
                    }
                    finally { Console.ForegroundColor = old; }
                }
                catch { /* no console (service/session 0) — file sink still works */ }
            }
            try { File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging must never crash */ }
        }
    }

    public static void Info(string m) => Write("INFO", m, ConsoleColor.Gray);
    public static void Ok(string m) => Write("OK", m, ConsoleColor.Green);
    public static void Warn(string m) => Write("WARN", m, ConsoleColor.Yellow);
    public static void Error(string m) => Write("ERROR", m, ConsoleColor.Red);
    public static void Step(string m) => Write("STEP", m, ConsoleColor.Cyan);
}
