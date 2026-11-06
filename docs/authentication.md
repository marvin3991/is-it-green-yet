# Authentication and permissions

English | [繁體中文](authentication.zh-TW.md)

Back to the [README](../README.md).

## First run: sign in

By default the app reuses your gh CLI sign-in; once [GitHub CLI](https://cli.github.com/) is installed, there's no other setup:

```powershell
gh auth login
```

At startup the app runs `gh auth token --hostname github.com` to get a token and keeps it only in memory. If you don't want the app to use gh's token, you can use a fine-grained PAT instead (see [Fine-grained PAT (alternative)](#fine-grained-pat-alternative) below). You can also start the app before signing in: after you sign in, click "Check again" on the banner, or wait for the app's automatic check every 5 minutes (see [When signed out](#when-signed-out)).

## gh CLI (default)

- The token is kept only in memory and never written to any file. On a 401, the app gets the token again and retries only once. This avoids sending invalid credentials repeatedly, which could make GitHub temporarily block all of your authentication (gh itself included).
- **Trade-off**: a token obtained through gh's browser/device sign-in always has at least the `repo` scope, which means full read/write access to public and private repos. With write actions off (the default), this tool sends only `GET`, so "read-only" relies only on the program's own discipline; the permissions don't guarantee it. For read-only enforced by permissions, use a fine-grained PAT instead. Conversely, with write actions on, the same gh token can re-run and cancel with no extra setup (if the scope isn't enough, see [Permissions for write actions](#permissions-for-write-actions) below).
- The environment variables `GH_TOKEN`/`GITHUB_TOKEN` override gh's own stored credentials. After you change either of them, restart IsItGreenYet. In Settings you can switch to "Ignore GH_TOKEN/GITHUB_TOKEN when running gh".
- The `X-OAuth-Scopes` check is shown only after you click "Check connection" in Settings: if the header has a value but doesn't include `repo`, it shows "This token may not be able to see private repos"; if it includes `repo` or the header is absent, it shows "No issues found".

## When signed out

After a 401, if the token obtained again is the same as before, can't be obtained, or the resend still gets a 401, the app goes into the signed-out state: all polling stops, the main window shows a banner, the tray icon gets a badge, and existing data isn't cleared. To recover:

- **gh mode**: run `gh auth login` in a terminal. Every 5 minutes the app re-runs `gh auth token` locally (no API call); when the token changes, it verifies it once with `GET /user` and resumes polling if that succeeds. If you don't want to wait, click "Check again" on the banner (or in the empty list) to get the token again and verify it right away.
- **PAT mode**: paste a new PAT in Settings and save.
- **Token from an environment variable**: update `GH_TOKEN`/`GITHUB_TOKEN`, then restart IsItGreenYet (a process that's already running doesn't see changes to environment variables).

## Fine-grained PAT (alternative)

How to create one (GitHub → Settings → Developer settings → Personal access tokens → Fine-grained tokens; these steps follow GitHub's documentation and haven't actually been tried in the web UI yet):

1. **Resource owner**: choose yourself.
2. **Repository access**: choose All repositories, or only the repos you want to monitor.
3. **Permissions**: turn on only the three read-only permissions below; to use write actions, change Actions to Read and write:

   | Permission | Used for | Needed? |
   |---|---|---|
   | Actions: Read-only | Workflow list, run list and single run, job list, downloading job logs | Yes |
   | Metadata: Read-only | Repo list (`GET /user/repos`) | Yes |
   | Pull requests: Read-only | Open PR list (to decide which PR branches to track) | Yes |
   | Contents: Read-only | No endpoint the app uses needs it, and it also grants read access to your code | **No** |
   | Actions: Read and write | Re-running and cancelling runs in the app | Needed for write actions (write actions are off by default; if you don't use them, you don't need it) |

4. In Settings, under "Account", switch to PAT, paste the token (for example `<YOUR_TOKEN>`), and click "Save". Before saving, the PAT is verified with `GET /user`, and it isn't saved if that fails; within the same Settings window, a PAT that got a 401 isn't sent a second time. While rate limited by GitHub (global pause), or when the API version is no longer supported, the PAT can't be verified or saved.

The PAT is stored in Windows Credential Manager as a generic credential with the target name `IsItGreenYet:github.com`, visible only on this computer to the same user (not roaming). `settings.json` records only the sign-in method, not the token.

- If PAT is selected but no PAT is available (none newly pasted, and none in Credential Manager), you can't save, and it shows "Paste a PAT first when PAT is selected."
- "Clear" deletes the PAT from Credential Manager only when you click "Save"; "Cancel" leaves it as it was. While the sign-in method is still PAT, you can't just clear it: switch back to gh CLI first and save, or paste a new PAT to replace it.

**Limitations** (based on GitHub's official documentation; items marked "inferred" aren't stated explicitly in the docs and haven't been tested):

- A fine-grained PAT can have only one resource owner (one user or one organization), and the app currently supports only one PAT.
- Outside collaborators on organization repos can access them only with a classic PAT.
- Private repos owned by another personal account where you're only a collaborator: the resource owner can only be yourself, so these are inferred to be unreadable.
- Organizations require approval for fine-grained PATs by default, and before approval only public resources can be read; the organization's default maximum lifetime is 366 days.
- A fine-grained PAT always has read-only access to all public repos, so with insufficient permissions the repo list may still succeed, just without the private repos, and no error is reported. "Check connection" in Settings shows "N repos visible (M private)" so you can spot this.
- When a repo's Actions endpoint returns 404, the PAT may not include that repo, organization approval may still be pending, the expiration may not match, or SSO may not be authorized; the app flags the reason and switches to retrying once an hour.
- Whether `GET /user/repos` returns only the selected repos when using a fine-grained PAT isn't documented (inferred, not yet tested).

## Permissions for write actions

Re-running and cancelling need a token that can write: a classic token or gh's OAuth token needs the `repo` scope; a fine-grained PAT needs Actions: Read and write. The app doesn't send extra requests to check. Instead, it disables write items in advance or afterwards in the three cases below; hover over a disabled item to see why:

| Case | How the app knows | What happens |
|---|---|---|
| The token's scope isn't enough | The response's `X-OAuth-Scopes` has a value but doesn't include `repo`. As for whether a classic token with only `public_repo` can write to public repos, GitHub's endpoint docs mention only `repo`, so that's treated as not enough too (speculative) | All writes for this account are disabled in advance. When `X-OAuth-Scopes` has no value (fine-grained token), it's unknown, so nothing is disabled |
| You don't have write access to this repo | `permissions.push` returned by the repo list (`GET /user/repos`) is `false` | Writes for this repo are disabled in advance; when the response has no `permissions`, it's unknown, so nothing is disabled |
| The fine-grained token doesn't have Actions write | Can't be checked in advance; the app finds out only when GitHub rejects a request for insufficient permissions (403) after it's sent | Writes for this repo are disabled. This is kept only in memory, and is lifted when you switch tokens, restart the app, or click "Check connection" in Settings (a PAT's permissions may have been changed on the web while the token string stays the same). Switching tokens only suspends it: switching back to the original token makes the block apply again, and only "Check connection" (or restarting the app) lifts it |

- gh mode can also get a fine-grained token (for example via `GH_TOKEN`), so the app doesn't assume gh mode can always write.
- Cases where organization policy, SSO, or repo settings make Actions unusable can't be detected in advance; they're left to GitHub's response.
- On the first start after upgrading from a version without write actions, the repo list is fetched once in full (without an ETag) to read `permissions`; after that, ETags are sent as usual.
