# WifiShare

Share folders **by IP address** on the network you are already on, driven by a
background Windows service.

Despite the name, Wi-Fi is not required: LAN mode
works on any connected adapter (Ethernet, Wi-Fi, anything with an IPv4 address)
and changes no IP, routing or DHCP settings. A second **subnet mode** keeps the
original purpose available: a dedicated NAT + DHCP network for one cabled client.

- One self-contained executable, zero NuGet packages, .NET 10.
- The service runs as LocalSystem and starts with Windows — sharing survives
  reboots with nobody signed in.
- After a one-time elevated `install`, **any signed-in user** controls sharing
  from a normal terminal over a local named pipe.

## Quick start

Build and publish (or use a release build):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish
```

One time, from an **Administrator** terminal:

```powershell
publish\WifiShare.exe install
```

From then on, from **any** terminal, no elevation:

```powershell
WifiShare share D:\SharedWithClient     # share a folder on the current network(s)
WifiShare status                        # state + the URLs clients use
WifiShare stop                          # stop sharing, disable boot auto-start
```

`WifiShare` with no arguments opens an interactive menu. `WifiShare help` lists
every command. Clients connect with `\\<this-machine's-IP>\Shared` or, when HTTP
is enabled, `http://<IP>:8080/` — `status` prints the exact addresses.

## How it works

| Piece | Detail |
|---|---|
| Service | `WifiShare`, LocalSystem, auto-start, crash-restart via SCM failure actions |
| Control channel | Named pipe `WifiShare.Control.v1`, JSON-lines, local only |
| Pipe permissions | SYSTEM + Administrators full control; **Interactive Users read/write** |
| Boot auto-start | A successful `share` saves a profile; the service re-applies it at boot |
| Password at rest | DPAPI, machine scope, in `C:\ProgramData\WifiShare\profile.json` |
| State / logs | `C:\ProgramData\WifiShare` (state, profile, DHCP leases, `WifiShare.log`) |

The pipe DACL is the security boundary: any process running as a signed-in user
can reconfigure or stop sharing. That is a deliberate choice for personal
machines — do not install this on a shared/multi-user server where local users
are not trusted. Passwords sent over the pipe are plaintext but never leave the
machine; avoid `--password` in scripts because shells keep command history.

LAN mode and subnet mode are mutually exclusive; starting one refuses while the
other is active. Every engine operation is serialized, so concurrent clients
cannot interleave changes.

## LAN mode (primary)

`share [folder]` creates/uses a managed local SMB account, grants it read/write
on the folder and share, opens scoped firewall rules (SMB 445, discovery, ping,
HTTP) on the selected interfaces, and optionally serves the folder over HTTP.
No adapter addressing, NAT, DHCP or forwarding is touched.

- Re-running `share` with new options reconfigures in place (tear down, re-share).
- `stop` removes the share, firewall rules and HTTP listener, and disables boot
  auto-start. The folder and the local account are kept.
- Defaults: share name `Shared`, user `admin`, password `123456`, HTTP port 8080.
  Override with `--name/--user/--password/--port/--no-http/--interfaces A,B`.
  Unspecified options fall back to the saved profile.

### Shared-folder login

When Windows asks for credentials, use **MACHINE-NAME\admin** (the server's
computer name) so the client selects the server's local account, not its own:

```cmd
net use \\192.168.1.20\Shared /user:MACHINE-NAME\admin *
```

Startup creates a standard local account, grants it folder + share access, and
removes the share's old Everyone grant; Administrators keep full access.
Existing unrelated accounts are never adopted or password-reset — pick another
username if the default is taken. Windows password policy is respected. SMB is
authenticated; the optional HTTP file browser is **not** — enable it only on
trusted networks. If Windows cached a wrong login, disconnect the mapping first
(`net use \\IP\Shared /delete`) and reconnect.

## Subnet mode (one cabled client)

`WifiShare subnet start` builds a dedicated network: WinNAT, a static address on
the downlink, and a DHCP server that offers exactly one client address.
`WifiShare subnet stop` restores the original adapter settings. The legacy
interactive flow is still available via `WifiShare console` (admin, in-process).

| Setting | This machine (downlink) | Client |
|---|---|---|
| IPv4 | 172.20.10.1 | **172.20.10.185** |
| Mask | 255.255.255.0 | 255.255.255.0 |
| Gateway | None; internet exits through the uplink | 172.20.10.1 |
| DNS | Existing uplink DNS | Upstream DNS (inherited or `--dns`) |

The .185 address belongs to the client; both addresses are editable within the
same /24 at start (`--host-ip/--client-ip`), and overlapping upstream subnets
are rejected. The DHCP pool contains exactly the client address — connect only
the intended client to this cable. `--suffix` sets a DNS suffix (default
`mydomain.net`); it does not create a domain or make private DNS reachable.

Client setup: set Ethernet IPv4 **and DNS to automatic**, reconnect the cable or
`ipconfig /renew`. Expect 172.20.10.185 / gateway 172.20.10.1; look for DHCP
OFFER/ACK in the service log. Then `\\172.20.10.1\Shared` or
`http://172.20.10.1:8080/`. `WifiShare leases` shows active leases.

**Disable ICS / Mobile hotspot first.** Start refuses existing ICS, Windows NAT
or UDP-67 conflicts before changing anything; it never deletes other
applications' networks. Setup snapshots address/DNS/forwarding and rolls back
on failure; a failed restore keeps the recovery file for retry. Old APIPA
snapshots restore to DHCP, never back to a static link-local address.

## CLI reference

```
WifiShare                      interactive menu
WifiShare install              install + start the service (admin, one time)
WifiShare uninstall            stop + remove the service (admin; settings kept)
WifiShare share [folder]       LAN-mode share; --name/--user/--password/--port/
                               --no-http/--interfaces A,B
WifiShare stop                 stop all sharing, disable boot auto-start
WifiShare status               service state + client access URLs
WifiShare leases               DHCP leases (subnet mode)
WifiShare subnet start [...]   --uplink/--downlink/--host-ip/--client-ip/--dns/
                               --suffix/--no-dhcp/--lease-hours + share options
WifiShare subnet stop          restore original network settings
WifiShare adapters             read-only adapter table (no service needed)
WifiShare console              legacy in-process menu (admin)
WifiShare help                 full usage
```

`install` must run from the published `WifiShare.exe`, not `dotnet run` — the
SCM needs a real binary path. To upgrade: `uninstall`, replace the exe,
`install` again. Settings in `C:\ProgramData\WifiShare` survive upgrades.

## Troubleshooting

1. **"Service is not reachable"**: it is not installed or not running. Check
   `sc query WifiShare`; install from an elevated terminal, or start it with
   `sc start WifiShare`. Details land in `C:\ProgramData\WifiShare\WifiShare.log`.
2. **Share fails at boot**: the saved folder may be gone or no adapter is up yet.
   The service logs the reason; fix and re-run `share`, or `stop` to disable
   auto-start.
3. **Client cannot open `\\IP\Shared`**: use the IP printed by `status`, check
   the firewall rules exist (`Get-NetFirewallRule -Name WifiShare-*`), and try
   the HTTP URL to separate SMB-auth problems from reachability problems.
4. **Subnet client has 169.254.x.x**: no DHCP lease. Check automatic IPv4/DNS,
   OFFER/ACK in the log, and whether another DHCP server holds UDP 67.
5. **Subnet client has .185 but no internet**: `ping 172.20.10.1`, then
   `ping 1.1.1.1`; check NAT is active (`Get-NetNat`), uplink internet and VPN
   routes. Names failing separately: `nslookup example.com` and the chosen DNS.
6. **`New-NetNat` error 122**: fixed in 1.1.1 — NAT names are short and unique;
   upgrade if an old build produced a 42-character name.

## Verification

```powershell
dotnet build -c Release
dotnet run --project tests/WifiShare.Tests.csproj -c Release
```

Tests cover addressing, subnet overlap, adapter selection, restoration
decisions, interface filtering, malformed requests and real loopback UDP
OFFER/ACK/NAK exchanges. They do not change OS networking. End-to-end
forwarding still requires an elevated run and a physical client.

Opt-in integration checks (see test source for what they touch):

```powershell
dotnet run --project tests/WifiShare.Tests.csproj -c Release -- --smb-integration
dotnet run --project tests/WifiShare.Tests.csproj -c Release -- --nat-integration
```

The SMB check validates the default login through a real loopback share logon;
the NAT check (Administrator) creates and removes its own NAT and restores
forwarding, refusing existing NAT/ICS sessions.

## License

MIT — see [LICENSE](LICENSE).
