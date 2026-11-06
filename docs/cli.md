# Command line

English | [繁體中文](cli.zh-TW.md)

Back to the [README](../README.md).

`IsItGreenYet-cli.exe` reads the status table that the running app writes to `%LOCALAPPDATA%\IsItGreenYet\cache.db`, prints one JSON object, and reports the overall status through its exit code; the result matches the tray. The computer needs the .NET 10 Runtime (installing the .NET 10 Desktop Runtime covers this; see [System requirements](../README.md#system-requirements) in the README).

```
IsItGreenYet-cli.exe status --json [--refresh]
IsItGreenYet-cli.exe notices
IsItGreenYet-cli.exe --help
```

| Argument | Behavior |
|---|---|
| `status --json` | Reads the status table in `cache.db`, prints one JSON object, and reports the overall status through the exit code |
| `--refresh` | First asks the running app to refresh, waits for the refresh to finish, then reads (see [`--refresh`](#--refresh) below) |
| `notices` | Prints the full text of the `LICENSE` and third-party notices embedded in the exe (see [`notices`](#notices) below); doesn't read `cache.db` |
| `--help` | Prints usage to stdout; exit code 0 |
| Anything else (unrecognized arguments, missing `--json`) | Prints usage to stderr; exit code 4; no JSON |

- `status` must come first; `--json` and `--refresh` can appear in any order, once each, and must match exactly, including case. `notices` and `--help` must be used on their own; together with other arguments they count as an argument error (exit code 4).
- The status comes from the app: while it's running, the app writes the status table and a heartbeat time every 60 seconds and whenever the status changes. The command line never calls the GitHub API itself and needs no token, so it returns an error (4) when the app has exited or is stuck; when the app has never run, it returns `no_status` (3). See the [exit code table](#exit-codes).
- Read-only: it doesn't change the contents of `cache.db`, and it doesn't read or change `settings.json`; if `cache.db` doesn't exist, it isn't created. `cache.db` is in WAL mode, so the read-only connection may leave `cache.db-wal` and `cache.db-shm` next to it. If `cache.db` is locked, it waits up to 5 seconds; if it's corrupted, it doesn't rename or repair it (it returns `db_error`).
- It's a console program: pwsh, Windows PowerShell, and cmd all wait for it to exit and can get its exit code (`$LASTEXITCODE`, `%ERRORLEVEL%`). The app itself is a windowed program, so PowerShell doesn't wait for it to exit and can't get its exit code; that's why the command line is a separate executable.

## Output

This section and the exit code table below are about `status --json`; for `notices`, see the [last section](#notices).

- stdout contains only one JSON object: a single line, not indented, ending with a newline. When `state` is `error`, stderr gets one line of explanation in English, for example `Error: The IsItGreenYet app is not running in this session. (reason: app_not_running)`. On an argument error, stderr gets the usage (several lines) and there's no JSON.
- The whole output is pure ASCII: non-ASCII characters (such as Chinese in repo names, workflow names, and titles) are always escaped as `\uXXXX`, and the `+` in version strings is escaped too (`\u002B`); the parsed values are unchanged. If you look at the JSON directly in a console, Chinese text shows up as escape sequences; parse it with PowerShell's `ConvertFrom-Json` (or `jq`) to get the original text back. Pure ASCII isn't affected by the console code page, and saving it with `>` won't garble it.

```powershell
$status = .\IsItGreenYet-cli.exe status --json | ConvertFrom-Json
$exitCode = $LASTEXITCODE
$status.state
$status.runs | Format-Table repo, workflow, state, title
```

- Key names are always English camelCase and don't change with the interface language. Times are always UTC ISO 8601 (ending in `Z`, for example `2026-10-29T02:00:00Z`; fractional seconds are written when present; see [Deviations from the design specs](spec-deviations.md) (Traditional Chinese)).
- When the status table can't be read (no `cache.db`, and so on; see the exit code table), `appVersion`, `heartbeatAt`, and the other fields written by the app are `null`, and `runs` is an empty array. For other errors, as long as the status table can be read, the data that was read is still included, to help work out the cause.

JSON fields (in output order):

| Field | Written by | Content |
|---|---|---|
| `schemaVersion` | CLI | Version of this JSON structure, currently `1`. Fields are only added, never changed; it goes up by 1 if a meaning changes |
| `state` | CLI | Result determined by the exit code table: `failure`, `in_progress`, `success`, `no_data`, or `error`. Equal to `overall` when there's no error |
| `exitCode` | CLI | Same as the program's exit code |
| `reason` | CLI | Reason code when `state` is `error` or `no_data` (see the exit code table); otherwise `null` |
| `cliVersion` | CLI | The command line's own version (`0.3.1+<40-character commit>`) |
| `appVersion` | App | Version of the app that wrote the status table (same format) |
| `generatedAt` | CLI | When the command line generated this output |
| `snapshotAt` | App | Time of the app snapshot this status comes from |
| `heartbeatAt` | CLI (read from the table) | The last time the app wrote the status table (heartbeat) |
| `refreshed` | CLI | `true` when `--refresh` was given and the app's refresh finished within the wait; otherwise `false` |
| `refreshPartial` | CLI | `true` when `refreshed` is `true` and this refresh didn't fully complete (it took over 90 seconds, or was blocked by being offline, a rate-limit pause, being signed out, and so on); otherwise `false` |
| `overall` | App | The tray's overall status: `failure`, `in_progress`, `success`, or `no_data`. "All succeeded" requires at least one tracked item whose latest conclusion is success, which only the app knows, so the command line doesn't work it out from `counts` itself. On the first run or after the cache database is rebuilt, it isn't `success` until everything has been checked (it's `no_data`, exit code 3, when nothing has failed or is in progress; when there are monitored repos but none has been checked yet, the exit code is 4 `data_stale`, see item 33 in [Known limitations](known-limitations.md)) |
| `signedIn` | App | `true`: signed in; `false`: signed out (after a 401); `null`: still checking (the app just started, or it started offline and hasn't finished verifying) |
| `apiVersionError` | App | Whether an API version error occurred (400/410; the app has stopped all polling). It isn't part of `connection` because it changes the exit code to 4 |
| `refreshMode` | App | The auto-refresh setting: `auto`, `30s`, `1m`, `5m`, or `paused` (same as `refreshMode` in `settings.json`) |
| `counts` | App | The same counts as the tray: `failing` is the number of failed tracked items, and `inProgress` is the number of tracked items in progress or queued (including waiting for approval) |
| `monitoredRepos` | App | Number of monitored repos |
| `staleRepos` | App | Number of repos that can't be updated |
| `connection` | App | `offline` (offline), `pausedUntil` (when the global rate-limit pause is scheduled to end; `null` when there's no pause), and `serverErrors` (GitHub keeps returning 5xx). These three don't change `state` or the exit code, consistent with the tray only adding a badge; if they last for more than 1 hour, the result becomes `data_stale` |
| `lastUpdatedAt` | App | The last time a monitored repo got a successful response (kept across restarts); `null` if there hasn't been a successful response yet |
| `runs` | App | The runs shown by "Needs attention only" (tracked failures, plus all unfinished runs and runs waiting for approval), in the same order as the main list; an empty array when there are none |
| `runs[].repo`, `workflow` | App | The repo's full name (`owner/name`) and the workflow name |
| `runs[].runId`, `runNumber`, `attempt` | App | The run's id, its number (the `#N` on screen), and the attempt |
| `runs[].title`, `branch`, `event` | App | Title, branch (may be `null`), and triggering event (for example, `push` or `pull_request`) |
| `runs[].status`, `conclusion` | App | The raw values from GitHub; either may be `null` (`conclusion` is always `null` before the run completes) |
| `runs[].state` | App | Category: `pending` (unfinished), `waiting` (waiting for approval), `success`, `failure`, or `neutral` (gray: cancelled, skipped, `neutral`, `stale`, or an unrecognized conclusion after completion) |
| `runs[].cancelling` | App | Whether the run is "Cancelling" (a cancellation sent from this app was accepted, and the run hasn't finished yet) |
| `runs[].startedAt`, `url` | App | Start time (may be `null`) and the URL of this run's web page |

## Exit codes

Check the table from top to bottom; the first row that applies is the result:

| Order | Condition | Exit code | `state` | `reason` |
|---|---|---|---|---|
| 0 | The .NET runtime can't be found, so the program doesn't start (see the first point below the table) | `-2147450749` | (no JSON) | — |
| 1 | Argument error | 4 | (no JSON) | — |
| 2 | Unexpected exception | 4 | `error` | `internal_error` |
| 3 | One of the first two steps of `--refresh` fails: checking whether the app is running, or opening the refresh event (see [`--refresh`](#--refresh) below) | 4 | `error` | `app_not_running`, `refresh_unreachable`, `refresh_unsupported` |
| 4 | Reading the DB fails (lock timeout, a corrupted file, SQLite errors other than locking and corruption, or a `schema_version` this version doesn't recognize), or the status table's content can't be understood (`status_json` isn't valid JSON, a field is missing, `overall` isn't one of the four values, `runs` contains `null`, or a time field isn't ISO 8601) | 4 | `error` | `schema_mismatch` when `schema_version` isn't recognized; `db_error` for everything else |
| 5 | No cache.db, no `status_snapshot` table, or no data in the table (the app has never run, or it's an older version that doesn't write the status table) | 3 | `no_data` | `no_status` |
| 6 | `--refresh` timed out | 4 | `error` | `refresh_timeout` |
| 7 | `stopped_at` has a value (the app exited normally) | 4 | `error` | `app_not_running` |
| 8 | `heartbeat_at` is older than `StatusHeartbeatStale` (180 seconds): the app isn't running, or is stuck | 4 | `error` | `stale` |
| 9 | Signed out (`signedIn == false`) | 4 | `error` | `signed_out` |
| 10 | API version error (`apiVersionError == true`) | 4 | `error` | `version_error` |
| 11 | Data too old: `monitoredRepos > 0`, and `lastUpdatedAt` is null or more than `StaleDataThreshold` (3600 seconds) before now | 4 | `error` | `data_stale` |
| 12 | `overall == failure` | 1 | `failure` | — |
| 13 | `overall == in_progress` | 2 | `in_progress` | — |
| 14 | `overall == success` | 0 | `success` | — |
| 15 | None of the above (`overall == no_data`) | 3 | `no_data` | `no_data` |

- The command line doesn't decide row 0 itself: .NET's startup program (the apphost) can't find the runtime, so the program never starts, stdout has no JSON, and stderr gets an error in English with a download link. `-2147450749` (hexadecimal `0x80008083`, our own conversion) is the PowerShell `$LASTEXITCODE` measured on 2026-11-05 by pointing `DOTNET_ROOT` at an empty folder (n=2), not on a computer that really lacks the runtime. Other runtime problems give other values: pointing `DOTNET_ROOT` at a folder with only .NET 8 on the same day gave `-2147450730` (`0x80008096`, n=2) for both `--help` and `status --json`, with the message `You must install or update .NET to run this application.` and no JSON. Scripts should treat any exit code other than 0 to 4 as the command line not having run.
- Row 4 covers content written by the app that the command line doesn't recognize (for example, an `overall` value from a newer version). When `status_json` can't be understood, `appVersion` and `heartbeatAt` still have values and the other fields written by the app are `null`; when a time field can't be understood, or there's a SQLite error, those two are `null` as well.
- When failure and in progress both apply, it returns 1: the order 1 > 2 > 0 matches the tray.
- Signed out counts as an error (4), not as no data (3): scripts need to tell "there's no data" apart from "the data can't be obtained". When `signedIn` is `null` (still checking), it doesn't count as signed out.
- A heartbeat only proves the app is still running, not that the data is fresh (for example, right after waking from sleep or starting, after a long time offline, or during a global pause), which is why row 11 exists. If auto-refresh has been set to "Paused" for more than 1 hour, it also returns `data_stale`; in that case `refreshMode` is `paused`.
- Being offline, a global pause, and persistent GitHub 5xx errors don't change the exit code by themselves; they're shown in `connection`. If they last for more than 1 hour, they turn into row 11's `data_stale`.
- **Waiting for approval counts as "in progress" (2)**, and it can last for days. If your script waits "until the exit code isn't 2", add your own limit on how long to wait.

## `--refresh`

- Asks the running app to refresh once (the same as pressing `F5`), waits for the refresh to finish, then reads; `refreshed` is then `true`. The app refreshes even when auto-refresh is set to "Paused".
- If the app is offline, paused by rate limiting, signed out, hitting an API version error, or asleep when the request arrives, it reports completion immediately with `refreshPartial: true` instead of waiting; the exit code follows the exit code table (usually `signed_out`, or based on `overall`; `data_stale` if the data is more than 1 hour old).
- It waits up to 210 seconds (one extra round if the app is already refreshing, up to 90 seconds each); on timeout it returns `refresh_timeout`, still outputs the current data, and `refreshed` is `false`. If the app exits or gets stuck during the wait, it stops waiting early; see the item "`--refresh` 提早結束等待" (`--refresh` stops waiting early) in [Deviations from the design specs](spec-deviations.md) (Traditional Chinese).
- It returns `app_not_running` when the app isn't running, `refresh_unsupported` when the app is an older version that has no refresh event, and `refresh_unreachable` when the app's named objects can't be opened (access denied, for example because the app is running as administrator).
- **Run it in the same logon session as the app**: the app receives the refresh signal through `Local\` named objects, which are visible only within the same logon session. When run from another session (for example, a scheduled task set to "Run whether user is logged on or not"), `--refresh` returns `app_not_running`. Without `--refresh`, these named objects aren't used.
- **Run it under the same Windows account as the app** (with or without `--refresh`): the command line reads the current account's `%LOCALAPPDATA%\IsItGreenYet\cache.db`. When run under another account (for example, SYSTEM, or a scheduled task set to run as another user), that account's folder doesn't have the app's status table, so without `--refresh` it always returns `no_status` (exit code 3); with `--refresh`, the session limitation in the previous point also applies (inferred from the code; not tested).

## `notices`

Prints the full text of the `LICENSE` and `THIRD-PARTY-NOTICES.md` embedded in the exe (with a divider between them). It's the same text as "License and third-party notices" in Settings, and it isn't translated: the notices are mostly in Traditional Chinese. It doesn't read `cache.db`, so it works when the app isn't running.

- Exit code 0. If the embedded text can't be read or a write reports an error, stderr gets one line of explanation in English and the exit code is 4. If the other end of a pipe closes first or stdout can't be written to, .NET doesn't report an error and the exit code is still 0 (tested on 2026-11-05), so exit code 0 doesn't guarantee complete output. This command never prints JSON.
- **Encoding**: the notices contain Chinese and other non-ASCII characters, so this command can't be pure ASCII like `status --json`. It always prints UTF-8 (without a BOM), and it neither reads nor changes the console code page. With console code page 950, Chinese text may look garbled if you read it directly in the console (that screen wasn't tested); **redirect it to a file** and open the file in a text editor:

| How you run it (code page 950) | Result |
|---|---|
| cmd: `.\IsItGreenYet-cli.exe notices > notices.txt` | Correct; the file is UTF-8 |
| pwsh 7: `.\IsItGreenYet-cli.exe notices > notices.txt` | Correct; the file is UTF-8 |
| Windows PowerShell 5.1: `cmd /c ".\IsItGreenYet-cli.exe notices > notices.txt"` | Correct; the file is UTF-8 |
| Windows PowerShell 5.1: `.\IsItGreenYet-cli.exe notices > notices.txt` | Garbled (PowerShell decodes it with the code page and saves it as UTF-16) |
| pwsh 7: through a pipeline, for example `\| Set-Content` | Garbled; correct if you first run `[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)` |

These were tested on 2026-11-05 on one computer with code page 950 (pwsh 7.6.9, Windows PowerShell 5.1), by comparing the saved files with the files in the repo. The probe in section 11.3 of the [phase 2 design spec](specs/2026-10-29-phase2-design.md) (Traditional Chinese) got the same result (UTF-8 with `>`: correct in pwsh 7, garbled in Windows PowerShell 5.1).
