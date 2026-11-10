// ============================================================================
// Anything — folder sharing by IP address, backed by a background Windows
// service. One self-contained executable, three entry modes:
//   Anything install      one-time elevated setup (LocalSystem, auto-start at boot)
//   Anything <verb>       terminal client for any signed-in user (named pipe)
//   Anything --service    SCM entry point (do not run from a terminal)
//
// LAN mode shares a folder on the current network(s) without touching any IP
// settings; subnet mode builds a dedicated NAT+DHCP network for one cabled
// client (default host 172.20.10.1, client 172.20.10.185). State, profile and
// logs live in C:\ProgramData\Anything. Failed setup rolls back; failed
// cleanup keeps the recovery file for retry. See README.md for client setup.
// Code: Core/ (primitives), Engine/ (orchestration), Service/ (SCM + pipe),
// Ipc/ (protocol), Cli/ (client) and this bootstrap; one assembly, one exe.
// ============================================================================
using System;

try
{
    return CliMain.Run(args);
}
catch (Exception ex)
{
    try
    {
        Log.Error("Fatal unhandled error: " + ex.Message);
        Log.Info(ex.ToString());
    }
    catch { /* logging must never crash the process */ }
    return 1;
}