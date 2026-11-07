# OpenCode Warp

A fast Windows dashboard and loopback relay that gives **OpenCode a rotating WARP exit IP** without routing the rest of the computer through WARP and **without restarting OpenCode when the free usage limit is hit**.

The app is self-contained in `D:\OpenCodeWarp`. It does not modify OpenCode's installation.

---

## What it fixes

- **WARP is applied to OpenCode only.** The relay listens on `127.0.0.1:41000`; only the OpenCode process tree receives `HTTP_PROXY` / `HTTPS_PROXY` pointing at it. Windows, browsers, and other apps stay direct.
- **Search and web fetch keep working.** In the default **AI-only** scope, model/provider hosts use WARP while search hosts (`mcp.exa.ai`, Exa, Firecrawl, Tavily, DuckDuckGo, Google, Bing, Brave) and ordinary web pages stay direct.
- **OpenCode is not killed or restarted on a connection reset.** The watcher detects `ECONNRESET`, reconnects WARP, and probes the OpenCode endpoint. It does not switch models.
- **Usage limits do not trigger IP rotation.** The provider may enforce its quota on the account, so changing IPs may not restore access.
- **One stable proxy endpoint.** OpenCode always uses `http://127.0.0.1:41000`. WARP's real proxy port (`127.0.0.1:40000`) changes underneath, so OpenCode never needs a new environment or a restart.

## How rotation and recovery work

1. A rotation starts from **Rotate WARP now** (or optionally on HTTP 429). Connection resets trigger a WARP reconnect and endpoint probe, without replacing the registration.
2. The relay **closes its WARP gate**; new AI connections wait instead of hitting a half-replaced tunnel.
3. `warp-cli disconnect`
4. `warp-cli registration delete`
5. `warp-cli registration new`
6. `warp-cli mode proxy` and `warp-cli proxy port 40000`
7. `warp-cli connect`
8. Poll `https://www.cloudflare.com/cdn-cgi/trace` until it reports `warp=on` with a **different exit IP**.
9. Close stale WARP tunnels and open the gate. Waiting AI connections continue through the new tunnel. Do **not** touch the OpenCode process.
10. If a WARP CONNECT returns 502/503/504, the relay retries three times with a short backoff. Two failed destinations within 30 seconds trigger an automatic WARP reconnect.

A 45-second cooldown and a minimum spacing between rotations prevent Cloudflare registration throttling.

---

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the .NET 8 SDK is enough to build)
- Cloudflare WARP installed (`C:\Program Files\Cloudflare\Cloudflare WARP\warp-cli.exe`)
- OpenCode CLI installed (`opencode.exe` or `opencode.cmd` on `PATH`, or set its path in Settings)

The app does not need to be run as Administrator. WARP proxy mode is user-level.

## Build and run

```powershell
cd D:\OpenCodeWarp
powershell -ExecutionPolicy Bypass -File build.ps1
```

That produces:

- `dist\OpenCodeWarp.exe` — the Windows dashboard
- `dist\ocwarp.exe` — headless diagnostics / relay

Start the dashboard by double-clicking `run.cmd` or `dist\OpenCodeWarp.exe`.

### First run

1. Open **Settings** and confirm:
   - Scope: **AI-only split routing**
   - Project folder: where you want OpenCode to open
  - Connection resets reconnect WARP and check the OpenCode endpoint. The app does not select another model.
2. Click **Launch OpenCode**. A hidden private server starts with a generated password, and OpenCode opens in its own console attached to that server. Both use the relay proxy environment.
3. Optionally enable **Launch OpenCode automatically when the relay starts** and **Start with Windows**.

> The app does not rotate IPs on account usage limits. If the provider rejects requests because the account quota is exhausted, wait for its reset.

> **Important:** launch OpenCode from this dashboard. If you start OpenCode from another terminal, it will not have the proxy environment and the app cannot inject one into an already-running process. The app will still watch the log and rotate WARP, but that instance will not be using the relay.

The window can be closed to the tray; the relay remains available. Use the tray menu to reopen, rotate, stop OpenCode, or exit. Exiting the app stops the OpenCode instance that it launched (an explicit exit, not a limit-hit rotation).

## Interface

- **WARP tunnel** — live `warp=on` state, exit IP, Cloudflare colo.
- **Local relay** — listening endpoint and traffic counters.
- **OpenCode** — TUI PID and uptime for the managed instance. The app only stops it on the explicit **Stop OpenCode** button or app exit; its hidden private server is stopped with it.
- **Rotations** — count, last rotation time, last exit IP.
- **Activity** — relay, quota, and rotation events.

If OpenCode was already running before the app starts, the OpenCode card shows **EXTERNAL** with the number of processes it did not launch. Those processes cannot be retrofitted with a proxy environment; click **Launch OpenCode** to open a managed instance that uses the relay. The app never kills or restarts the external instances.

- **Test WARP** — verifies `warp=on` through WARP's local proxy.
- **Test split routing** — proves the three requirements together: AI hosts use WARP, search hosts are direct, and an ordinary `example.com` fetch returns 200 through the relay.

## Settings

Stored at `%LOCALAPPDATA%\OpenCodeWarp\settings.json`.

| Setting | Default | Meaning |
|---|---|---|
| Scope | `AiOnly` | AI hosts through WARP; search/fetch direct. `All` sends everything except the direct list through WARP. |
| Relay port | `41000` | Stable loopback proxy used by OpenCode. |
| WARP proxy port | `40000` | Local WARP proxy port. |
| Rotate on rate limit | off | Also rotate on HTTP 429 / rate-limit errors. |
| Auto-launch OpenCode | off | Start OpenCode when the relay starts. |
| Start with Windows | off | HKCU Run entry; starts minimized to the tray. |
| Minimum seconds between rotations | 25 | Cloudflare throttling guard. |
| Rotation attempts | 3 | Retries per rotation request. |
| WARP wait seconds | 30 | How long to wait for a fresh exit IP. |
| Health check seconds | 25 | Background WARP trace interval. |

Extra AI provider hosts can be added in Settings (for example a custom `baseURL` host). AI hosts are also discovered automatically from `opencode.json` `baseURL` entries.

## CLI (`ocwarp.exe`)

```powershell
ocwarp status        # WARP, direct egress, WARP egress, OpenCode discovery
ocwarp trace         # direct vs WARP exit IP
ocwarp rotate        # rotate WARP without touching OpenCode
ocwarp split-test    # verify AI -> WARP, search -> direct, web fetch works
ocwarp selftest      # full relay smoke test
ocwarp hosts         # show AI host rules and sample routing decisions
ocwarp run           # headless relay + launch OpenCode
ocwarp watch         # headless relay only
ocwarp proxy         # relay only, foreground
ocwarp test-watcher  # synthetic free-limit detection test
```

Add `--verbose` for debug output.

## Troubleshooting

**"Only one usage of each socket address..."** — another relay/CLI is already using port 41000. Close the other process, or change the relay port in Settings and restart the app.

**WARP is Connected but the card is OFFLINE** — click **Reconnect**. The app waits for a trace that reports `warp=on`; it does not consider "Connected" enough.

**Search or web fetch breaks** — keep the scope on **AI-only** and run **Test split routing**. If you selected **All**, add the search hosts to the direct-host compatibility list (the defaults are already there).

**OpenCode cannot be found** — set the full path to `opencode.exe` or `opencode.cmd` in Settings. Leaving it empty auto-detects the npm and local installs.

**"You've used today's free messages" keeps appearing** — that limit may be attached to the provider account rather than the WARP IP. The app does not rotate IPs or switch models for account quota errors; wait for the quota reset.

**A rotation fails** — OpenCode stays open. Click **Rotate WARP now** to retry, or check the activity log / `%LOCALAPPDATA%\OpenCodeWarp\logs`. The relay retries transient WARP 502s and automatically repairs the tunnel after repeated failures.

## Notes

- The relay only binds to loopback.
- The app clears inherited proxy variables from its own process; only the OpenCode child gets an explicit proxy block.
- The OpenCode processes have stale `OPENCODE_SESSION_ID`, `OC_*`, and tokenharbor base URLs removed. The hidden server receives a fresh `OPENCODE_SERVER_PASSWORD`; the attached TUI receives the matching `OPENCODE_PASSWORD`.
- WARP stays in local proxy mode; the app does not set the Windows system proxy, so the rest of the machine remains direct.
- Logs: `%LOCALAPPDATA%\OpenCodeWarp\logs\relay-YYYYMMDD.log`
- Settings: `%LOCALAPPDATA%\OpenCodeWarp\settings.json`
