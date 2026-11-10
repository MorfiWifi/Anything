# Anything

Share **any folder by IP address** on the network you are already on, driven by
a background Windows service. Formerly named WifiShare — Wi-Fi was never
required: LAN mode works on any connected adapter (Ethernet, Wi-Fi, anything
with an IPv4 address) and changes no IP, routing or DHCP settings. A second
**subnet mode** builds a dedicated NAT + DHCP network for one cabled client,
or an isolated DHCP + files network with `--no-uplink`.

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
publish\Anything.exe install
```

From then on, from **any** terminal, no elevation:

```powershell
Anything share D:\SharedWithClient     # share a folder on the current network(s)
Anything status                        # state + the URLs clients use
Anything stop                          # stop sharing, disable boot auto-start
```

`Anything` with no arguments opens an interactive menu. `Anything help` lists
every command **with examples**. Clients connect with `\\<this-machine's-IP>\Shared`
or, when HTTP is enabled, `http://<IP>:8080/` — `status` prints the exact addresses.

## Sample commands

```powershell
# --- one-time service setup (Administrator) ---
Anything install                                    # install + start the service
Anything uninstall                                  # remove the service, keep settings

# --- LAN mode: share on the network you are already on (any user) ---
Anything share D:\Data                              # SMB + HTTP, all physical adapters
Anything share D:\Data --name Media --port 9000     # custom share name / HTTP port
Anything share D:\Data --user guest --password "s3cret"
Anything share --no-http --interfaces "Ethernet"    # SMB only, one adapter
Anything share                                      # re-apply the saved profile
Anything status                                     # client access URLs
Anything stop                                       # stop sharing + boot auto-start

# --- subnet mode: dedicated cable network, internet through Wi-Fi ---
Anything subnet start                               # defaults: 172.20.10.1 -> .185
Anything subnet start --uplink "Wi-Fi" --downlink "Ethernet" --dns 1.1.1.1,8.8.8.8
Anything subnet start --host-ip 192.168.50.1 --client-ip 192.168.50.10 --suffix lab.local
Anything subnet start --no-dhcp --folder D:\Drop --no-http   # static client, SMB only
Anything leases                                     # who got the DHCP address
Anything subnet stop                                # restore original adapter settings

# --- isolated subnet: no uplink, no internet — DHCP + files only ---
Anything subnet start --no-uplink --host-ip 172.20.10.1 --client-ip 172.20.10.185 `
  --suffix mydomain.net --dns 172.16.61.20,172.16.61.10

# --- diagnostics / legacy ---
Anything adapters                                   # read-only adapter table
Anything console                                    # legacy in-process menu (admin)
Anything help                                       # full usage with examples
```

On the client PC:

```powershell
# LAN mode or subnet mode — use the IP printed by `Anything status`
net use \\192.168.110.110\Shared /user:MACHINE-NAME\admin *
# or in any browser: http://192.168.110.110:8080/

# Subnet client with --no-dhcp (static setup):
# IP 172.20.10.185, mask 255.255.255.0, gateway 172.20.10.1, DNS as configured
ipconfig /renew                                     # DHCP mode: refresh the lease
ping 172.20.10.1                                    # subnet host reachability
```

## How it works

| Piece | Detail |
|---|---|
| Service | `Anything`, LocalSystem, auto-start, crash-restart via SCM failure actions |
| Control channel | Named pipe `Anything.Control.v1`, JSON-lines, local only |
| Pipe permissions | SYSTEM + Administrators full control; **Interactive Users read/write** |
| Boot auto-start | A successful `share` saves a profile; the service re-applies it at boot |
| Password at rest | DPAPI, machine scope, in `C:\ProgramData\Anything\profile.json` |
| State / logs | `C:\ProgramData\Anything` (state, profile, DHCP leases, `Anything.log`) |

The pipe DACL is the security boundary: any process running as a signed-in user
can reconfigure or stop sharing. That is a deliberate choice for personal
machines — do not install this on a shared/multi-user server where local users
are not trusted. Passwords sent over the pipe are plaintext but never leave the
machine; avoid `--password` in scripts because shells keep command history.

LAN mode and subnet mode are mutually exclusive; starting one refuses while the
other is active. Every engine operation is serialized, so concurrent clients
cannot interleave changes.

**Upgrading from WifiShare 1.2:** `WifiShare uninstall` (old exe, admin), then
`Anything install` (new exe, admin). On first start the data folder, recovery
state and managed SMB account markers migrate from `ProgramData\WifiShare`
automatically; the saved share re-applies at boot.

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

`Anything subnet start` builds a dedicated network: WinNAT, a static address on
the downlink, and a DHCP server that offers exactly one client address.
`Anything subnet stop` restores the original adapter settings. The legacy
interactive flow is still available via `Anything console` (admin, in-process).

### Isolated subnet (no uplink, no internet)

With `--no-uplink` the subnet is fully isolated: static address + DHCP + SMB/HTTP
file sharing on the cable, **no NAT, no forwarding, no uplink required** (WinNAT
is not needed either). Use this when the client only needs the shared folder:

```powershell
Anything subnet start --no-uplink --host-ip 172.20.10.1 --client-ip 172.20.10.185 `
  --suffix mydomain.net --dns 172.16.61.20,172.16.61.10
```

`--no-uplink` and `--uplink` are mutually exclusive. DHCP still advertises the
host IP as gateway and the chosen DNS servers, but nothing routes beyond this
machine — `ping 172.20.10.1` and the share/HTTP links are the connectivity tests.
ICS and UDP-67 conflict checks still apply because they would break DHCP.

### Addressing

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
`http://172.20.10.1:8080/`. `Anything leases` shows active leases.

**Disable ICS / Mobile hotspot first.** Start refuses existing ICS, Windows NAT
or UDP-67 conflicts before changing anything; it never deletes other
applications' networks. Setup snapshots address/DNS/forwarding and rolls back
on failure; a failed restore keeps the recovery file for retry. Old APIPA
snapshots restore to DHCP, never back to a static link-local address.

## CLI reference

```
Anything                      interactive menu
Anything install              install + start the service (admin, one time)
Anything uninstall            stop + remove the service (admin; settings kept)
Anything share [folder]       LAN-mode share; --name/--user/--password/--port/
                              --no-http/--interfaces A,B
Anything stop                 stop all sharing, disable boot auto-start
Anything status               service state + client access URLs
Anything leases               DHCP leases (subnet mode)
Anything subnet start [...]   --uplink/--downlink/--host-ip/--client-ip/--dns/
                              --suffix/--no-dhcp/--lease-hours + share options;
                              --no-uplink = isolated subnet (DHCP + files only)
Anything subnet stop          restore original network settings
Anything adapters             read-only adapter table (no service needed)
Anything console              legacy in-process menu (admin)
Anything help                 full usage with examples
```

`install` must run from the published `Anything.exe`, not `dotnet run` — the
SCM needs a real binary path. To upgrade: `uninstall`, replace the exe,
`install` again. Settings in `C:\ProgramData\Anything` survive upgrades.

## Troubleshooting

1. **"Service is not reachable"**: it is not installed or not running. Check
   `sc query Anything`; install from an elevated terminal, or start it with
   `sc start Anything`. Details land in `C:\ProgramData\Anything\Anything.log`.
2. **Share fails at boot**: the saved folder may be gone or no adapter is up yet.
   The service logs the reason; fix and re-run `share`, or `stop` to disable
   auto-start.
3. **Client cannot open `\\IP\Shared`**: use the IP printed by `status`, check
   the firewall rules exist (`Get-NetFirewallRule -Name Anything-*`), and try
   the HTTP URL to separate SMB-auth problems from reachability problems.
4. **Subnet client has 169.254.x.x**: no DHCP lease. Check automatic IPv4/DNS,
   OFFER/ACK in the log, and whether another DHCP server holds UDP 67.
5. **Subnet client has .185 but no internet**: expected with `--no-uplink`
   (isolated by design). Otherwise `ping 172.20.10.1`, then
   `ping 1.1.1.1`; check NAT is active (`Get-NetNat`), uplink internet and VPN
   routes. Names failing separately: `nslookup example.com` and the chosen DNS.
6. **`New-NetNat` error 122**: fixed in 1.1.1 — NAT names are short and unique;
   upgrade if an old build produced a 42-character name.

## Verification

```powershell
dotnet build -c Release
dotnet run --project tests/Anything.Tests.csproj -c Release
```

Tests cover addressing, subnet overlap, adapter selection, restoration
decisions, interface filtering, malformed requests and real loopback UDP
OFFER/ACK/NAK exchanges. They do not change OS networking. End-to-end
forwarding still requires an elevated run and a physical client.

Opt-in integration checks (see test source for what they touch):

```powershell
dotnet run --project tests/Anything.Tests.csproj -c Release -- --smb-integration
dotnet run --project tests/Anything.Tests.csproj -c Release -- --nat-integration
```

The SMB check validates the default login through a real loopback share logon;
the NAT check (Administrator) creates and removes its own NAT and restores
forwarding, refusing existing NAT/ICS sessions.

## License

MIT — see [LICENSE](LICENSE).