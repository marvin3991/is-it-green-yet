# Building from source

English | [繁體中文](building.zh-TW.md)

Back to the [README](../README.md).

## Build

Requirements: .NET SDK 10 (`global.json` requires `10.0.401` or later and allows newer feature bands) and PowerShell 7. Developed and tested on Windows 11 (x64).

```powershell
git clone https://github.com/marvin3991/is-it-green-yet.git IsItGreenYet
cd IsItGreenYet
pwsh -File tools/publish.ps1
```

The script first runs the full unit test suite and produces no output if any test fails; it then regenerates the icon, and finally writes two win-x64 single-file executables to `dist\` in one go:

## The two executables

| File | What it is | Size | Requirements |
|---|---|---|---|
| `IsItGreenYet.exe` | App | 3,658,240 bytes (about 3.49 MiB) | The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) must be installed |
| `IsItGreenYet-cli.exe` | [Command line](cli.md) | 3,334,668 bytes (about 3.18 MiB) | The .NET 10 Runtime must be installed (installing the .NET 10 Desktop Runtime covers this: the Desktop Runtime installer includes the .NET Runtime) |

The sizes are from one build of version `0.3.1` (MiB = 1024² bytes) and change from build to build. Both are framework-dependent and don't include the .NET runtime; the first release has no self-contained builds, which differs from the specs (see [Deviations from the design specs](spec-deviations.md) (Traditional Chinese)). For the command that installs the runtime and the message you get when it's missing, see [System requirements](../README.md#system-requirements) in the README. `dist\` contains only these two files. The command line's `status --json` only reads the status written by the app, and the command line never calls the GitHub API itself, so `status --json` has to be used with the app running (see [Command line](cli.md)).

## Running the executables

- Single file, no installer, and the app itself writes nothing to Program Files: once the runtime is installed, copy the exe to a folder and run it. Don't put it in a folder that other people or your browser can write to (such as Downloads): a DLL or `gh.exe` with a matching name next to the exe gets loaded ([Known limitations](known-limitations.md), item 37). The app writes its own data only to `%LOCALAPPDATA%\IsItGreenYet`, the Start with Windows value under HKCU, and Windows Credential Manager (when using a PAT), so by design the exe itself doesn't need administrator rights (running with standard rights hasn't been tested); installing the runtime may need them (see [System requirements](../README.md#system-requirements) in the README).
- The required native DLL (SQLite's `e_sqlite3.dll`) is bundled into the exe and is extracted at startup to `%TEMP%\.net\<executable name>\<hash>\`. Each new version adds another hash subfolder, and old ones aren't cleaned up automatically; while the app isn't running, you can delete the old folders under `%TEMP%\.net\IsItGreenYet*\` by hand.
- Keep the executable in a fixed location: Windows 11 remembers a tray icon's pinned position by the executable's path (according to third-party documentation; not officially confirmed).
- When the tray icon first appears, it's in Windows 11's hidden icons area; whether to pin it to the taskbar is up to you. Its name under Settings > Personalization > Taskbar > Other system tray icons is `IsItGreenYet`.
- The executables aren't code-signed; see the next section.

## No code signing

The published exes aren't code-signed. SmartScreen's reputation looks at the file hash and the certificate, and without signing each new version starts from zero ([Microsoft documentation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)), so every version may show a warning. Users should go in this order; the commands are in the [README](../README.md#no-code-signing):

1. Download only from this project's Releases.
2. If Edge holds back the download, check the address, then choose to keep the file in the download list (the exact button text hasn't been verified); if Edge says outright that the file is harmful, don't keep it.
3. Move it to a folder of your own; don't leave it in Downloads.
4. Compare the hashes. This only guards against a damaged download, not a swapped file.
5. Double-click `IsItGreenYet.exe`; run `IsItGreenYet-cli.exe` from PowerShell.
6. Press "Run anyway" only if the screen says it's an unrecognized app from an unknown publisher.
7. On a red block, or if Microsoft Defender quarantines the file, stop and open an issue.

A computer with [Smart App Control](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview) on may not be able to run the exes at all (not tested), and company policies may block them too. The docs don't teach turning off SmartScreen, wildcard `Unblock-File`, or working around company policies. The SmartScreen screens were seen in step 4 of the [release process](#release-process) for 0.3.0, and their text is in the README; the Edge download screens haven't been seen yet.

## `publish.ps1` parameters

| Parameter | Description |
|---|---|
| `-SkipTests` | Skip the unit tests (shows a warning) |
| `-OutputDirectory <path>` | Output folder; `dist\` by default. The whole folder is deleted and recreated (it doesn't go through the Recycle Bin), so it can't point to a drive root, the repo root, or a parent folder of the repo. If the path is a file, or the folder contains anything other than the two published files (including hidden files and subfolders; `-selfcontained` exes left over from older versions count too, so delete them by hand first), the script stops before running the tests, names the first item that doesn't match, and deletes nothing |

## Development commands

```powershell
dotnet build
dotnet test
dotnet run --project src/IsItGreenYet.App
dotnet run --project src/IsItGreenYet.Cli -- status --json
```

## Regenerating the icon

To regenerate only the icon (`-PreviewPath` also writes an enlarged 256/32/16 preview image):

```powershell
pwsh -File tools/generate-icon.ps1 -PreviewPath docs/icon-preview.png
```

## Release process

`.github/workflows/release.yml` builds the published files into a **draft** release when a `v*.*.*` tag is pushed; the project author checks the contents and publishes it by hand.

1. Change `<Version>` in `src/IsItGreenYet.App/IsItGreenYet.App.csproj` and `src/IsItGreenYet.Cli/IsItGreenYet.Cli.csproj`; the two must match.
2. Open a PR and merge it.
3. Create a tag on `main` and push it. The tag is `v` plus the version (for example `v0.3.0`): `git tag v0.3.0`, `git push origin v0.3.0`.
4. When the workflow finishes, check the draft on the Releases page: the two exes, `SHA256SUMS.txt`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, and the generated release notes. Then verify the exes built by CI by hand:
   - Look at the "檢查發行的 exe" (check the exes) step of the build job (`tools/check-release-exes.ps1`). It checks that the bundle list, the hash of `e_sqlite3.dll`, the host section, and the copyright field still match what [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) is based on; if anything differs, the workflow stops and there's no draft. Compare the SDK version, apphost source, and apphost version string it prints with the previous release and the notices, and record them in [testing/acceptance.md](testing/acceptance.md) together with the hashes of both exes. If something differs, find out why first; a draft that hasn't been published can be deleted, and you can delete the tag and start over.
   - Download the draft's exes and `SHA256SUMS.txt` with a browser. Only a browser adds the mark of the web (`Zone.Identifier`): `curl.exe` and `Invoke-WebRequest` don't write it (tested locally on 2026-11-05), and neither does `gh release download` (run on 2026-11-06).
   - Move them to a folder of your own instead of leaving them in Downloads. Moving within the same drive keeps the mark of the web (inferred; not tested).
   - Verify the SHA256 with the command in the [README](../README.md#no-code-signing).
   - First exit the running app from the tray: the app is single-instance, so a second copy only brings up the first one and exits, and you'd be looking at the old version. Start the draft's app, wait for the main window, then run the command line (`IsItGreenYet-cli.exe status --json`) and check that `appVersion` is this tag's version and `reason` is neither `app_not_running` nor `stale`. With either reason, `appVersion` is what the previous copy left: the new app hasn't written yet or didn't start, so wait a few seconds and run it again. Note the SmartScreen and Edge screens, and add the real text to the README and to [No code signing](#no-code-signing) above.
   - Mark-of-the-web test: use the commands below to copy the exe twice and write `ZoneId=2` and `ZoneId=3` into `Zone.Identifier`, then double-click each copy in File Explorer and compare which ones show a warning. The app is single-instance, so exit the running copy before trying the other. The computer must be online. If Zone 3 doesn't show a warning either, this comparison doesn't count and says nothing about Zone 2; whether having pressed "Run anyway" for the same hash earlier affects the result is unknown. Record the result: winget marks the files it installs as Zone 2 (read from the winget-cli source code; it requires a matching hash and a trusted source), so if Zone 2 still shows a warning, a winget package won't remove it (inferred). When you're done, delete both copies and `%TEMP%\.net\IsItGreenYet-zone*`.

     ```powershell
     Copy-Item .\IsItGreenYet.exe .\IsItGreenYet-zone2.exe
     Copy-Item .\IsItGreenYet.exe .\IsItGreenYet-zone3.exe
     Set-Content -LiteralPath .\IsItGreenYet-zone2.exe -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=2"
     Set-Content -LiteralPath .\IsItGreenYet-zone3.exe -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
     ```

   - Replace the generated release notes with a short handwritten version in Chinese and English that says which file to download, that the exes aren't signed, and where the notices are.
5. Press Publish.

A tag containing `-` (for example `v0.3.0-beta.1`) is marked as a prerelease. `/releases/latest` doesn't return prereleases, so the app doesn't notify about those versions.

Don't withdraw or delete a published release; if something is wrong, publish a newer fixed version. The app remembers only one notified version, so after a withdrawal it stays silent about an older version it never announced (item 45 in [Known limitations](known-limitations.md)).

If any of these doesn't hold, the workflow stops and no release is created:

- The tag without `v` equals `<Version>` in both csproj files and is valid SemVer.
- The commit the tag points to is on `main`, and the tag still points to the commit that was built when the release is created (the workflow stops if the tag moved during the build).
- `THIRD-PARTY-NOTICES.md` doesn't contain the `RELEASE-BLOCKER` marker. The marker means the third-party notices have open items: add it when there's a new one, and delete that line once it's settled.
- All checks in `tools/check-release-exes.ps1` pass (see step 4).
- No release with the same name exists (drafts included); nothing is overwritten.

You can run a dry run before tagging: run Release by hand on the Actions page, or run `gh workflow run release.yml --ref main` (`--ref` can also be a branch). It checks that the two csproj versions match, runs the full unit tests, builds and checks the two exes, computes the hashes, and uploads the output as the artifact `release-files` (kept for 7 days) without creating a release; the `RELEASE-BLOCKER` marker only shows a warning.
