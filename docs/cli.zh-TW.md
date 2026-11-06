# 命令列

[English](cli.md) | 繁體中文

回到 [README](../README.zh-TW.md)。

`IsItGreenYet-cli.exe` 讀執行中的 App 寫進 `%LOCALAPPDATA%\IsItGreenYet\cache.db` 的狀態表，輸出一個 JSON 物件，用退出碼表示整體狀態，結果和系統匣一致。電腦要有 .NET 10 Runtime（裝了 .NET 10 Desktop Runtime 就有，見 [README 的系統需求](../README.zh-TW.md#系統需求)）。

```
IsItGreenYet-cli.exe status --json [--refresh]
IsItGreenYet-cli.exe notices
IsItGreenYet-cli.exe --help
```

| 參數 | 行為 |
|---|---|
| `status --json` | 讀 `cache.db` 的狀態表，輸出一個 JSON 物件，用退出碼表示整體狀態 |
| `--refresh` | 先請正在執行的 App 刷新，等刷新完成後再讀（見下方「`--refresh`」） |
| `notices` | 輸出 exe 內嵌的 `LICENSE` 與第三方聲明全文（見下方「`notices`」），不讀 `cache.db` |
| `--help` | 用法印到 stdout，退出碼 0 |
| 其他（不認得的參數、少了 `--json`） | 用法印到 stderr，退出碼 4，不輸出 JSON |

- `status` 要在第一個；`--json` 與 `--refresh` 順序不限、各一次，大小寫要完全相同。`notices`、`--help` 要單獨使用，和其他參數一起出現時算參數錯誤（退出碼 4）。
- 狀態來自 App：App 執行時每 60 秒，以及狀態改變時，寫一次狀態表與心跳時間。命令列自己不打 GitHub API、不需要 token，所以 App 已結束或停住時回錯誤（4）；App 從沒執行過時回 `no_status`（3），見退出碼表。
- 只讀資料：不改 `cache.db` 的內容，不讀也不改 `settings.json`；`cache.db` 不存在時不建立。`cache.db` 是 WAL 模式，唯讀連線可能在旁邊留下 `cache.db-wal`、`cache.db-shm`。`cache.db` 被鎖住時最多等 5 秒，損毀時不改名、不修復（回 `db_error`）。
- 是主控台程式：pwsh、Windows PowerShell 與 cmd 都會等它結束，拿得到退出碼（`$LASTEXITCODE`、`%ERRORLEVEL%`）。App 本身是視窗程式，PowerShell 不等它結束、拿不到退出碼，所以命令列另外做成一個執行檔。

## 輸出

這一節與下面的退出碼表講的是 `status --json`；`notices` 見最後一節。

- stdout 只有一個 JSON 物件：一行、不縮排，結尾換行。`state` 是 `error` 時，stderr 寫一行英文說明，例如 `Error: The IsItGreenYet app is not running in this session. (reason: app_not_running)`；參數錯誤時 stderr 是用法（多行），沒有 JSON。
- 整個輸出是純 ASCII：非 ASCII 字元（中文的 repo 名稱、workflow 名稱、標題）一律跳脫成 `\uXXXX` 的形式，版本字串裡的 `+` 也會被跳脫（`\u002B`），解析後的值不變。直接在主控台看 JSON 時，中文會顯示成跳脫字串；用 PowerShell 的 `ConvertFrom-Json`（或 `jq`）解開就是原本的文字。純 ASCII 不受主控台字碼頁影響，用 `>` 存檔也不會變亂碼。

```powershell
$status = .\IsItGreenYet-cli.exe status --json | ConvertFrom-Json
$exitCode = $LASTEXITCODE
$status.state
$status.runs | Format-Table repo, workflow, state, title
```

- 鍵名固定是英文 camelCase，不隨介面語言改變。時間一律是 UTC 的 ISO 8601（結尾 `Z`，例如 `2026-10-29T02:00:00Z`；有小數秒時照寫，見[和設計規格的差異](spec-deviations.md)）。
- 讀不到狀態表時（沒有 `cache.db` 等，見退出碼表），`appVersion`、`heartbeatAt` 與其他 App 寫的欄位是 `null`、`runs` 是空陣列；其他錯誤時只要讀得到狀態表，照樣附上讀到的資料，方便判斷原因。

JSON 的欄位（依輸出的順序）：

| 欄位 | 誰寫 | 內容 |
|---|---|---|
| `schemaVersion` | 命令列 | 這個 JSON 結構的版本，目前是 `1`；欄位只增不改，改變意義時加 1 |
| `state` | 命令列 | 依退出碼表判斷的結果：`failure`、`in_progress`、`success`、`no_data`、`error`。沒有錯誤時等於 `overall` |
| `exitCode` | 命令列 | 和程式的退出碼相同 |
| `reason` | 命令列 | `state` 是 `error` 或 `no_data` 時的原因代碼（見退出碼表），其他時候是 `null` |
| `cliVersion` | 命令列 | 命令列自己的版本（`0.3.0+<40 字元的 commit>`） |
| `appVersion` | App | 寫入狀態表的 App 的版本（格式同上） |
| `generatedAt` | 命令列 | 命令列產生這份輸出的時間 |
| `snapshotAt` | App | 這份狀態對應的 App 快照時間 |
| `heartbeatAt` | 命令列（讀表） | App 最後一次寫入狀態表的時間（心跳） |
| `refreshed` | 命令列 | 帶 `--refresh`，而且等到 App 這次刷新完成時是 `true`；其他時候是 `false` |
| `refreshPartial` | 命令列 | `refreshed` 是 `true`，而且這次刷新沒有完整做完（超過 90 秒，或被離線、限流暫停、未登入等擋住）時是 `true`；其他時候是 `false` |
| `overall` | App | 系統匣的整體狀態：`failure`、`in_progress`、`success`、`no_data`。「全部成功」要至少一個追蹤項目的最新結論是成功，只有 App 知道，所以命令列不從 `counts` 自己推。第一次執行或快取資料庫重建之後，全部查完之前不會是 `success`（沒有失敗或進行中時是 `no_data`，退出碼 3；已經有監看中的 repo、第一個 repo 還沒查回來時，退出碼是 4 `data_stale`，見[已知限制第 33 項](known-limitations.zh-TW.md)） |
| `signedIn` | App | `true`：已登入；`false`：未登入（收到 401 之後）；`null`：還在確認（App 剛啟動，或離線啟動、還沒驗證完） |
| `apiVersionError` | App | 是否發生 API 版本錯誤（400／410，App 已停止所有輪詢）。不放在 `connection` 裡，因為它會讓退出碼變成 4 |
| `refreshMode` | App | 自動更新的設定值：`auto`、`30s`、`1m`、`5m`、`paused`（同 `settings.json` 的 `refreshMode`） |
| `counts` | App | 和系統匣相同的計數：`failing` 是失敗的追蹤項目數，`inProgress` 是進行中或排隊（含等待核准）的追蹤項目數 |
| `monitoredRepos` | App | 監看中的 repo 數 |
| `staleRepos` | App | 無法更新的 repo 數 |
| `connection` | App | `offline`（離線）、`pausedUntil`（限流的全域暫停預定恢復的時間，沒有暫停時是 `null`）、`serverErrors`（GitHub 持續回 5xx）。這三項不改變 `state` 與退出碼，和系統匣只加角標一致；持續超過 1 小時會變成 `data_stale` |
| `lastUpdatedAt` | App | 監看中 repo 最近一次成功回應的時間（重新啟動後保留）；還沒有任何成功回應時是 `null` |
| `runs` | App | 「只看需要注意」的那些 run（追蹤中的失敗，加上所有未完成與等待核准的 run），排序同主清單；沒有時是空陣列 |
| `runs[].repo`、`workflow` | App | repo 的完整名稱（`owner/name`）、workflow 名稱 |
| `runs[].runId`、`runNumber`、`attempt` | App | run 的 id、編號（畫面上的 `#N`）、第幾次嘗試 |
| `runs[].title`、`branch`、`event` | App | 標題、分支（可能是 `null`）、觸發事件（例如 `push`、`pull_request`） |
| `runs[].status`、`conclusion` | App | GitHub 給的原值，兩者都可能是 `null`（還沒完成時 `conclusion` 一定是 `null`） |
| `runs[].state` | App | 判定類別：`pending`（未完成）、`waiting`（等待核准）、`success`、`failure`、`neutral`（灰色：取消、略過、`neutral`、`stale`、完成後不認得的結論） |
| `runs[].cancelling` | App | 是否「取消中」（在這個 App 裡送出的取消已受理，run 還沒完成） |
| `runs[].startedAt`、`url` | App | 開始時間（可能是 `null`）、這筆 run 的網頁網址 |

## 退出碼

依下表由上往下判斷，第一個成立的就是結果：

| 順序 | 條件 | 退出碼 | `state` | `reason` |
|---|---|---|---|---|
| 0 | 找不到 .NET Runtime，程式沒有啟動（見表格下方第一點） | `-2147450749` | （不輸出 JSON） | — |
| 1 | 參數錯誤 | 4 | （不輸出 JSON） | — |
| 2 | 未預期的例外 | 4 | `error` | `internal_error` |
| 3 | `--refresh` 的前兩步失敗：檢查 App 是否在執行、開啟刷新用的事件（見下方「`--refresh`」） | 4 | `error` | `app_not_running`、`refresh_unreachable`、`refresh_unsupported` |
| 4 | 讀 DB 失敗（鎖定逾時、檔案損毀、鎖定與損毀以外的 SQLite 錯誤、`schema_version` 不是這個版本認得的值），或狀態表的內容讀不懂（`status_json` 不是合法 JSON、缺欄位、`overall` 不是四種值之一、`runs` 裡有 `null`，或時間欄不是 ISO 8601） | 4 | `error` | `schema_version` 不認得時是 `schema_mismatch`，其他都是 `db_error` |
| 5 | 沒有 cache.db、沒有 `status_snapshot` 表、或表裡沒有資料（App 從沒執行過、或是還沒有狀態表的舊版本） | 3 | `no_data` | `no_status` |
| 6 | `--refresh` 逾時 | 4 | `error` | `refresh_timeout` |
| 7 | `stopped_at` 有值（App 已正常結束） | 4 | `error` | `app_not_running` |
| 8 | `heartbeat_at` 超過 `StatusHeartbeatStale`（180 秒）：App 沒有在執行，或停住了 | 4 | `error` | `stale` |
| 9 | 未登入（`signedIn == false`） | 4 | `error` | `signed_out` |
| 10 | API 版本錯誤（`apiVersionError == true`） | 4 | `error` | `version_error` |
| 11 | 資料過舊：`monitoredRepos > 0`，而且 `lastUpdatedAt` 是 null，或早於現在超過 `StaleDataThreshold`（3600 秒） | 4 | `error` | `data_stale` |
| 12 | `overall == failure` | 1 | `failure` | — |
| 13 | `overall == in_progress` | 2 | `in_progress` | — |
| 14 | `overall == success` | 0 | `success` | — |
| 15 | 以上都不成立（`overall == no_data`） | 3 | `no_data` | `no_data` |

- 第 0 列不是命令列自己判斷的：.NET 的啟動程式（apphost）找不到 Runtime，程式根本沒有開始執行，所以 stdout 沒有 JSON，stderr 是英文的錯誤訊息與下載連結。`-2147450749`（十六進位 `0x80008083`，自算）是 2026-11-05 用 `DOTNET_ROOT` 指空資料夾模擬量到的 PowerShell `$LASTEXITCODE`（n=2），沒有在真的沒裝 Runtime 的電腦上試過。其他 Runtime 問題是別的值：同一天用 `DOTNET_ROOT` 指到只有 .NET 8 的資料夾，`--help` 與 `status --json` 都是 `-2147450730`（`0x80008096`，n=2），訊息是 `You must install or update .NET to run this application.`，一樣沒有 JSON。腳本要把 0～4 以外的退出碼都當成命令列沒有執行。
- 第 4 列包含 App 寫進命令列不認得的內容（例如新版本的 `overall` 值）：`status_json` 讀不懂時 `appVersion`、`heartbeatAt` 照樣有值，其他 App 寫的欄位是 `null`；時間欄讀不懂或 SQLite 錯誤時兩者也是 `null`。
- 失敗與進行中同時成立時回 1：1 > 2 > 0 的順序和系統匣一致。
- 未登入算錯誤（4），不算沒有資料（3）：腳本要分得出「沒有資料」和「沒辦法拿到資料」。`signedIn` 是 `null`（還在確認）時不算未登入。
- 心跳只能證明 App 還在跑，不能證明資料是新的（例如剛睡醒、剛啟動、長時間離線或全域暫停），所以另外有第 11 列。自動更新設成「暫停」超過 1 小時也會回 `data_stale`，這時 `refreshMode` 是 `paused`。
- 離線、全域暫停、GitHub 持續 5xx 本身不改變退出碼，由 `connection` 表示；持續超過 1 小時就會變成第 11 列的 `data_stale`。
- **等待核准算「進行中」（2）**，可能持續好幾天。腳本用「等到退出碼不是 2」的寫法時，要自己加上等待的上限。

## `--refresh`

- 請正在執行的 App 刷新一次（同按 `F5`），等刷新完成後再讀，這時 `refreshed` 是 `true`。自動更新設成「暫停」時照樣刷新。
- App 收到時已經離線、被限流暫停、未登入、API 版本錯誤或在睡眠中，會立即回報完成並標 `refreshPartial: true`，不等；退出碼照退出碼表（通常是 `signed_out`，或照 `overall`；資料超過 1 小時則是 `data_stale`）。
- 最多等 210 秒（App 正在刷新時要多等一次，每次最長 90 秒）；逾時回 `refresh_timeout`，照樣輸出目前的資料，`refreshed` 是 `false`。App 在等待途中結束或停住時會提早結束等待，見[和設計規格的差異](spec-deviations.md)的「`--refresh` 提早結束等待」。
- App 沒在執行時回 `app_not_running`；App 是沒有刷新用事件的舊版本時回 `refresh_unsupported`；開不到 App 的具名物件（存取被拒，例如 App 以系統管理員身分執行）時回 `refresh_unreachable`。
- **要在 App 所在的同一個登入工作階段裡執行**：App 用 `Local\` 的具名物件接收刷新訊號，只有同一個登入工作階段看得到。從另一個工作階段執行（例如設成「不論使用者是否登入都執行」的排程工作）時，`--refresh` 會回 `app_not_running`。不帶 `--refresh` 時不用這些具名物件。
- **要用和 App 同一個 Windows 帳號執行**（帶不帶 `--refresh` 都一樣）：命令列讀的是目前帳號的 `%LOCALAPPDATA%\IsItGreenYet\cache.db`。用別的帳號（例如 SYSTEM，或排程工作設成別的使用者）執行時，那個帳號的資料夾裡沒有 App 的狀態表，不帶 `--refresh` 一律回 `no_status`（退出碼 3）；帶 `--refresh` 時另外受上一點的工作階段限制（讀程式推得，未實測）。

## `notices`

輸出 exe 內嵌的 `LICENSE` 與 `THIRD-PARTY-NOTICES.md` 全文（中間隔一條分隔線），和設定頁的「授權與第三方聲明」是同一份內容，沒有翻譯。不讀 `cache.db`，App 沒在執行也能用。

- 退出碼 0。內嵌的內容讀不到，或寫入時回報錯誤，stderr 寫一行英文說明，退出碼 4。管線另一端先關閉、stdout 不能寫入時，.NET 不回報錯誤，退出碼仍是 0（2026-11-05 實測），所以退出碼 0 不保證輸出完整。這個指令不輸出 JSON。
- **編碼**：聲明有中文與其他非 ASCII 字元，做不到 `status --json` 的純 ASCII。這個指令固定輸出 UTF-8（沒有 BOM），不看也不改主控台字碼頁。主控台字碼頁是 950 時，直接在主控台看，中文可能是亂碼（這個畫面沒有實測）；**導向檔案**，再用文字編輯器開：

| 怎麼接（字碼頁 950） | 結果 |
|---|---|
| cmd：`.\IsItGreenYet-cli.exe notices > notices.txt` | 正確，檔案是 UTF-8 |
| pwsh 7：`.\IsItGreenYet-cli.exe notices > notices.txt` | 正確，檔案是 UTF-8 |
| Windows PowerShell 5.1：`cmd /c ".\IsItGreenYet-cli.exe notices > notices.txt"` | 正確，檔案是 UTF-8 |
| Windows PowerShell 5.1：`.\IsItGreenYet-cli.exe notices > notices.txt` | 亂碼（PowerShell 用字碼頁解讀後另存成 UTF-16） |
| pwsh 7：接管線，例如 `\| Set-Content` | 亂碼；先執行 `[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)` 就正確 |

以上是 2026-11-05 在一台字碼頁 950 的電腦實測（pwsh 7.6.9、Windows PowerShell 5.1），比對存下來的檔案和 repo 的檔案。第二階段設計規格 §11.3 的探針也是同樣的結果（UTF-8 的 `>`：pwsh 7 正確、Windows PowerShell 5.1 亂碼）。
