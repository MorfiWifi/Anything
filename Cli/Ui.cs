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
// Ui — banner, menus, prompts, tiny progress helpers.
// ============================================================================
internal static class Ui
{
    public static void Banner()
    {
        if (!Console.IsOutputRedirected) Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@" __        ___  __ _   ___ _                    ");
        Console.WriteLine(@" \ \      / (_)/ _(_) / __| |__   __ _ _ __ ___  ");
        Console.WriteLine(@"  \ \ /\ / /| | |_| | \__ \ '_ \ / _` | '__/ _ \ ");
        Console.WriteLine(@"   \ V  V / | |  _| | |___| | | | (_| | | |  __/ ");
        Console.WriteLine(@"    \_/\_/  |_|_| |_| |___/|_| |_|\__,_|_|  \___| ");
        Console.ResetColor();
        Console.WriteLine("  Folder sharing by IP address  |  Background service + optional client subnet");
        Console.WriteLine($"  Log file: {Log.FilePath}");
        Console.WriteLine(new string('=', 70));
    }

    public static void Section(string title)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.ResetColor();
    }

    public static void Pause()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("Press ENTER to continue...");
        Console.ResetColor();
        try { Console.ReadLine(); } catch { }
    }

    // Prompt with a default shown in [brackets]; ENTER keeps the default.
    public static string Prompt(string label, string defaultValue)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{label} [{defaultValue}]: ");
        Console.ResetColor();
        string? input = null;
        try { input = Console.ReadLine(); } catch { }
        if (string.IsNullOrWhiteSpace(input)) return defaultValue;
        return input.Trim();
    }

    public static bool PromptYesNo(string label, bool defaultYes)
    {
        string hint = defaultYes ? "Y/n" : "y/N";
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{label} ({hint}): ");
        Console.ResetColor();
        string? input = null;
        try { input = Console.ReadLine(); } catch { }
        if (string.IsNullOrWhiteSpace(input)) return defaultYes;
        input = input.Trim().ToLowerInvariant();
        return input is "y" or "yes";
    }

    public static string PromptPassword(string current)
    {
        Console.Write("SMB password (Enter keeps current): ");
        if (Console.IsInputRedirected)
        {
            string? input = Console.ReadLine();
            return string.IsNullOrEmpty(input) ? current : input;
        }
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) { password.Length--; Console.Write("\b \b"); }
            }
            else if (!char.IsControl(key.KeyChar)) { password.Append(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return password.Length == 0 ? current : password.ToString();
    }

    // Simple "please wait" dots while we sleep between long operations.
    public static void Wait(int seconds, string message)
    {
        Console.Write(message);
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            Console.Write(".");
        }
        Console.WriteLine();
    }
}
