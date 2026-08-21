# CPU Monitoring, Threshold Restart & Options Page

Date: 2026-08-21

## Problem

SwiftWatchdog currently handles one known SwiftPOS Service Monitoring Service
failure mode: a silent hang where the service reports "Running" but has
stopped doing anything. That's covered by the existing daily forced restart
and the 60s down-check.

A second, newly reported failure mode has surfaced: the service can also pin
CPU usage for a sustained period. This hasn't been reproduced during design
(see "CPU metric" below for the telemetry gathered instead), so the watchdog
needs to detect and recover from it directly — restart the service if its CPU
usage stays above a threshold for a configured duration.

All watchdog behavior (check interval, daily restart time, timeouts) is
currently hardcoded as `const` fields in `WatchdogWorker`. Since staff need to
tune the new CPU threshold/period without editing code, this also adds a
tray-launched options window and the settings plumbing to support it.

## Settings model & storage

New `WatchdogSettings` (immutable POCO, JSON-serialized):

| Field | Type | Range | Default |
|---|---|---|---|
| `CpuMonitoringEnabled` | bool | — | `true` |
| `CpuThresholdPercent` | double | 1–1000 | `65` |
| `CpuSustainedPeriodSeconds` | int | 10–36000 | `120` |
| `DailyRestartTime` | TimeOnly | — | `03:00` |

Persisted to `C:\ProgramData\SwiftWatchdog\settings.json`, written only by the
service (LocalSystem) — consistent with how `PAUSED` is handled today; no ACL
changes to `DataDir`.

- **Startup**: load `settings.json` if present; if missing *or* fails to
  parse, fall back to defaults and log a warning (never crash startup on a
  bad/corrupt file — relevant since these are POS tills that can lose power
  mid-write).
- **Writes**: written atomically — serialize to a temp file in the same
  directory, then `File.Move` (overwrite) over `settings.json`. Prevents a
  half-written file from corrupting settings on a crash or power loss.
- **Concurrency**: `WatchdogWorker` holds the current settings as an
  immutable object reference. `SETSETTINGS` builds a *new* `WatchdogSettings`
  instance and atomically swaps the reference (`Interlocked.Exchange` or
  equivalent) rather than mutating fields in place. The CPU-monitor loop and
  the main loop each snapshot the reference once at the top of their own
  iteration — no torn reads across the two background loops.

`WatchdogWorker`'s currently-const `DailyRestartTime` and the new CPU fields
become mutable, sourced from this settings object instead of `const`.

## CPU metric

**Decision: raw per-core** — `(ΔTotalProcessorTime / Δwallclock) × 100`, where
100% = one full logical core saturated (not divided by core count). This is
shape-agnostic and portable across tills with different hardware: a single
runaway thread reads as ~100% regardless of whether the box has 2 cores or
32, whereas a normalized metric (100% = whole box) would need a different
threshold per machine to catch the same failure.

This was validated in two ways during design:

1. **Live process inspection**: the target process has 25+ threads on this
   12-core test box — clearly multi-threaded, which is consistent with either
   metric working, but doesn't by itself indicate the *failure's* CPU shape.
2. **5-minute normal-usage baseline** (captured via `Get-Counter
   '\Process(...)\% Processor Time'`, the same raw-per-core semantics as the
   chosen metric): 50 samples, **min 0%, max 1.54%, avg 0.03%**. This confirms
   a 65% default threshold has a large safety margin against false positives
   during normal operation.

The actual CPU-pinning failure could not be reproduced during design, so the
65%/120s defaults are a reasonable starting point rather than a value tuned
against an observed occurrence. **This should be revisited the first time the
CPU-monitor's own logging captures a real episode** — it logs start time,
duration, and min/max CPU% for every sustained-threshold event, which gives
real data to re-tune against.

Implementation note: this permission-safe perf-counter approach was needed
only because the *diagnostic PowerShell session* used during design was
unprivileged. The actual `WatchdogWorker` service runs as LocalSystem, which
has unrestricted query rights to `Process.TotalProcessorTime` for any
process — so the service can use the direct `Process` API as originally
planned; it doesn't need to go through performance counters.

## Service-side CPU monitoring

A second background loop in `WatchdogWorker`, independent of the existing 60s
health-check loop, sampling every **10 seconds** while not paused:

1. Resolve the target service's PID once and hold the `Process` handle
   (factor the existing `sc queryex` PID-lookup out of `KillServiceProcess`
   into a shared helper for the initial resolution). Re-resolve only when the
   held `Process` throws or has exited — this doubles as PID-change
   detection (service was restarted by another path), at which point the
   CPU-sample baseline resets rather than computing a bogus delta across two
   process lifetimes. Avoids re-shelling `sc.exe` on every 10s tick
   (~8,600 calls/day at the old rate).
2. Compute raw per-core CPU% between consecutive samples; the first sample
   after (re)resolution primes the baseline and is skipped.
3. Track a "high CPU since" timestamp. Once sustained ≥
   `CpuSustainedPeriodSeconds`, log the episode (start time, duration,
   min/max CPU% observed during the episode) and invoke the existing
   `ForceRestart()` path — stop with timeout, kill process tree if it won't
   stop, then start. A process pegged for the full sustained period is
   already likely wedged, so this matches the urgency of the down-recovery
   restart: fires immediately, any time of day, no quiet-hours window.
4. After a CPU-triggered restart, a **fixed 5-minute cooldown** (hardcoded
   constant, not user-configurable) blocks re-arming — prevents restart-loop
   thrashing if the service comes back up already busy (e.g. re-indexing on
   startup). Deliberately independent of `CpuSustainedPeriodSeconds`, which
   could be configured as low as 10s.
5. Tracking resets (without restarting) whenever CPU drops below threshold,
   the watchdog is paused, `CpuMonitoringEnabled` is false, or the service
   can't be resolved.
6. Log only on state transitions (high-CPU episode start, return-to-normal,
   restart) — not every 10s tick.

**Restart mutual exclusion**: a shared lock wraps all three restart paths
(down-recovery, daily forced, CPU-triggered) so the main loop and the CPU
loop can never interleave stop/start/kill calls against the service
concurrently.

## Pipe protocol extension

Two new commands alongside the existing `PAUSE`/`RESUME`:

- `GETSETTINGS` → service responds with the current settings as JSON.
- `SETSETTINGS <json>` → service validates and clamps to the ranges above,
  writes `settings.json`, atomically swaps the in-memory settings reference,
  responds `OK` or `ERR: <reason>`.

Unlike `PAUSE`/`RESUME` (which respond based only on recognizing the command
keyword, then perform the actual stop/start *after* replying —
fire-and-forget from the pipe's perspective), `SETSETTINGS` must validate and
persist *before* writing the response, so the tray gets a real result to act
on (e.g. keep the options window open with the entered values if the service
rejected them).

The current fixed 64-byte command buffer and 256-byte pipe in/out buffers are
too small and fragile for JSON payloads. Increase buffer sizes and switch the
read loop from a single `Read()` call (which assumes the whole message
arrives in one read — fine for 5-byte commands, not safe for variable-length
JSON) to one that accumulates until a newline terminator, capped at a few KB
to bound memory use against a misbehaving local writer.

**Pipe ACL stays `Everyone`**, matching the existing Pause/Resume trust
model — these are till machines where physical/login access already implies
trust, and Pause/Resume already allows a bigger lever (stopping the POS
service outright) than tweaking a restart threshold.

## Tray options window

New "Options..." item in the tray context menu, opening a modal `OptionsForm`
using the approved grouped-sections layout:

- **"CPU Monitoring" group**: enable checkbox, threshold (%) and sustained
  period (seconds) as `NumericUpDown` controls, clamped to the ranges above.
  Both numeric fields grey out (disabled, values preserved) when the
  checkbox is unchecked.
- **"Daily Restart" group**: a time picker for `DailyRestartTime`.
- Cancel / Save buttons.

Opening the window sends `GETSETTINGS` to populate current values; failure
shows a `MessageBox` (matching the existing Pause/Resume error pattern) and
the window doesn't open. Save sends `SETSETTINGS`; on `OK` the window closes;
on `ERR` a `MessageBox` shows the reason and the window stays open with the
user's entered values so they can correct and retry. Settings apply live —
no service restart required; the next loop tick on either background loop
picks up the new values.

Out of scope for this pass: no live status panel (current CPU%, last
restart time) in the options window; health-check interval (60s) stays
hardcoded.

## Upgrade path

Existing installs have no `settings.json` — first run under the new version
just writes the defaults above, no migration needed.

`Installer/SwiftWatchdog.iss` currently only `taskkill`s the tray app before
copying files; it never stops the service, so `[Files]` can fail to overwrite
the running service's locked EXE on upgrade. Fix: add a best-effort
`sc stop SwiftWatchdog` to the existing `CurStepChanged(ssInstall)` block.
The existing `[Run]` step already re-runs `--install` afterward, which
reconfigures and restarts the service on the new binary — no changes needed
to `ServiceInstaller.cs`.

Bump `FileVersion`/`AssemblyVersion` in both `.csproj` files and `AppVersion`
in the `.iss` from `1.0.0` to `1.1.0`.

Protocol compatibility is defense-in-depth only (the installer always
replaces both EXEs together, so version-mismatched pairs aren't a real
scenario in practice): an old tray only ever sends `PAUSE`/`RESUME`, which a
new service still handles identically; a new tray's `GETSETTINGS` against a
hypothetical old service gets `ERR: Unknown command`, handled the same as any
other pipe error — shown and the window doesn't open, no crash.

## Testing / validation approach

No automated test project exists in this repo; verification is manual,
matching the existing project convention:

- Build, install, confirm `settings.json` is created with defaults on first
  run.
- Open Options, change each field, Save, confirm `settings.json` updates and
  behavior changes live (e.g. change daily restart time and confirm the new
  time is honored without a service restart).
- Confirm the CPU-monitor loop logs state transitions correctly; since the
  real failure isn't reproducible on demand, this is validated by
  synthetically loading the process (or a stand-in) to cross a low test
  threshold, confirming the sustained-period timer and forced-restart path
  fire correctly, then reverting to real defaults.
- Confirm pipe error paths (e.g. malformed `SETSETTINGS` payload) return
  `ERR` and the options window surfaces it without crashing.
- Confirm the installer upgrade path: install 1.0.0-equivalent build, then
  install the new build over it, confirm the service ends up running the new
  binary and `settings.json` is created correctly.
