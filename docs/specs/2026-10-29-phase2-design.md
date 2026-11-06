# IsItGreenYet 設計規格（第二階段）

- 日期：2026-10-29
- 狀態：**已確認**（2026-10-29），已實作並在 2026-10-30 合併（PR #6，版本 0.2.0）。§17 的決策 D1～D15 都依建議定案；§17 另外列出我自己決定、你可以改的設計選擇。實機驗收結果見 [acceptance.md](../testing/acceptance.md)，實作和規格不同的地方見 [spec-deviations.md](../spec-deviations.md)
- 依據：
  - 第一階段設計規格 [`2026-10-24-design.md`](2026-10-24-design.md)，以下簡稱 **P1**，引用寫成「P1 §6.4」。
  - main 的 `687813d`（PR #4 合併後）。
  - 外部文件：GitHub 官方文件與 changelog（[W1]～[W10]）、Microsoft Learn（[W11]～[W13]），見 [§18](#18-參考來源)。
  - 本機實驗：命令列子系統、`cache.db` 同時讀取、輸出編碼（§11.1～§11.3）。
- 這份文件取代 P1 §19.1、§19.2。沒有寫到的地方，一律照 P1。
- 審查：完稿後由 4 個角度的審查者（程式描述、前後一致、設計漏洞、外部事實）與 1 個模擬實作者審過一輪，每組發現再由另一個代理獨立複查；58 項中 56 項屬實，已全部處理。第二輪收斂式複查確認第一輪全部修好，另找到 6 項修訂造成的新問題（中等 4、低 2），本版也已處理。

---

## 0. 結論先講

1. **範圍**：做 P1 §19 預留的兩件事。
   - 在 run 選單裡重跑與取消：重跑失敗的 job、整個 run 重跑、取消、強制取消。
   - 命令列 `IsItGreenYet-cli.exe status --json`。
   - 輪詢、監看範圍、追蹤規則、通知條件都不變。
2. **寫入功能預設關閉**（D1）。關閉時只送 GET，run 選單沒有寫入項目。有幾項改變不受這個開關控制，列在 §1.3。
3. **P1 §19 有三處要修正**：
   - 介面少了強制取消（P1 §19.1 的介面只有兩個方法，表格卻列了 force-cancel）。
   - 「cancel 回 202 後最多 5 分鐘才會變成 completed」不保證成立。官方的取消流程：`if` 條件為 true 的 job 不會被取消，5 分鐘後只強制終止「已標記取消、還在跑」的 job 與 step [W3]。所以「取消中」要一直顯示到 run 真的完成。
   - 命令列不能做在現有的 GUI exe 上。實驗結果：PowerShell 不等 GUI exe 結束，`$LASTEXITCODE` 是空的（§11.1）。改成另做一個主控台執行檔。
4. **Engine 新增兩個機制**，少了任何一個，功能都會壞：
   - **操作後追蹤**（§6）：目前已完成、又不在 runs 第 1 頁的 run 不會再被查。重跑一筆舊 run 之後，可能永遠看不到新的 attempt。待確認的重跑存進 DB，App 重新啟動後接著追。
   - **寫入專用的送出路徑**（§5）：複製 log 的路徑遇到全域暫停會等暫停結束再送，重跑如果照這樣做，可能在幾十分鐘後才送出。寫入改成等太久就放棄，回報「沒有送出」。
5. **命令列讀 App 寫入的狀態表**（§10）：App 把整體狀態與心跳時間寫進 `cache.db` 的新資料表，命令列只讀這張表，結果和系統匣一致。資料太舊、App 沒在執行時回錯誤。`--refresh` 改成請正在執行的 App 刷新，命令列自己不打 API。

---

## 1. 範圍

### 1.1 做

| 項目 | 章節 |
|---|---|
| 四個寫入端點與共通規則 | §3 |
| 寫入權限的判斷 | §4 |
| Engine 的寫入流程、結果分類 | §5 |
| 操作後追蹤、「取消中」、強制取消的出現時機 | §6 |
| 每一列能不能操作 | §7 |
| 重跑與通知、ETA | §8 |
| App 的選單、確認對話框、結果顯示、設定頁、字串 | §9 |
| 命令列、狀態表、`--refresh` | §10、§11 |
| 發行（dist 變成 4 個 exe） | §12 |

### 1.2 不做

| 項目 | 原因 |
|---|---|
| 單一 job 重跑（`POST .../jobs/{job_id}/rerun`） | 要先打 E8，還要做 job 層級的介面 [W1]。之後有需要再加 |
| `enable_debug_logging`、`enable_debugger` | 一律不送，等於預設值 false [W1] |
| 核准 fork run（`/approve`）、pending deployments | 需求沒有；pending deployments 要 Deployments write，不是 Actions write [W6] |
| 批次「全部重跑」或「全部取消」 | 需求沒有；一次誤按的影響太大 |
| 提示視窗上的操作按鈕 | 提示視窗設計成不搶焦點（P1 §9.4），確認對話框一定會搶 |
| 系統匣選單的單筆 run 操作 | 系統匣選單沒有單筆 run 的概念（P1 §12） |
| 寫入操作的快速鍵 | P1 §10.12 沒有；尤其不拿 Delete 當取消，避免誤觸 |
| 命令列自己打 API | 理由見 D11 |
| 第一階段延後的項目（P1 Q10、Q11、Q16、Q17，[已知限制](../known-limitations.zh-TW.md)，DWM 外框，錯誤對話框的「開啟資料夾」） | 不在這次範圍；要做另外提 |
| 第三階段（MCP server、外部 check run，P1 §19.3） | 第三階段 |

### 1.3 不受寫入開關控制的改變

寫入功能關閉時，下列改變照樣生效。驗收時拿第一階段的行為比對，要扣掉這幾項：

| 改變 | 章節 |
|---|---|
| 升級後第一次啟動，清單 A、B 各重抓一次完整回應（不帶 ETag），為了取得 `permissions` | §4.1 |
| `run_attempt > 1` 的成功不再存 ETA 樣本 | §8.2 |
| Engine 每 60 秒，以及狀態改變時，寫入 `status_snapshot` | §10.2 |
| App 建立刷新用的具名事件 | §10.5 |
| `cache.db` 多三張資料表 | §10.2 |

---

## 2. 架構變動

```
IsItGreenYet.slnx
├── src/IsItGreenYet.Core   (net10.0)          新增：寫入端點、寫入流程、狀態表
├── src/IsItGreenYet.App    (net10.0-windows)  新增：選單項目、確認對話框、設定、刷新訊號
├── src/IsItGreenYet.Cli    (net10.0)          新專案：主控台子系統，只參考 Core
└── tests/IsItGreenYet.Core.Tests (net10.0)    Core 與 Cli 的測試
```

- Cli 專案要加進 `IsItGreenYet.slnx`（CI 只建置這個檔）。
- 測試專案多參考 Cli 專案。Cli 的邏輯寫成可以不經過 `Main` 直接呼叫的類別，測試注入假的時鐘、資料夾、輸出與「App 是否在執行」的檢查。
- App 維持 WinExe，啟動流程不變。
- 版本號從 App 傳給 Engine：`EngineDependencies` 加一個 `AppVersion`（App 的 InformationalVersion），寫進狀態表用。Cli 的 csproj 也設同一個 `Version`。

---

## 3. GitHub API

### 3.1 新增的端點

| # | 端點 | 用途 | 成功的狀態碼（文件） | fine-grained 權限 | classic／OAuth scope |
|---|---|---|---|---|---|
| E11 | `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs` | 重跑失敗的 job 與其下游 job | 201 | Actions: write | `repo` |
| E12 | `POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun` | 整個 run 重跑 | 201 | Actions: write | `repo` |
| E13 | `POST /repos/{owner}/{repo}/actions/runs/{run_id}/cancel` | 取消 | 202；另列 409 | Actions: write | `repo` |
| E14 | `POST /repos/{owner}/{repo}/actions/runs/{run_id}/force-cancel` | 強制取消 | 202；另列 409 | Actions: write | `repo` |

出處：[W1]（端點頁，API 版本 2026-03-10）、[W6]（fine-grained 權限總表）。

- 四個端點的回應 schema 都是空物件 [W1]，不含新的 attempt 編號。所以重跑有沒有生效，要靠操作後追蹤看到 `run_attempt` 變大（§6）。
- 2026-03-10 與 2022-11-28 兩版的 workflow-runs 頁，寫入端點的參數、狀態碼、權限完全相同。這是研究代理比對兩版文件的結果，我沒有重新比對。所以沿用 P1 §4.1 固定送的 `X-GitHub-Api-Version: 2026-03-10`，不用另外處理。
- E14 只在一般取消沒反應時才用，它會略過 `always()` 這類讓 job 繼續跑的條件 [W1]。

### 3.2 寫入請求的共通規則

| 項目 | 規則 | 依據 |
|---|---|---|
| header | 和 P1 §4.1 相同：`Accept`、`User-Agent`、`X-GitHub-Api-Version`；`Authorization` 只送 `api.github.com` | P1 §4.1 |
| `If-None-Match` | 不帶 | POST 沒有條件請求 [W4] |
| body | 不送（`Content-Length: 0`） | 四個端點的 body 參數都是選填 [W1]。GitHub 會不會拒收沒有 body 的 POST，文件沒寫，列入實測 R1 |
| 成功 | 任何 2xx 都算受理，不解析 body | 回應 schema 是空物件 [W1] |
| 快取 | 不讀也不寫 `http_cache` | 寫入的回應不能重用 |
| 轉址 | **一律不跟隨**（301、302、307、308 都一樣），算「被拒：位置改變」，並排入一次 repo 完整重抓（同 P1 §5.4） | 官方建議 301、302、307 都重送到 `Location` [W4]。寫入刻意不跟隨，**屬於偏離官方建議的設計選擇**：寫入不應該自動送往伺服器指定的其他位置。repo 改名後，先讓清單更新成新名稱，你再按一次就會送到正確的位置 |
| 逾時 | `HttpTimeout`（30 秒） | P1 附錄 B |
| 自動重送 | 不重送。唯一的例外是 401：重新取得 token，值不同時用新值重送一次（P1 §13.2） | 401 代表伺服器沒有執行。其他失敗時，請求可能已經生效；重跑有 50 次上限 [W2]，自動重送可能多用一次 |
| 寫入之間的間隔 | 兩個寫入請求之間至少隔 `MutativeRequestSpacing`（1 秒）。間隔只看寫入請求，不影響輪詢 | 修改型請求之間至少等 1 秒 [W4] |
| 點數 | 不另外計算 | 估算：官方說多數 POST 算 5 點、REST 每分鐘上限 900 點 [W5]。每分鐘請求上限是 60（P1 §6.3），就算全部是 POST，以 5 點估算也只有 60 × 5 = 300 點。這四個端點實際算幾點，官方沒有公開；超過時會收到次級限流，照 P1 §6.4 處理 |
| 每分鐘上限 | 寫入請求也算進 `MaxRequestsPerMinute` | P1 §6.3 |
| api.log | 方法欄寫 `POST`，其他格式同 P1 §14.4；同樣經過 `SecretRedactor` | P1 §13.4 |

### 3.3 寫入回應的分類

錯誤分類只用在 `api.github.com` 的回應，依下表的順序判斷。限流的判斷沿用 P1 §6.4 的第 1、2 列；其他列只回報給這次操作，**不改輪詢目標的狀態**。P1 §6.4 第 3～5 列是輪詢專用的規則，寫入不套用。

| 順序 | 回應 | 結果（§5.2） | 行為 |
|---|---|---|---|
| 1 | 2xx | 已受理 | 進入操作後追蹤（§6） |
| 2 | 限流（P1 §6.4 第 1、2 列） | 被拒：限流 | 全域暫停，照 P1 §6.4 |
| 3 | 401 | 依 P1 §13.2 重取 token、重送一次；仍是 401 就進入「未登入」，結果是被拒：未登入 | — |
| 4 | 403，而且 message 以 `Resource not accessible by` 開頭 | 被拒：權限不足 | 讀 `X-Accepted-GitHub-Permissions` 組出「缺 Actions write」的說明；這個 repo 的寫入改成停用（§4.3） |
| 5 | 其他 403 | 被拒：存取被拒 | 伺服器 message 原樣顯示 |
| 6 | 404 | 被拒：找不到 | 顯示「找不到這筆 run，或缺少寫入權限」。依據：認證不足時，私有資源回 404 而不是 403 [W7]，所以分不出是哪一種。操作後追蹤的 E6 會把列更新成實際狀態；E6 也回 404 時，照 P1 §4.2 E6 的規則標成「已不存在」，這筆 run 從清單移除 |
| 7 | 409 | 被拒：狀態衝突 | 伺服器 message 原樣顯示。409 的觸發條件官方沒寫（P1 §19.1 已註明），推測是 run 的狀態不能轉成 cancelled |
| 8 | 422 | 被拒：無法處理 | 伺服器 message 原樣顯示 |
| 9 | 400、410 | 被拒：其他 | **不當成 API 版本錯誤**。P1 把 400／410 判成版本錯誤（P1 §4.1），那是輪詢的規則；寫入回 400 不代表版本錯誤，不停止輪詢 |
| 10 | 3xx | 被拒：位置改變 | 見 §3.2 |
| 11 | 5xx、其他狀態碼 | 5xx：**結果不確定**；其他狀態碼：被拒：其他 | 伺服器 message 原樣顯示，寫 error.log。5xx 不算被拒：GitHub 處理太久時會自己回 5xx，請求可能已經生效（§3.2「自動重送」那一列），所以重跑照結果不確定記成待確認（§6.2），取消照取消的結果不確定處理。2026-10-30 最終審查裁定，取代原本的「5xx 是被拒：其他」 |
| 12 | 連線層錯誤、逾時 | 結果不確定 | 見 §5.2 |

---

## 4. 寫入權限

### 4.1 會擋住寫入的三種情況

| 情況 | 怎麼知道 | 處理 |
|---|---|---|
| token 的 scope 不夠 | 回應的 `X-OAuth-Scopes` 有值，但不含 `repo` | 事先停用這個帳號的所有寫入 |
| 你在這個 repo 沒有寫入權 | E2 回應的 `permissions.push == false` | 事先停用這個 repo 的寫入 |
| fine-grained token 沒有 Actions write | 事先查不到，只有被拒時才知道 | 收到「權限不足」的 403 後，停用這個 repo 的寫入（§4.3） |

- **scope**：記下最近一次回應的 `X-OAuth-Scopes`，連同當時使用的 token 一起記（只在記憶體，不記 token 原值，記能分辨是不是同一把 token 的識別即可）。不多送請求。
  - token 改變時清掉，在新 token 的回應出現之前視為不確定。
  - header 不存在，或值是空的（fine-grained token）：不確定，不停用。
  - gh 模式也可能拿到 fine-grained token（`GH_TOKEN`，P1 §4.4），所以不假設 gh 模式一定能寫。
  - 只有 `public_repo` 的 classic token 能不能寫公開 repo，端點頁只提到 `repo` [W1]，所以也當成不夠（推測，README 寫明）。
- **`permissions.push`**：`GET /user/repos` 的 repo 物件可能帶 `permissions` 物件；這個物件本身是選填，有出現時 `push` 是必填的 boolean [W8]。
  - 存在新資料表 `repo_permissions`（§10.2），鍵是 `(repo_id, user_id)`，和 E2 的處理結果、ETag 在同一個交易寫入（同 P1 §14.3 `http_cache` 的規則）。
  - 回應沒有 `permissions` 物件：這個 repo 視為不確定，不寫成 false。
  - `user_id` 不是目前帳號的資料列，一律視為不確定。
  - **升級時的一次性重抓**：第一階段建的 DB 裡，清單 A、B 已經有 ETag，之後只會拿到 304，讀不到 `permissions`。所以 `DatabaseInitializer` 第一次建立 `repo_permissions` 表時（建立前先查這張表是否已存在），同一個交易刪掉清單 A、B 所有分頁在 `http_cache` 的資料列，下一次抓清單就會拿到完整回應。只在建表的那一次做，之後不再判斷。
  - 還沒讀到某個 repo 的值時，視為不確定，照常可以送出，由伺服器決定。

### 4.2 不判斷的情況

組織政策、SSO、repo 設定讓 Actions 不能用這類情況，事先都查不到，一律交給伺服器回應（§3.3 第 5 列）。

### 4.3 被拒之後的停用

- 只有「權限不足」的 403（§3.3 第 4 列）才停用，而且只停用那個 repo 的寫入。其他 403、409、422 只回報那一次。
- 送出時用的 token 已經不是目前的 token（送出期間換了 token），就不停用。
- 停用狀態只放在記憶體。下列任一情況會清除：
  - token 改變（換 PAT、gh 換帳號、401 後重取的 token 不同）
  - App 重新啟動
  - 在設定頁按「檢查連線」
- 停用的選單項目用 tooltip 說明原因，並提示「到設定頁按『檢查連線』可以重新檢查」。理由：fine-grained PAT 的權限可能在網頁上改過，但 token 字串不變（不確定），不能只靠 token 改變來清除。

---

## 5. 寫入流程（Engine）

### 5.1 介面

```csharp
public interface IActionsWriter
{
    Task<ApiResult<object>> RerunFailedJobsAsync(RepoInfo repo, long runId, CancellationToken ct);
    Task<ApiResult<object>> RerunAllAsync(RepoInfo repo, long runId, CancellationToken ct);
    Task<ApiResult<object>> CancelRunAsync(RepoInfo repo, long runId, CancellationToken ct);
    Task<ApiResult<object>> ForceCancelRunAsync(RepoInfo repo, long runId, CancellationToken ct);
}
```

- P1 §19.1 寫的 `RepoRef` 不存在；GitHubApi 的同類方法用 `RepoInfo`，這裡跟著用。
- App 不直接呼叫 `IActionsWriter`，而是呼叫 Engine 的對應方法（同 `CopyFailedLogAsync` 的寫法）。Engine 負責節流、401、全域暫停、操作後追蹤，所以 `IActionsWriter` 回傳 `ApiResult<object>`（Engine 要拿到額度 header、401 與限流資訊），給 App 的 `WriteResult` 由 Engine 組。
- Engine 對 App 的方法用 `(repoId, runId, expectedAttempt)` 當參數。`expectedAttempt` 是確認對話框上顯示的那一次嘗試（§9.2）。送出前由 Engine 自己查出 `RepoInfo` 與這筆 run 的最新狀態，不信任 App 手上可能已過時的資料。

### 5.2 結果分類

| 結果 | 意思 | App 怎麼顯示（§9.4） |
|---|---|---|
| 已受理 | 伺服器回 2xx | 訊息條 |
| 被拒 | 伺服器有回應，但沒有執行。帶原因（§3.3 的分類）、狀態碼、伺服器 message。5xx 不算被拒（§3.3 第 11 列） | 錯誤對話框 |
| 未送出 | 送出前就停下，請求沒有離開本機。帶原因（見下表） | 訊息條 |
| 結果不確定 | 請求可能已經生效：送到了但沒拿到回應（逾時、連線中斷、程式結束時被取消），或伺服器回 5xx（§3.3 第 11 列，帶狀態碼與伺服器 message） | 錯誤對話框，說明「可能已經生效，App 會自動確認」 |

「未送出」的原因：

| 原因 | 什麼時候 |
|---|---|
| 寫入功能已關閉 | D1 的開關是關的 |
| 未登入 | §5.4 |
| API 版本錯誤 | §5.4 |
| 限流暫停中 | §5.4（全域暫停，不是自動更新的「暫停」） |
| 離線 | §5.4 |
| 缺少權限 | §4 事先停用的情況 |
| 不能操作 | 依 §7 這筆 run 現在不能做這個動作，附 §7 的原因代碼 |
| 已有新的嘗試 | 這筆 run 最新的 `run_attempt` 和 `expectedAttempt` 不同：對話框開著時，有人從網頁重跑了，或前一次重跑剛被看到 |
| 已有操作在送出中 | 同一筆 run 已經有一個寫入在送 |
| 找不到 repo 或 run | 對話框開著時，repo 被靜音、略過、移出，或 run 已不存在 |
| 排隊太久 | §5.3 第 3 點 |
| 程式正在結束 | App 結束時被取消（App 不顯示，同複製 log） |

「被拒」與「結果不確定」都會進入操作後追蹤（§6），由 E6 判斷實際狀態；「未送出」不會。

### 5.3 送出路徑

1. 和複製 log 共用使用者操作那一條：`MaxConcurrentUser = 1`、同一個節流器與額度狀態（P1 §6.3）。沿用 401 重取 token 重送一次、限流時進入全域暫停（P1 §13.2、§6.4）。
2. **資料來源與執行緒**：現在的執行緒模型規定，使用者操作只讀不可變的參考（`_repoLookup`、`_settings`），步驟專用的 run 資料（`_data`）與排程不能碰。寫入照這個規則：
   - Engine 每次發布快照時，一併發布一份不可變的「可操作性查表」：`runId → (repoId, run_attempt, §7 的四個動作能不能用與原因代碼)`，用和 `_repoLookup` 相同的方式交給使用者操作讀。
   - 「送出中」的 run 集合，以及 §4 的權限停用狀態，用鎖保護（使用者操作會寫、建立快照時會讀）。
   - 記下待確認的操作、排 E6，一律透過 `_userEffects` 交給下一步處理（同現在使用者操作改步驟狀態的做法）。唯一的例外是 `pending_reruns`：使用者操作拿到結果（含程式結束時被取消）時就直接寫進 DB，不經 `_userEffects`。理由：結束時 Engine 會等使用者操作結束，但不會再處理 `_userEffects`，經它轉交的話，結束前一刻送出的重跑會漏記。
   - 「送出中」的資料**不在寫入回傳時移除**，要留到套用了這筆 `_userEffects` 的那一步發布快照之後（那份快照與可操作性查表已經含這筆待確認的操作）才移除。否則寫入回傳到下一份快照之間，查表還是舊的，再按一次重跑會通過檢查。App 端也一樣：寫入項目停用到收到那一份快照為止。
3. **和複製 log 不同的部分**：
   1. 送出前在節流器的鎖內，再檢查一次：寫入功能開關、未登入、版本錯誤、全域暫停、離線、可操作性查表（含 `expectedAttempt`）、送出中集合。任一不成立就回「未送出」。現在的使用者操作不檢查離線，寫入要加上。
   2. 等待時間（等使用者操作那一條空出來，加上等節流器放行）超過 `MutativeMaxQueueWait`（15 秒）就放棄，回「未送出：排隊太久」。複製 log 現在的做法是一直等到暫停結束；寫入如果照做，可能在你按下去的幾十分鐘後才送出。
   3. 不重送（§3.2）。
4. App 停用了選單項目，Engine 仍然自己檢查，不只靠 App。

### 5.4 什麼情況不能寫

| 情況 | 能不能寫 | 依據 |
|---|---|---|
| 未登入 | 不能 | P1 §13.2：未登入期間不送任何 API |
| API 版本錯誤 | 不能 | P1 §4.1 |
| 全域暫停（限流） | 不能 | P1 §6.4：全域暫停期間所有請求都不送 |
| 離線 | 不能 | 送了也一定失敗，而且會變成「結果不確定」 |
| 自動更新設成「暫停」 | 可以 | 暫停的是週期性輪詢；你按下去的寫入照送，操作後追蹤也照常進行到結束（§6.2） |
| 寫入功能關閉 | 不能 | D1 |

---

## 6. 操作後追蹤

### 6.1 為什麼一定要有

- E6 只查「本地未完成、而且不在 E4 第 1 頁」的 run，run 一完成就不再排下一次（P1 §4.2 E6）。
- 重跑一筆已完成的舊 run 之後，本地仍然顯示它已完成，所以沒有任何輪詢會去查它。只有它剛好出現在 E4 第 1 頁時才會被看到，而 runs 清單的排序文件沒寫（P1 §4.2 E4）。
- 取消一筆不在第 1 頁、沒追蹤也沒標記的 run 時，現有的 E6 間隔是 `QuietMax × k`（P1 §6.1），「取消中」最慢要 5 分鐘以上才會消失。

### 6.2 規則

1. 有回應的寫入（已受理、被拒、結果不確定）都記下一筆「待確認的操作」：`(runId, 動作, 送出前的 run_attempt, 送出的 UTC 時間, 送出的單調時間戳)`。
   - 重跑（E11、E12）的已受理與結果不確定，另外寫進 `pending_reruns` 表（§10.2），App 重新啟動後接著追（第 6 點）。這一筆由使用者操作直接寫（§5.3 第 2 點）。
   - 其他的只放在記憶體。
2. 記下的同時，用使用者操作的優先序（P1 §6.3 第 1 級）排一次 E6。
3. 之後這筆 run 一直是 E6 的候選，直到下表的結束條件或放棄條件（清除見第 5、7 點）：

   | 動作與結果 | 間隔 | 結束條件 | 放棄條件 |
   |---|---|---|---|
   | 重跑，已受理或結果不確定 | `ActiveRepoInterval × k` | 看到 `run_attempt` 大於送出前的值，或 run 不再是已完成（`status` 本身是 `startup_failure` 算已完成，P1 §7.1） | 從送出起超過 `RerunConfirmTimeout`（5 分鐘），**而且**送出後已收到至少 `RerunConfirmMinResponses`（3）次 E6 回應（200 或 304）。兩個條件都要成立，所以鎖定、睡眠、離線、全域暫停期間送不出 E6 時，不會因為時間到就放棄 |
   | 取消，已受理 | 前 `CancelGraceBeforeForce`（5 分鐘）用 `ActiveRepoInterval × k`，之後用 `QuietMax × k` | 看到 `status == completed`，或 `status` 本身是 `startup_failure`（P1 §7.1） | 不放棄（run 最長 35 天會被 GitHub 取消，P1 §7.1）；repo 停止監看或被移除時一起清除（第 7 點） |
   | 被拒（任何動作）、取消的結果不確定 | — | 第一次 E6 有回應就結束（只是為了把本地狀態對齊伺服器） | — |

   - **不受自動更新「暫停」影響**：P1 在暫停時不排任何週期性輪詢（P1 §6.2），現在的程式會讓週期間隔回 null、把目標移出排程。操作後追蹤的 E6 不走這個判斷，切到暫停時也不移除。
   - **鎖定下限**：重跑的確認不套用 `LockedMinInterval`（最多 5 分鐘、每 15 秒一個請求，額度可以忽略）；取消的追蹤照樣套用（可能持續很久）。
   - 錯誤時照 P1 §6.4 的單一目標退避，以上表的間隔當成正常間隔。
   - 全域暫停、離線、未登入、睡眠期間照 P1 的全域規則不送請求，恢復後接著追。
   - 取消的「結果不確定」不標成「取消中」：不知道取消有沒有生效。run 本來就未完成，P1 現有的 E4／E6 規則會接手。
4. 重跑結束後，新的 attempt 如果還沒完成，本地就是「未完成」，P1 現有的 E4／E6 規則會接手，不用再特別追蹤。
5. E6 回 404：照 P1 的規則把 run 標成「已不存在」，這筆待確認的操作一起清除（`pending_reruns` 的資料列也刪掉）。
6. **App 重新啟動**：啟動時讀 `pending_reruns`，每一筆都照第 3 點重新追蹤。單調時間戳無法跨程序比較，所以重新啟動後視為已超過 `RerunConfirmTimeout`，只要再收到 `RerunConfirmMinResponses` 次 E6 回應還沒看到新的 attempt，就放棄並刪除資料列。
7. **repo 停止監看或被移除**：這個 repo 所有 run 的待確認操作一起清除，重跑的 `pending_reruns` 資料列也刪掉，不通知。
   - POST 進行中 repo 停止監看或被移除：套用結果時不記成待確認的重跑（資料列刪掉），取消也不標成「取消中」。
   - 資料庫重建後第一次分類就不再監看（例如強制納入遺失）也算。
   - 這兩種情況，被 P1 §5.2 第 6 條略過都不算：重建後本地沒有 run，要等 E4 的首次檢查才知道。
   - 資料庫重建後或重新啟動時，repo 不在本地 `repos` 表（重建清空了，或讀回的是孤兒列）：連續 `RemovalConfirmRounds`（2）輪完整清單都沒有，才清除待確認的重跑，不完整的一輪不算；這個輪數只記在記憶體，重新啟動後從 0 算。取消中不在這條路清除（清單上沒有這一列，看不到）。repo 還在本地 `repos` 表時走 P1 §5.4 的連續兩輪移除，輪數存在 `repos.missing_rounds`，重新啟動不歸零。
   - 重新啟動時讀回、repo 還在清單上但已不監看（例如靜音中）的列不清除，恢復監看後接著追（第 6 點）。

### 6.3 「取消中」

- GitHub 沒有「取消中」這種狀態：run 列表的 status 篩選值沒有 `cancelling`，run 物件的 status 也沒有 enum [W1]。所以只能是本地狀態。
- 取消（E13、E14）**已受理**之後，這筆 run 標成「取消中」，看到 `status == completed`（或 `status` 本身是 `startup_failure`，P1 §7.1）才清除（E6 回 404、repo 停止監看或被移除時也清除，§6.2 第 5、7 點）。**不設時間上限**：帶 `always()` 的 job 可能讓 run 跑很久 [W3]。
- 「取消中」只在記憶體。App 重新啟動後就不再顯示，列回到 GitHub 給的狀態。
- 系統匣與摘要列不變：取消中的 run 照常依它的判定類別計算（P1 §7.3），完成後依結論計算。
- 快照（`MonitorSnapshot`）多一個欄位，列出取消中的 run id 與送出時間，給 App 顯示用。

### 6.4 強制取消的出現時機

- 取消（E13）已受理超過 `CancelGraceBeforeForce`（5 分鐘）而且 run 仍未完成，選單才出現「強制取消」（D7）。
- 5 分鐘取自官方的取消逾時：5 分鐘後伺服器會強制終止已標記取消的 job [W3]。超過這個時間還沒完成，代表有 job 不受一般取消影響。
- 文案不寫「保證 5 分鐘內完成」。
- 取消中時，一般的「取消」停用（原因：已送出取消），不重送，5 分鐘也不重算。
- App 重新啟動後沒有「取消中」的紀錄，強制取消不會出現；要先再送一次一般取消。對已在取消中的 run 再送 E13，會回 202 還是 409，列入實測 R5。
- 5 分鐘後追蹤間隔變成 `QuietMax × k`，所以本地狀態可能落後；run 其實已經完成時按下強制取消，伺服器會拒絕，§3.3 照常處理並送 E6 更新列。

---

## 7. 每一列能不能操作

Engine 在建立快照時算好每一列的四個動作能不能用，不能用時附上原因代碼（enum），由 App 轉成目前語言的文字。原因不放文字，換介面語言時才能立即更新。同一份結果也放進 §5.3 的可操作性查表。

### 7.1 依 run 狀態

| run 的狀態 | 重跑失敗的 job（E11） | 整個 run 重跑（E12） | 取消（E13） | 強制取消（E14） |
|---|---|---|---|---|
| 未完成（queued、in_progress、requested、pending、不認得的非 completed） | 不能：還沒完成 | 不能：還沒完成 | 可以 | §6.4 的條件成立才出現 |
| 等待（`waiting`） | 不能：還沒完成 | 不能：還沒完成 | 可以 | 同上 |
| `completed` + `failure`、`timed_out` | 可以 | 可以 | 不能：已完成 | 不出現 |
| `completed` + `startup_failure`，或 `status` 本身是 `startup_failure` | 不能：沒有可重跑的 job（啟動失敗時可能根本沒有 job，P1 §10.9） | 可以 | 不能：已完成 | 不出現 |
| `completed` + `cancelled` | 可以 | 可以 | 不能：已完成 | 不出現 |
| `completed` + `success`、`skipped`、`neutral`、`stale`、不認得 | 不能：沒有失敗的 job | 可以 | 不能：已完成 | 不出現 |
| `completed` + `action_required` | 不能：等待核准 | 不能：等待核准 | 不能：已完成 | 不出現 |

- `status` 本身是 `startup_failure` 的 run，P1 判成失敗（P1 §7.1），這裡和 `completed` + `startup_failure` 同一列，不歸到「未完成」。
- `cancelled` 的 run 能不能用「重跑失敗的 job」，官方頁沒寫。先開放，實測 R7 確認；如果伺服器拒絕，改成「不能」。
- `startup_failure` 用「整個 run 重跑」會怎樣，列入實測 R13。
- `waiting` 是在等 deployment protection rule（P1 §7.1）。取消一個等待中的 run 會怎樣，文件沒寫，靠伺服器回應。
- `action_required` 只提供既有的「開啟 run」，到網頁上處理。

### 7.2 共通條件（任一成立，四個動作都不能用）

| 條件 | 原因代碼 | 依據 |
|---|---|---|
| 寫入功能關閉 | 不顯示這些項目（§9.1） | D1 |
| 這筆 run 已有操作在送出中 | 送出中 | §5.3 |
| 帳號的 scope 不夠、repo 沒有寫入權、或寫入已被停用 | 缺少權限 | §4 |
| 未登入、API 版本錯誤、離線、限流暫停中 | 各自一個代碼 | §5.4。自動更新的「暫停」不在此列 |

已不存在的 run 不在清單上（P1 §4.2 E6 回 404 後會移出），所以沒有對應的原因代碼；對話框開著時才變成不存在的，由 Engine 回「未送出：找不到 repo 或 run」。

### 7.3 只限重跑的條件

| 條件 | 原因代碼 | 依據 |
|---|---|---|
| 已有待確認的重跑（§6.2，還沒看到新的 attempt） | 已送出重跑，等待 GitHub 更新 | 避免重複按，多用掉一次重跑額度。這段期間「取消」照 §7.1 是「已完成」而停用 |
| workflow 不是 `active` | workflow 已停用 | workflow 停用期間不能重跑 [W2]。workflow 清單每 `WorkflowsRefresh`（3600 秒）刷新一次，狀態最多落後 1 小時，最後以伺服器回應為準 |
| `created_at` 超過 `RerunWindowDays`（30 天） | 超過 30 天 | 首次執行後 30 天內才能重跑 [W2]。官方沒說用哪個時間起算；`run_started_at` 每次重跑都會重設 [W1]，所以用 `created_at`（推測它是首次建立的時間，列入實測 R1） |
| `run_attempt ≥ MaxReruns + 1`（51） | 可能已達重跑上限 | 一個 run 最多重跑 50 次 [W2]。推測：第 1 次執行加 50 次重跑 = 51。超過時 changelog 說會得到失敗的 check suite [W9]，不一定回 HTTP 錯誤，所以事先停用，文案寫「可能」 |

### 7.4 只限取消的條件

| 條件 | 原因代碼 | 依據 |
|---|---|---|
| 已在取消中（§6.3） | 已送出取消 | §6.4 |

---

## 8. 重跑與通知、ETA

### 8.1 通知

- 去重的鍵是 `(run_id, run_attempt, conclusion)`（P1 §9.2）。重跑產生新的 attempt，不會被去重擋下。
- 缺口：N1「追蹤中的 run 失敗」要求這個 run 在完成的那一刻屬於某個追蹤鍵（P1 §9.1）。重跑舊 run 不會搶走 `latest`（P1 §7.2），所以重跑一筆不在最新組的舊失敗 run，再失敗時不會通知。N2 只管成功，而且比對的是 `actor.id`；重跑時按下去的人記在 `triggering_actor`，可能和 `actor` 不同 [W10]。
- 所以在 App 內重跑（E11、E12 已受理或結果不確定）時，Engine 自動幫你「完成時通知我」（D6）：
  1. `pending_reruns` 的資料列記下 `notify = 1`（§10.2）。
  2. 操作後追蹤看到新的 attempt **還沒完成**：轉成既有的「完成時通知我」標記（寫進 `watched_runs`），刪除 `pending_reruns` 的資料列。之後照 P1 的 N3 規則通知。
  3. 看到新的 attempt **已經完成**（重跑在一次查詢間隔內就跑完）：在完成觀察的當下直接發一則 N3 通知，刪除資料列。這一步不經過「完成時通知我」的檢查（它要求 run 未完成，對已完成的 run 會靜默忽略）。
  4. 放棄（§6.2 的放棄條件），或 repo 停止監看、被移除（§6.2 第 7 點）：刪除資料列，不通知。
- 這則通知照 N3 的設定開關（`notifyWatchedRunDone`），不受首輪靜默的時間界線限制（同 P1 §9.3 的 N3），同一筆 run 同時符合 N1／N2 時只發一次（P1 §9.1）。
- 第 2 步轉成 `watched_runs` 之後，照 P1 的規則輪詢；自動更新設成「暫停」時，P1 不輪詢被標記的 run，所以暫停期間不會通知（和 P1「完成時通知我」在暫停時的行為相同）。
- 取消不自動標記：取消後的結論是 `cancelled`（灰色），照 P1 不發失敗通知。

### 8.2 ETA 樣本

- 目前每次成功都存一筆耗時樣本（P1 §8.2），存的地方是 `MonitorEngine.PlanCompletion`。只重跑失敗的 job 時，耗時通常比完整執行短，會把中位數往下拉（推測）。
- 改成：`PlanCompletion` 存樣本的條件加上 `run_attempt == 1`（D13）。`DurationEstimator` 不動：它的 `CompletedDuration` 也用來顯示清單上的耗時，重跑過的 run 照樣顯示耗時。
- 代價：從網頁或 App 用「整個 run 重跑」成功的耗時是完整的，也會被排除。App 分不出從網頁送的是哪一種重跑。

---

## 9. 介面（App）

### 9.1 run 選單

- ⋯ 按鈕和右鍵用的是同一個選單（P1 §10.9）。寫入功能**開啟**時，在既有項目後面加一個分隔線和下列項目：

  | 項目 | 出現條件 | 確認對話框的按鈕樣式 |
  |---|---|---|
  | 重跑失敗的 job | 一律出現，依 §7 啟用或停用 | Accent |
  | 整個 run 重跑 | 一律出現，依 §7 啟用或停用 | Accent |
  | 取消這次執行 | 一律出現，依 §7 啟用或停用 | Danger |
  | 強制取消 | 只在 §6.4 的條件成立時出現 | Danger |

- 寫入功能**關閉**時不顯示這些項目，也不顯示分隔線；選單的項目與出現條件和第一階段相同（P1 §10.9）。
- 停用的項目用 tooltip 顯示原因（§7 的原因代碼轉成文字）。WPF 預設不對停用的元素顯示 tooltip，所以這些項目要設 `ToolTipService.ShowOnDisabled="True"`，原因同時放進 `AutomationProperties.HelpText`（做法同 repo 管理頁的強制納入按鈕）。
- 分隔線：選單現在直接設 `ItemContainerStyle`，加分隔線會丟例外；改用 `ItemContainerStyleSelector`（既有的 `MenuItemStyleSelector`）。

### 9.2 確認對話框

- 每個寫入動作都要確認一次（D5）：按選單項目算第一步，接著開確認對話框。
- 對話框（`ConfirmDialog`，新增）：
  - 外框沿用 `ErrorDialog`，但不顯示錯誤圖示、不播放錯誤音效。
  - 左邊「不要」：`IsCancel`，預設焦點在它上面。Esc 等於「不要」。
  - 右邊執行按鈕：取消類用 `Style.Button.Danger`，重跑類用 Accent 樣式。**不設 `IsDefault`**，Enter 不會誤觸執行。
  - 內容寫明是哪一筆：`repo · workflow #run_number`，以及第幾次嘗試、分支。
  - 說明文字：

    | 動作 | 說明的重點 |
    |---|---|
    | 重跑失敗的 job | 重跑失敗的 job 與它下游的 job；會消耗 Actions 分鐘數；一個 run 最多重跑 50 次 |
    | 整個 run 重跑 | 所有 job 都重跑；其餘同上 |
    | 取消這次執行 | 帶 `if: always()` 這類條件的 job 會繼續跑完 |
    | 強制取消 | 會略過 `always()` 這類條件，清理用的 job 可能不會執行 |

- 按下選單項目的當下，就記下 `repoId`、`runId`、`run_attempt` 與對話框要顯示的文字，確認後用記下的值送出（`run_attempt` 當成 `expectedAttempt`，§5.1）。理由：選單一關，延後的重排就會套用，列的容器也會被回收給別筆 run 用（P1 §10.10）；對話框開著時 run 也可能被別人重跑。
- 對話框的文字在開啟當下用目前語言組好；開著時切換語言不更新（關掉再開就是新語言）。

### 9.3 「取消中」的顯示

- **圖示與篩選不變**：沿用這一列目前的顯示狀態（`DisplayState`）。進行中的是 3/4 圓弧，排隊中是中心點，等待中是時鐘，狀態篩選也照原本的分類。
- **文字覆蓋**：
  - 狀態的 Label、tooltip 與報讀名稱改成「取消中」。
  - 時間欄的時間細節換成「取消中」：進行中的「預估剩餘」、排隊中與等待中的時間文字都換掉，耗時照常顯示。
- 不新增顯示狀態：
  - P1 §16.1 規定不新增任何顏色。
  - 狀態篩選把 `DisplayState` 的名稱存進 settings.json，新增一種會牽動已存的設定。
- 換介面語言時，「取消中」的文字立即更新（和列上的其他文字一樣）。
- 寬度：「取消中」與英文的 `Cancelling` 會出現在時間欄。P1 的寬度斷點（980／890／710）是量出來的，要用量測工具把中英文各從 1100 掃到 600 重量一次；不夠時再調整斷點，並同步 P1 附錄 B 的數值與 `Tuning`（`WidthBreakpointTests` 會檢查兩者一致）。

### 9.4 送出與結果

- 確認後，訊息條先顯示「正在送出…」，同一筆 run 的寫入項目在送出期間停用。
- 結果：

  | 結果 | 顯示 |
  |---|---|
  | 已受理 | 訊息條：「已送出重跑」／「已送出取消」等 |
  | 被拒 | 錯誤對話框：原因的說明（§3.3），加上伺服器 message 原文 |
  | 未送出 | 訊息條：原因（§5.2 的表，例如「目前離線，沒有送出」） |
  | 結果不確定 | 錯誤對話框：「沒有收到 GitHub 的回應，可能已經生效。App 會自動確認這筆 run 的狀態。」收到 5xx 時（§3.3 第 11 列）改成「GitHub 回報伺服器錯誤（狀態碼 N），可能已經生效。App 會自動確認這筆 run 的狀態。」，加上伺服器 message 原文 |

- 程式正在結束時，把結果當成「已取消」，不顯示（同複製 log 的做法）。

### 9.5 設定頁

- 新增「寫入操作」卡片，放在「失敗 log」和「一般」之間：

  | 項目 | 預設 | 說明文字的重點 |
  |---|---|---|
  | 允許在 App 內重跑與取消 run | 關 | 開啟後，run 選單會出現重跑與取消；每次都會先確認。用 PAT 時要另外開 Actions: Read and write |

- `settings.json` 新增 `allowWriteActions`（boolean，預設 `false`）。
- 「帳號」卡片的 PAT 說明加一句：「要用寫入功能，fine-grained PAT 要另外開 Actions: Read and write」。
- 按「檢查連線」時，除了既有的檢查，也清除 §4.3 的寫入停用狀態。

### 9.6 字串

- `TextTable` 每一項都是必填，兩種語言少寫一項就編譯不過；新字串一律中英文都加。
- 要新增的字串：
  - 選單項目 4 個
  - 「取消中」（列上、tooltip、報讀名稱、時間欄）
  - 確認對話框：標題、4 種說明、「不要」、4 種執行按鈕
  - 「正在送出…」，以及已受理的 2 種訊息（重跑、取消）
  - 被拒：§3.3 每一種分類各一句（含「缺 Actions write」與 404 的「找不到這筆 run，或缺少寫入權限」）
  - 未送出：§5.2 表格的每一種原因各一句
  - 結果不確定的說明（沒有回應、收到 5xx 各一句）
  - §7 的每一個原因代碼各一句，加上「到設定頁按『檢查連線』可以重新檢查」
  - 設定卡片：標題、開關、說明；PAT 說明的補充
- 英文標點照[已知限制](../known-limitations.zh-TW.md)第 27 項的現況，不在這次統一。

---

## 10. 命令列

### 10.1 指令

```
IsItGreenYet-cli.exe status --json [--refresh]
IsItGreenYet-cli.exe --help
```

| 參數 | 行為 |
|---|---|
| `status --json` | 讀 `cache.db` 的狀態表，輸出一個 JSON 物件，用退出碼表示整體狀態 |
| `--refresh` | 先請正在執行的 App 刷新，等刷新完成後再讀（§10.5） |
| `--help` | 用法印到 stdout，退出碼 0 |
| 其他（不認得的參數、少了 `--json`） | 用法印到 stderr，退出碼 4，不輸出 JSON |

- 執行檔與 App 分開（D2），所以不經過 App 的單一實例檢查（P1 §3.3），自然符合 P1 §19.2「命令列模式不走單一實例喚回」。
- 只讀資料：不改 `cache.db` 的內容，不讀也不改 settings.json。`cache.db` 是 WAL 模式（P1 §14.3），唯讀連線可能在旁邊建立 `-wal`、`-shm` 附屬檔（§11.2）。

### 10.2 新增的資料表

三張表都用 `CREATE TABLE IF NOT EXISTS` 建立，**不升 schema 版本**：目前沒有升級機制（`schema_version` 固定是 1），新增資料表不需要升級。第一階段的 DB 第一次被第二階段的 App 開啟時補建。

| 資料表 | 主鍵 | 主要欄位 | 誰寫 | 用途 |
|---|---|---|---|---|
| `status_snapshot` | `id = 1` | `written_at`、`heartbeat_at`、`stopped_at`、`app_version`、`status_json`、`refresh_started_seq`、`refresh_done_seq`、`refresh_partial` | Engine | 給命令列讀的整體狀態 |
| `repo_permissions` | `(repo_id, user_id)` | `can_push`、`updated_at` | Engine（E2 處理時） | §4.1 |
| `pending_reruns` | `run_id` | `repo_id`、`prev_attempt`、`requested_at`、`notify` | Engine | §6.2、§8.1 |

**`status_snapshot` 的寫法**

- 每次都用同一個 UPSERT 寫整列（`status_json` 與 `heartbeat_at` 一起寫），不單獨更新心跳。
- 什麼時候寫：
  - 每 `StatusHeartbeatInterval`（60 秒）一次，不論內容有沒有改變。自動更新設成「暫停」時照樣寫（Engine 還在跑）。
  - `status_json` 和**最後一次成功寫入**的內容不同時，立即寫。
  - 寫入失敗、或 DB 損毀重建（P1 §14.3）之後，清掉「最後一次成功寫入」的記錄，下一次一定整列重寫。
- `status_json`：Engine 發布快照時組好的 JSON，內容是 §10.3 表格中標「App 寫」的欄位。
- `stopped_at`：App 正常結束時（Engine 釋放時）寫入結束的時間；啟動後第一次寫入時清成 null。App 當掉時寫不到，靠 `heartbeat_at` 判斷（§10.6）。
- `refresh_started_seq`、`refresh_done_seq`、`refresh_partial`：§10.5。
- 時間一律存 UTC ISO 8601（P1 §14.3）。
- 寫入失敗只寫 error.log，不影響輪詢。

### 10.3 JSON 結構

```json
{
  "schemaVersion": 1,
  "state": "failure",
  "exitCode": 1,
  "reason": null,
  "cliVersion": "0.2.0+<40 字元的 commit>",
  "appVersion": "0.2.0+<40 字元的 commit>",
  "generatedAt": "2026-10-29T02:00:05Z",
  "snapshotAt": "2026-10-29T02:00:00Z",
  "heartbeatAt": "2026-10-29T02:00:00Z",
  "refreshed": false,
  "refreshPartial": false,
  "overall": "failure",
  "signedIn": true,
  "apiVersionError": false,
  "refreshMode": "auto",
  "counts": { "failing": 1, "inProgress": 2 },
  "monitoredRepos": 11,
  "staleRepos": 0,
  "connection": { "offline": false, "pausedUntil": null, "serverErrors": false },
  "lastUpdatedAt": "2026-10-29T01:59:58Z",
  "runs": [
    {
      "repo": "octo-org/sample-app",
      "workflow": "CI",
      "runId": 30433642,
      "runNumber": 123,
      "attempt": 1,
      "title": "Fix tests",
      "branch": "main",
      "event": "push",
      "status": "completed",
      "conclusion": "failure",
      "state": "failure",
      "cancelling": false,
      "startedAt": "2026-10-29T01:50:00Z",
      "url": "https://github.com/octo-org/sample-app/actions/runs/30433642"
    }
  ]
}
```

| 欄位 | 誰寫 | 內容 |
|---|---|---|
| `schemaVersion` | 命令列 | 這個 JSON 結構的版本，從 1 開始；欄位只增不改，改變意義時加 1 |
| `state` | 命令列 | 依 §10.6 判斷的結果：`failure`、`in_progress`、`success`、`no_data`、`error`。沒有錯誤時等於 `overall` |
| `exitCode` | 命令列 | 和程式的退出碼相同（§10.6） |
| `reason` | 命令列 | `state` 是 `error` 或 `no_data` 時的原因代碼（§10.6），其他時候是 `null` |
| `cliVersion` | 命令列 | 命令列自己的 InformationalVersion |
| `generatedAt` | 命令列 | 命令列產生這份輸出的時間 |
| `heartbeatAt` | 命令列（讀表） | `status_snapshot.heartbeat_at` |
| `refreshed`、`refreshPartial` | 命令列 | §10.5 |
| `appVersion` | App 寫 | App 的 InformationalVersion（`EngineDependencies.AppVersion`，§2） |
| `snapshotAt` | App 寫 | 這份狀態對應的快照時間 |
| `overall` | App 寫 | 系統匣的整體狀態（P1 §7.3，Engine 算好的結果）：`failure`、`in_progress`、`success`、`no_data`。「全部成功」要至少一個追蹤鍵的 `lastDecisive` 是成功，只有 Engine 知道，所以命令列不自己從 `counts` 推。第一次載入還沒完成時不會是 `success`（P1 §6.6） |
| `signedIn` | App 寫 | `true`：已登入；`false`：未登入（401 後的狀態）；`null`：還在確認（App 剛啟動、或離線啟動、還沒完成 E1 驗證） |
| `apiVersionError` | App 寫 | 是否發生 API 版本錯誤（P1 §4.1：400／410，停止所有輪詢）。不放在 `connection`，因為它會讓退出碼變成 4（§10.6） |
| `refreshMode` | App 寫 | 自動更新的設定值（同 settings.json 的 `refreshMode`） |
| `counts` | App 寫 | 和系統匣相同的計數：`failing` 是失敗的追蹤鍵數、`inProgress` 是 `keyPending` 的追蹤鍵數（P1 §7.4） |
| `monitoredRepos` | App 寫 | 監看中的 repo 數（§10.6 第 11 列用） |
| `staleRepos` | App 寫 | 無法更新的 repo 數（P1 §7.3），由 Engine 用單調時鐘算好 |
| `connection` | App 寫 | 離線、全域暫停（`pausedUntil` 是預定恢復的時間）、GitHub 持續 5xx。這三項不改變 `state` 與退出碼，和系統匣只加角標一致（P1 §7.3）。API 版本錯誤不在這裡，見 `apiVersionError` |
| `lastUpdatedAt` | App 寫 | 監看中 repo 最近一次成功回應的時間，取 `repos.last_success_at` 的最大值（跨重新啟動保留）。還沒有任何成功回應時是 `null` |
| `runs` | App 寫 | 「只看需要注意」的那些 run（P1 Q13：追蹤中的失敗，加上所有未完成與等待核准的 run），排序同主清單。沒有時是空陣列 |
| `runs[].state` | App 寫 | P1 §7.1 的判定類別：`pending`（未完成）、`waiting`（等待）、`success`、`failure`、`neutral`（灰：取消、略過、`neutral`、`stale`、完成後不認得的結論） |
| `runs[].cancelling` | App 寫 | 是否「取消中」（§6.3） |

- 鍵名固定英文、camelCase，不隨介面語言改變。
- 輸出用 System.Text.Json 的預設 encoder：非 ASCII 字元一律跳脫成 `\uXXXX` 的形式，整個輸出是純 ASCII（§11.3）。版本字串裡的 `+` 也會被跳脫，解析後的值不變。
- 直接在主控台看 JSON 時，中文的 repo 名稱、workflow 名稱、標題會顯示成跳脫字串；要用 `ConvertFrom-Json` 或 `jq` 解開。README 寫明。
- stdout 只有這一個 JSON 物件加換行；錯誤說明一律寫到 stderr，英文、純 ASCII。

### 10.4 讀 cache.db

- 路徑：`%LOCALAPPDATA%\IsItGreenYet\cache.db`（同 `AppPaths`）。
- 用 `Mode=ReadOnly`、`Pooling=False` 開檔，一個讀取交易讀完就關閉。檔案不存在時不建立。
- **絕不呼叫 `DatabaseInitializer.Ensure` 或 `Recover`**：判定損毀時，它們會把 cache.db 改名。
- 鎖定時的重試上限是 `CliDbBusyTimeout`（5 秒），逾時回錯誤（Microsoft.Data.Sqlite 預設重試 30 秒 [W11]，對命令列太長）。
- 實驗結果（§11.2）：App 持有寫入交易時，唯讀讀取不會被擋，讀到的是最後一次已提交的值。

### 10.5 `--refresh`

**App 端**

- App 取得單一實例之後，建立具名事件 `Local\IsItGreenYet.Refresh`（AutoReset，同既有的喚回事件 `Local\IsItGreenYet.Activate`），用監聽執行緒等待；收到訊號就送一個「命令列刷新」指令給 Engine。
- Engine 收到指令時：
  1. `refresh_started_seq` 加 1 並立即寫入狀態表。如果當下就被全域狀態擋住（未登入、API 版本錯誤、全域暫停、離線、睡眠），這次刷新立即算完成：`refresh_done_seq` 設成同一個值、`refresh_partial = 1`，不必等。理由：例如離線時，全量刷新只會排一次離線探測（runs、PR 都不排），探測有結果就會被當成刷新完成，命令列會拿離線前的舊資料當成剛刷新的結果。
  2. 沒有被擋住時，做一次全量刷新（同 F5，P1 §6.6）。
  3. 這次刷新的目標，是第 2 步全量刷新排入的那一批。因為結果才排入的後續目標（例如補抓 workflow 清單之後才排的 E4）不算在內。暫停模式補做的完整重抓（P1 §6.6）算在內，最後一頁回來才算處理完。
  4. 這一批的每個目標符合下列任一情況，就算處理完：有結果（成功、304 或錯誤）、已移出排程（例如 repo 被靜音）、刷新途中才被全域狀態擋住。
  5. 全部處理完，或從開始算超過 `RefreshCompletionMax`（90 秒），就把 `refresh_done_seq` 設成這次的 `refresh_started_seq` 並立即寫入。超過時間，或有目標是被全域狀態擋住的，`refresh_partial = 1`。
  6. 刷新進行中又收到訊號：不合併進這一次，在這一次結束後再做一次（最多排一次）。
- 自動更新設成「暫停」時照樣刷新（同 F5）。

**命令列端**

1. 用 `Mutex.TryOpenExisting` 檢查 `Local\IsItGreenYet.SingleInstance` 是否存在，**開到之後立刻 Dispose**，不持有到後面的步驟，也不 `WaitOne`。不能建立 Mutex：命令列握著它的期間，重新啟動的 App 會以為自己是第二份而直接結束。
   - 不存在：回錯誤 `app_not_running`。
   - 存取被拒（例如 App 以系統管理員身分執行）：回錯誤 `refresh_unreachable`。
2. 用 `EventWaitHandle.TryOpenExisting` 開啟 `Local\IsItGreenYet.Refresh`。不存在（App 是第一階段的版本）：回錯誤 `refresh_unsupported`；存取被拒：`refresh_unreachable`。
3. 讀出 `refresh_started_seq`（s0）與 `refresh_done_seq`（d0）。讀不到狀態表或資料列：不發訊號，照 §10.6 判斷這次的讀取（第 4、5 列：`db_error`、`schema_mismatch` 或 `no_status`）。
4. 發出訊號，關閉事件。
5. 每 `CliRefreshPollInterval`（500 毫秒）讀一次 `refresh_done_seq`，直到它 ≥ max(s0, d0) + 1。
   - s0 == d0（App 沒在刷新）：等這次訊號觸發的刷新。
   - s0 > d0（App 正在刷新）：這次訊號會排在進行中的刷新之後，所以要多等一次。
   - 完成後照一般流程讀狀態輸出，`refreshed: true`，`refreshPartial` 照表裡的值。
6. 超過 `CliRefreshTimeout`（210 秒）：回錯誤 `refresh_timeout`，JSON 照樣輸出目前的資料，`refreshed: false`。210 秒涵蓋最壞的情況：App 正在刷新時要等兩次，每次最長 `RefreshCompletionMax`（90 秒）。
- `Local\` 的具名物件只在同一個登入工作階段裡看得到。從另一個工作階段執行（例如「不論使用者是否登入都執行」的排程工作）時，`--refresh` 會回 `app_not_running`。README 寫明 `--refresh` 要在同一個登入工作階段裡執行。
- 為什麼不讓命令列自己打 API：見 D11。

### 10.6 退出碼

依下表由上往下判斷，第一個成立的就是結果：

| 順序 | 條件 | 退出碼 | `state` | `reason` |
|---|---|---|---|---|
| 1 | 參數錯誤 | 4 | （不輸出 JSON） | — |
| 2 | 未預期的例外 | 4 | `error` | `internal_error` |
| 3 | `--refresh` 的步驟 1、2 失敗 | 4 | `error` | `app_not_running`、`refresh_unreachable`、`refresh_unsupported` |
| 4 | 讀 DB 失敗（鎖定逾時、檔案損毀、鎖定與損毀以外的 SQLite 錯誤、`schema_version` 不是這個版本認得的值），或狀態表的內容讀不懂（`status_json` 不是合法 JSON、缺欄位、`overall` 不是四種值之一、`runs` 裡有 `null`，或時間欄不是 ISO 8601） | 4 | `error` | `schema_version` 不認得時是 `schema_mismatch`，其他都是 `db_error` |
| 5 | 沒有 cache.db、沒有 `status_snapshot` 表、或表裡沒有資料（App 從沒執行過、或還是第一階段的版本） | 3 | `no_data` | `no_status` |
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

- 錯誤（第 2～11 列）時，只要讀得到狀態表，JSON 照樣附上讀到的資料，方便判斷原因。
- 1 > 2 > 0 的順序和系統匣一致（P1 §7.3）：失敗與進行中同時成立時回 1。
- P1 §7.3 把「還沒登入」算成無資料，P1 §19.2 卻把它算成錯誤（退出碼 4）。命令列照 §19.2：腳本需要分得出「沒有資料」和「沒辦法拿到資料」。`signedIn` 是 `null`（還在確認）時不算未登入。
- **資料過舊**（第 11 列）沿用 P1 §19.2「資料過舊回 4」。心跳只能證明 App 還在跑，不能證明資料是新的：例如剛睡醒、剛啟動、長時間離線或全域暫停。門檻沿用 `StaleDataThreshold`（P1 附錄 B，1 小時）。自動更新設成「暫停」超過 1 小時也會回 `data_stale`，`refreshMode` 會是 `paused`。
- 等待核准算「進行中」（P1 Q8），可能持續好幾天；腳本用「等到不是 2」的寫法時要注意，README 寫明。
- 離線、全域暫停、GitHub 持續 5xx 本身不改變退出碼（同系統匣只加角標），由 `connection` 表示；持續超過 1 小時就會變成第 11 列的 `data_stale`。

### 10.7 發行與檔名

- 專案 `src/IsItGreenYet.Cli`：`OutputType=Exe`、`AssemblyName=IsItGreenYet-cli`、`net10.0`、只參考 Core。
- 兩種版本都出（D2），和 App 一樣是單一執行檔：

  | 檔案 | 說明 |
  |---|---|
  | `IsItGreenYet-cli.exe` | 相依框架，要有 .NET 10 Runtime（裝了 .NET 10 Desktop Runtime 就有：Desktop Runtime 安裝程式包含 .NET Runtime [W12]） |
  | `IsItGreenYet-cli-selfcontained.exe` | 自封式，不需要 Runtime。不壓縮，和 App 的自封式版本一致 |

- 大小：研究代理用 `publish.ps1` 同樣的參數實測，相依框架版 3,015,298 bytes、自封式 76,389,727 bytes。這兩個數字我沒有重新量，以實作時的實際值為準，寫進 README。

---

## 11. 命令列的實驗結果（2026-10-29）

環境：Windows 11 26H2（build 26300）、pwsh 7.6.9、Windows PowerShell 5.1、.NET SDK 10.0.403。實驗程式放在工作階段的暫存區，不進 repo。§11.1 的探針先睡 1500 毫秒，再印一行 JSON，以退出碼 3 結束；§11.3 的探針不延遲。

### 11.1 shell 會不會等、拿不拿得到退出碼

以下是非互動執行（`pwsh -Command`、`powershell -Command`、`cmd /c`）的結果，另外在 ConPTY 下做了互動測試。

| 探針 | pwsh 7 | Windows PowerShell 5.1 | `cmd /c` | 互動式 cmd |
|---|---|---|---|---|
| GUI 子系統（WinExe） | 不等（836 毫秒就回來），`$LASTEXITCODE` 是空的；`>` 存檔是空的；只有接管線時才等 | 同 pwsh | 會等，`%ERRORLEVEL%` 是 3，但畫面上沒有輸出 | 不等，`ERRORLEVEL` 是 0 |
| GUI 子系統＋只呼叫 `AttachConsole(ATTACH_PARENT_PROCESS)` | 不等，退出碼是空的，沒有輸出 | 不等，退出碼是空的，沒有輸出 | 會等，退出碼 3，有輸出 | 不等 |
| GUI 子系統＋`AttachConsole` 後重開主控台輸出（`CONOUT$`） | 不等，退出碼是空的；輸出在 shell 已經往下執行之後才出現（互動時黏在提示字元後面） | 不等，用 `-Command` 執行時沒有輸出 | 會等，退出碼 3，有輸出 | 不等，輸出插在提示字元後面 |
| 主控台子系統（Exe） | 會等，退出碼 3，管線與 `>` 都拿得到輸出（`>` 存檔的非 ASCII 內容會變亂碼，見 §11.3） | 會等，退出碼 3 | 會等，退出碼 3 | 會等，退出碼 3 |

- 結論：shell 在啟動程式時就依 PE 子系統決定要不要等，程式自己改不了。只有主控台子系統在三種 shell 都會被等到結束、拿得到退出碼（D2）。
- 把 App 本身改成主控台子系統也不行：從終端機開 App 時，終端機會一直等到 App 結束 [W13]。開機自啟與從檔案總管啟動時跳出主控台視窗的問題，可以用 detached 的主控台配置避開（Windows 11 24H2 以後支援 [W13]，本機的平台符合），但終端機卡住的問題避不開。
- 沒測到的：傳統主控台視窗（互動測試只在 ConPTY 下跑過），列入實測 R12。

### 11.2 同時讀 cache.db

- App 持有寫入交易時，`Mode=ReadOnly` 的讀取不會被擋，讀到的是最後一次已提交的值。
- 唯讀連線會在 cache.db 旁邊留下 `-wal`（0 bytes）與 `-shm`（32,768 bytes）。
- 沒測到的：命令列在迴圈裡執行時，App 的 DB 損毀復原（改名）會不會失敗，列入實測 R12。

### 11.3 輸出編碼

本機的主控台字碼頁是 950（Big5）。探針輸出含「綠燈」兩個字的 JSON，看各種接法讀回來的中文對不對：

| 探針的輸出 | pwsh 7 接 `ConvertFrom-Json` | pwsh 7 用 `>` 存檔 | Windows PowerShell 5.1 接 `ConvertFrom-Json` | Windows PowerShell 5.1 用 `>` 存檔 | cmd 顯示 |
|---|---|---|---|---|---|
| 預設（cp950） | 正確 | 亂碼 | 正確 | 正確 | 正確 |
| UTF-8 | 亂碼 | 正確 | 亂碼 | 亂碼 | 正確 |
| 純 ASCII（非 ASCII 跳脫成 `\uXXXX` 的形式） | 正確 | 正確 | 正確 | 正確 | 資料正確，但畫面上看到的是跳脫字串，不是中文 |

只有純 ASCII 每一種接法讀回來都正確，所以 JSON 一律純 ASCII（§10.3）。

---

## 12. 發行

- `tools/publish.ps1`：從兩個版本改成四個，dist 只能有這四個 exe。
  - 現在的腳本只發行一個專案，而且「每個版本只產生一個 exe」的檢查寫死成 `IsItGreenYet.exe`。改成 `$variants` 每一列都帶「專案路徑」與「預期的輸出檔名」，`dotnet publish` 的專案參數與單一 exe 的檢查都依各列取值。
  - 最後「dist 只能有這些檔案」的檢查，從兩個改成四個。
- 版本號：App 與命令列一起改成 `0.2.0`（我的決定：第二階段加了寫入與命令列，屬於功能增加）。Release 由發版 workflow 建成 draft，專案作者確認內容後手動發佈。
- README 的「兩者功能完全相同，`dist\` 只會有這兩個檔案」改成四個檔案的說明。

---

## 13. 文件要改的地方

| 文件 | 改什麼 |
|---|---|
| P1 | §1.3 的「重跑、取消 run」與 §19 開頭，加上指向本文件的連結（本 PR 已改）。寬度斷點重量後如果改變，更新附錄 B 那一列的數值；**那一列的格式不能改**，`WidthBreakpointTests` 用固定字串讀它 |
| README | 「唯讀、只送 GET」改成「預設唯讀，開啟寫入功能後才會送 POST」；PAT 權限表的 Actions: Read and write 改成「寫入功能需要」；gh token 的取捨補上寫入；⋯ 選單的項目；設定頁；dist 的檔案；「再執行一次 exe 會喚回視窗」加上命令列的例外；命令列的用法、JSON、退出碼、跳脫字串與工作階段的說明；例外處理表加 §14 的列；「和設計規格的差異」（現在在 [spec-deviations.md](../spec-deviations.md)）加上 Danger 樣式的兩個寫死顏色（`#FF9694`、`#E56260`，不在色票裡，偏離 P1 §16.1「不新增任何顏色」，D12）；手動測試從 M53 起（現在在 [manual-checklist.md](../testing/manual-checklist.md)）；測試數；進度 |
| `docs/design-notes.md` | 「定義了但沒用到的資源」移除兩個 Danger 樣式 |
| 第一階段的實作計畫 | 歷史文件，不改；另寫第二階段的計畫 |

---

## 14. 例外處理

P1 §15 的每一列照舊；下表是新增的。

| 情境 | 行為 |
|---|---|
| 寫入功能關閉時，從別的路徑呼叫到寫入 | Engine 回「未送出：寫入功能已關閉」，不送請求 |
| token 沒有寫入權（scope 不夠、repo 沒有寫入權、Actions write 被拒） | 事先停用或被拒後停用（§4）；tooltip 說明原因與「檢查連線」 |
| 按下去時 run 其實已經完成（本地狀態落後） | 伺服器回 409 或其他錯誤，顯示 message；操作後追蹤立刻送 E6，把列更新成已完成 |
| 對話框開著時 run 被別人重跑 | 回「未送出：已有新的嘗試」，請你重新確認 |
| 對話框開著時 repo 被靜音、略過或移出 | 回「未送出：找不到 repo 或 run」 |
| 重跑超過 30 天、workflow 已停用、可能已達 50 次 | 事先停用並說明；伺服器的判斷不同時，以伺服器回應為準 |
| 送出時離線、全域暫停、未登入、API 版本錯誤 | 回「未送出」，不排隊等恢復 |
| 等節流超過 15 秒 | 回「未送出：排隊太久」 |
| 逾時、連線中斷或伺服器回 5xx | 回「結果不確定」，不重送；操作後追蹤確認實際狀態（重跑在確認前兩種重跑都停用，§7.3） |
| 重跑後連按 | 待確認的重跑期間，兩種重跑都停用（§7.3） |
| 取消後超過 5 分鐘仍未完成 | 顯示「強制取消」；「取消中」保留到完成 |
| App 在「取消中」時重新啟動 | 「取消中」與強制取消都不再顯示，列回到 GitHub 的狀態 |
| App 在重跑還沒確認時重新啟動 | 從 `pending_reruns` 接著追蹤（§6.2 第 6 點） |
| 重跑後一直看不到新的 attempt | 超過 5 分鐘、而且收到 3 次 E6 回應之後停止特別追蹤，不顯示訊息 |
| 自動更新設成「暫停」時寫入 | 照送；操作後追蹤照常進行（§6.2）；重跑轉成「完成時通知我」之後，暫停期間不輪詢、不通知（同 P1） |
| repo 改名後送寫入（3xx） | 不跟隨，回「被拒：位置改變」，並重抓 repo 清單 |
| 命令列：App 正常結束 | 退出碼 4，`reason: app_not_running`（`stopped_at` 有值） |
| 命令列：App 當掉、或心跳超過 180 秒 | 退出碼 4，`reason: stale` |
| 命令列：資料超過 1 小時沒更新 | 退出碼 4，`reason: data_stale` |
| 命令列：App 是第一階段的版本，或從沒執行過 | 不帶 `--refresh`：退出碼 3，`reason: no_status`。帶 `--refresh`：第一階段的 App 正在執行時回 `refresh_unsupported`（它沒有刷新事件）；App 沒在執行（含從沒執行過）時回 `app_not_running`（§10.5 命令列端第 1 步）；兩者都是退出碼 4 |
| 命令列：DB 被鎖住超過 5 秒、損毀、schema 版本不符 | 退出碼 4，`reason: db_error` 或 `schema_mismatch`；不改名、不修復 |
| 命令列：`--refresh` 時 App 沒在執行，或在別的登入工作階段 | 退出碼 4，`reason: app_not_running` |
| 命令列：`--refresh` 開不到 Mutex 或事件（存取被拒） | 退出碼 4，`reason: refresh_unreachable` |
| 命令列：`--refresh` 210 秒內沒完成 | 退出碼 4，`reason: refresh_timeout`，照樣輸出目前的資料 |
| 命令列：`--refresh` 時 App 離線、限流暫停或未登入 | App 立即回報刷新完成並標 `refreshPartial: true`，不等；退出碼照 §10.6（通常是 4 `signed_out` 或照 `overall`，資料超過 1 小時則是 `data_stale`） |
| 命令列：未預期的例外 | 退出碼 4，`reason: internal_error`；stderr 印一行英文說明 |
| 狀態表寫入失敗 | 寫 error.log，不影響輪詢；下一次一定整列重寫 |

---

## 15. 測試策略

沿用 P1 §17 的做法（假的 `HttpMessageHandler`、`ManualTimeProvider`、暫存目錄的 DB、中文句子式的測試名稱、假資料）。

| 測試類別 | 涵蓋 |
|---|---|
| `GitHubClientTests` | POST 帶正確的 header、不帶 `If-None-Match`、不讀寫 `http_cache`；2xx 都算受理、空 body 不算錯；任何 3xx 都不跟隨；401 只重送一次；限流進入全域暫停；§3.3 每一列的分類（含 409、400 不當成版本錯誤）；逾時回「結果不確定」而且不重送；api.log 方法欄是 POST、不含 token |
| `RequestThrottleTests` | 寫入之間至少隔 1 秒；寫入間隔不影響輪詢；既有的測試不改 |
| `MonitorEngine*Tests`（寫入） | 寫入功能關閉時回「未送出」；§5.2「未送出」的每一種原因；送出前在鎖內重新檢查；等待中進入全域暫停時 POST 不會送出；排隊超過 15 秒放棄；同一筆 run 不能同時送兩個；寫入回傳後、下一份快照發布前再送同一筆重跑要回「未送出」；`expectedAttempt` 不符時不送；權限不足後停用、token 改變與「檢查連線」後解除、換 token 前送出的 403 不停用；§7 表格的每一格（含 `status` 是 `startup_failure`）；換語言後原因代碼不變 |
| `MonitorEngine*Tests`（操作後追蹤） | 有回應的寫入都排一次 E6；舊的已完成 run 重跑後一定查到新 attempt（已受理與結果不確定都要）；第一次 E6 還看不到新 attempt 時繼續追；重跑在一次查詢間隔內就完成時仍會通知；放棄條件要時間與回應次數都成立；鎖定中、離線中重跑仍查到新 attempt；自動更新「暫停」時重跑仍查到新 attempt、取消中仍會清除；App 重新啟動後從 `pending_reruns` 接著追；重跑受理後立刻結束 App（或送出途中被取消），重新啟動仍從 `pending_reruns` 接著追；「取消中」的生命週期與 5 分鐘後出現強制取消；E6 回 404 時清除待確認的操作 |
| `MonitorEngine*Tests`（ETA） | `run_attempt > 1` 的成功不存樣本；重跑過的已完成 run 仍顯示耗時 |
| `NotificationPlannerTests` | 重跑非追蹤 run 再失敗時，經由 §8.1 通知；同時符合 N1 時只發一次；N3 設定關閉時不通知 |
| `MonitorEngine*Tests`（權限）／`CacheRepositoryTests` | `permissions.push` 寫入 `repo_permissions`、和 ETag 同一個交易；回應沒有 `permissions` 時視為不確定；`user_id` 不同時視為不確定 |
| `DatabaseInitializerTests` | 新的三張表；第一階段的 DB 開啟後會補建這三張表，`schema_version` 仍是 1；建立 `repo_permissions` 的那一次刪掉清單 A、B 的 `http_cache` 資料列，之後不再刪（既有的「資料表清單」測試要更新） |
| `MonitorEngineStatusTests`（新） | 內容改變才立即寫、心跳每 60 秒整列寫；寫入失敗後下一次整列重寫；結束時寫 `stopped_at`、啟動後清掉；`refresh_started_seq`／`refresh_done_seq` 的遞增；收到指令時已離線、未登入、限流暫停、版本錯誤時立即完成並標 `refresh_partial`；刷新目標被靜音移除、途中被全域狀態擋住、超過 90 秒時都會完成並標 `refresh_partial`；刷新中再收到訊號會在結束後再做一次；`overall` 與 `apiVersionError` 照 Engine 的狀態寫入（只有灰色結論的鍵時 `overall` 是 `no_data`） |
| `CliTests`（新） | §10.6 每一列的退出碼與 `reason`；輸出純 ASCII；沒有 DB 時不建檔；有 DB 時只可能多出 `-wal`／`-shm`；第一階段的 DB（沒有新表）；空的狀態表；不改名 DB；`--refresh` 的成功、App 沒在跑、舊版、存取被拒、逾時、App 正在刷新時要多等一次；檢查完 Mutex 後不持有 handle（之後 `new Mutex(true, name, out createdNew)` 拿到 `createdNew == true`）；參數錯誤；未預期的例外 |
| `TextTableTests` | 新字串兩種語言都有；設定頁引用的選單名稱和 run 選單一致 |

App 端照 P1 不寫自動化 UI 測試，改用量測工具與實機驗證（§16）。

---

## 16. 驗收與實測

### 16.1 前提

- **每一個對 GitHub 的寫入動作都先取得你的同意**：觸發 workflow、重跑、取消、強制取消、停用與啟用 workflow、修改沙盒的 workflow 檔、新建 repo、改名。
- 不操作你正在執行的 App。
- 沙盒：第一階段收尾時建的 private 沙盒 repo（名稱不寫進 repo）。`Sandbox` workflow 可以手動觸發，輸入 `result`（success／failure）、`seconds`（0～600）、`queue`；只有一個 job，沒有 `always()`，`timeout-minutes: 15`。
- 沙盒的 README 寫它之後會被改名、再刪除（第一階段的改名實測）。寫入實測要排在改名之前，或另外建一個沙盒（要你同意）。
- 費用：每個 job 至少算 1 分鐘（沙盒 README）。

### 16.2 實測項目

| # | 要查清楚的事（官方沒寫） | 做法 | 要你同意的寫入 |
|---|---|---|---|
| R1 | 不帶 body 的 POST 是否被接受；重跑後多久查得到新的 attempt；run id 不變、`run_attempt` 從 1 變 2；`created_at`、`actor`、`triggering_actor` 會不會變 | `result=failure seconds=20` 觸發，完成後重跑失敗的 job | 觸發、重跑 |
| R2 | 整個 run 重跑 | `result=success` 觸發，完成後整個 run 重跑 | 觸發、重跑 |
| R3 | 取消期間 API 的 status、多久變成 completed、conclusion | `seconds=600` 觸發，開始跑之後取消 | 觸發、取消 |
| R4 | 取消排隊中的 run；「取消中」在排隊中的列怎麼顯示 | 用 `queue=true` 觸發兩筆，取消正在排隊的那一筆 | 觸發 2 次、取消 |
| R5 | 409 什麼時候出現、message 寫什麼；對取消中的 run 再送一次取消 | 對已完成的 run 送取消；R3 進行中再送一次取消 | 取消 |
| R6 | 強制取消 | 現在的 workflow 只能驗證回 202。要測「一般取消沒反應」，要在沙盒的 workflow 加一個 `if: always()` 的長 step | 改 workflow 檔、觸發、取消、強制取消 |
| R7 | 對被取消的 run 用「重跑失敗的 job」 | 拿 R3 的 run 重跑 | 重跑 |
| R8 | workflow 停用時重跑回什麼 | 停用 Sandbox、重跑、再重新啟用 | 停用與啟用、重跑 |
| R9 | 缺 Actions write 的 403 與 `X-Accepted-GitHub-Permissions` | 你自己建一個唯讀的 fine-grained PAT 並在 App 裡設定（我不經手 token）。**先記下原本的認證方式，測完還原** | 重跑（預期被拒） |
| R10 | 重跑後再失敗會通知（§8.1）；取消不發失敗通知 | 接在 R1、R3 之後觀察 | 同 R1、R3 |
| R11 | 「取消中」的畫面、強制取消的出現時機、中英文寬度 | 接在 R3、R4、R6 之後 | 同 R3、R4、R6 |
| R12 | 命令列：pwsh 7、Windows PowerShell 5.1、cmd、Windows Terminal 與傳統主控台視窗的退出碼與輸出；App 在跑、沒在跑、剛結束；`--refresh`；命令列在迴圈裡執行時，App 的 DB 損毀復原能不能改名 | 本機執行 | 無 |
| R13 | 對 `startup_failure` 的 run 整個重跑 | 故意把 workflow 檔弄壞觸發一次，再還原 | 改 workflow 檔、觸發、重跑 |
| R14 | `permissions` 實際出現在 E2 回應、值正確；升級後第一次啟動會重抓清單 | 用第一階段建的 DB 啟動第二階段的 App，看 api.log 與 `repo_permissions` | 無 |
| R15 | repo 改名後送寫入回哪一種 3xx | 和第一階段的改名實測一起做 | 改名、重跑 |

**測不了或不建議測**

- 30 天上限：沙盒最早的 commit 是 2026-10-29，沒有 30 天前的 run；改用其他 repo 就是在正式 repo 送寫入。
- 50 次上限：至少要用掉 50 個 job 分鐘。
- 重跑別人觸發的 run、fork PR 的 run：要第二個帳號。
- 只有 read 角色的 repo：你目前沒有組織，推測沒有這種 repo。

### 16.3 手動測試清單（已補進 [manual-checklist.md](../testing/manual-checklist.md)，從 M53 起）

| # | 項目 | 預期 |
|---|---|---|
| M53 | 寫入功能預設關閉 | 選單沒有寫入項目，也沒有分隔線；api.log 沒有 POST |
| M54 | 開啟寫入功能 | 選單出現 3 項（強制取消要等 §6.4）；關閉後又消失 |
| M55 | 確認對話框 | 預設焦點在「不要」；Enter 不會執行；Esc 等於不要 |
| M56 | 送出期間清單重排 | 確認後送出的是原本那一筆，不是重排後在同一位置的別筆 |
| M57 | 離線時按重跑 | 「目前離線，沒有送出」，api.log 沒有 POST |
| M58 | 換介面語言 | 「取消中」與停用原因立即換成新語言 |
| M59 | 停用項目的 tooltip | 停用的寫入項目滑過時看得到原因 |
| M60 | 命令列在三種 shell | 退出碼與 JSON 正確（同 R12） |

---

## 17. 決策紀錄

2026-10-29 確認：全部採用「建議」欄。

| # | 問題 | 決定 |
|---|---|---|
| D1 | 寫入功能要不要開關 | 加開關，預設關（§9.5）。README 承諾唯讀；用 gh token 時唯讀只靠程式自律（P1 §4.4） |
| D2 | 命令列的形式與版本 | 另做主控台執行檔 `IsItGreenYet-cli.exe`；相依框架與自封式兩種都出，dist 變成 4 個 exe（§10.7、§11.1） |
| D3 | 提供哪幾種重跑 | 重跑失敗的 job、整個 run 重跑；單一 job 重跑延後（§3.1） |
| D4 | 沒有寫入權時 | scope 不夠、`permissions.push=false` 時事先停用；fine-grained token 只有在「權限不足」的 403 之後才停用那個 repo，「檢查連線」可以重新檢查（§4） |
| D5 | 確認放在哪 | 選單 → 確認對話框；取消類 Danger、重跑類 Accent；每個寫入都確認（§9.2） |
| D6 | App 內重跑後的通知 | 自動在新的 attempt 完成時通知（§8.1） |
| D7 | 強制取消何時出現 | 送出取消超過 5 分鐘仍未完成（§6.4） |
| D8 | 50 次上限的邊界 | `run_attempt ≥ 51` 事先停用，文案寫「可能」（§7.3） |
| D9 | 命令列的資料來源與退出碼 | App 寫入狀態表與心跳；退出碼照 §10.6 的順序表（App 沒在跑、資料過舊回 4；沒有資料或舊版回 3；自動更新暫停時照算；等待核准算 2） |
| D10 | JSON 結構 | §10.3 |
| D11 | `--refresh` | 請正在執行的 App 刷新，命令列不打 API（§10.5）。理由：讓命令列自己打 API，要把取 token 的元件從 App 搬到 Core，App 同時在跑時會有兩套節流同時打 API，兩個程序也會同時寫 api.log（目前只有程序內的鎖） |
| D12 | Danger 樣式的寫死顏色 | 照 SAM 原樣使用，記在 [spec-deviations.md](../spec-deviations.md) |
| D13 | 重跑後的 ETA 樣本 | `run_attempt > 1` 的成功不存樣本（§8.2） |
| D14 | 什麼情況不能寫 | 未登入、版本錯誤、全域暫停、離線時不能；自動更新暫停時可以（§5.4） |
| D15 | 延遲回應讓列短暫退回舊狀態 | 不在第二階段處理；實測真的發生再另外改。原因：`UpsertRun` 寫入時不比較 attempt（推測） |

**我自己決定、你可以改的**（設計選擇）：

| 項目 | 決定 | 章節 |
|---|---|---|
| 寫入功能關閉時的選單 | 完全不顯示寫入項目與分隔線 | §9.1 |
| POST 的 body | 不送 | §3.2 |
| POST 遇到轉址 | 一律不跟隨（偏離官方建議） | §3.2 |
| 結果的顯示方式 | 被拒、結果不確定用錯誤對話框；已受理、未送出用訊息條 | §9.4 |
| 404 的說明 | 不另外送 E6 判斷，直接寫「找不到這筆 run，或缺少寫入權限」 | §3.3 |
| `permissions` 的保存 | 存進 DB；升級時刪掉清單 A、B 的 ETag 強制重抓一次 | §4.1 |
| 寫入被拒後的停用 | 用「檢查連線」解除，不另做按鈕 | §4.3 |
| 重跑確認的放棄條件 | 超過 5 分鐘而且收到 3 次 E6 回應；不套用鎖定下限 | §6.2 |
| 待確認的重跑 | 存進 DB，重新啟動後接著追；取消不存 | §6.2 |
| 取消的「結果不確定」 | 不標成「取消中」 | §6.2 |
| 取消中的一般取消 | 停用，不重送 | §6.4 |
| 待確認的重跑期間 | 兩種重跑都停用 | §7.3 |
| 命令列的資料過舊 | 最後一次成功回應超過 1 小時回 4（含自動更新暫停） | §10.6 |
| 刷新的完成條件 | 收到指令時已被全域狀態擋住就立即完成；否則那一批目標都處理完，或超過 90 秒 | §10.5 |
| 版本號 | 0.2.0 | §12 |
| 附錄 A 標「設計選擇」的常數值 | — | 附錄 A |

---

## 18. 參考來源

- [W1] [Workflow runs（API 版本 2026-03-10）](https://docs.github.com/en/rest/actions/workflow-runs?apiVersion=2026-03-10)：四個寫入端點的狀態碼、body 參數、權限；`run_started_at` 重跑時重設；status 篩選值沒有 `cancelling`。
- [W2] [Re-running workflows and jobs](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)：首次執行後 30 天內可以重跑；一個 run 最多重跑 50 次；workflow 停用時不能重跑；重跑使用原觸發者的權限。（2026-10-29 重新抓取確認）
- [W3] [Workflow cancellation](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-cancellation)：`if` 條件為 true 的 job 不會被取消；5 分鐘的取消逾時後，強制終止已標記取消、還在跑的 job 與 step。（2026-10-29 重新抓取確認）
- [W4] [Best practices for using the REST API](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)：修改型請求之間至少等 1 秒；條件請求；301、302、307 都建議重送到 `Location`。
- [W5] [Rate limits for the REST API](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)：多數 POST 算 5 點，部分端點的點數不公開；REST 每分鐘 900 點。
- [W6] [Permissions required for fine-grained personal access tokens](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)：寫入端點需要 Actions write；pending deployments 需要 Deployments write。
- [W7] [Troubleshooting the REST API](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)：認證不足時，私有資源回 404。
- [W8] [List repositories for the authenticated user](https://docs.github.com/en/rest/repos/repos?apiVersion=2026-03-10#list-repositories-for-the-authenticated-user)：repo 物件的 `permissions` 物件是選填，物件內的 `push` 是必填的 boolean。（2026-10-29 重新抓取確認）
- [W9] [Actions workflows are limited to 50 reruns（2026-04-10 changelog）](https://github.blog/changelog/2026-04-10-actions-workflows-are-limited-to-50-reruns/)：超過上限時得到失敗的 check suite 與 annotation。
- [W10] [Contexts reference](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts)：重跑後 `run_id` 不變、`run_attempt` 遞增；重跑時 `triggering_actor` 可能和 `actor` 不同。
- [W11] [Microsoft.Data.Sqlite database errors](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)：遇到 busy 時自動重試，預設逾時 30 秒。
- [W12] [Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)：.NET Desktop Runtime 安裝程式包含 .NET Runtime。（2026-10-29 重新抓取確認）
- [W13] [Console allocation policy](https://learn.microsoft.com/en-us/windows/console/console-allocation-policy)：detached 主控台配置要 Windows 11 24H2（build 26100）以後；主控台子系統的程式從終端機啟動時，shell 會等它結束。

---

## 附錄 A：新增的具名常數

| 名稱 | 值 | 依據 |
|---|---|---|
| `MutativeRequestSpacing` | 1 秒 | 官方：修改型請求之間至少等 1 秒 [W4] |
| `MutativeMaxQueueWait` | 15 秒 | 設計選擇：按下去之後一直顯示「正在送出…」，等更久不如放棄讓你重按 |
| `RerunWindowDays` | 30 天 | 官方 [W2] |
| `MaxReruns` | 50 次 | 官方 [W2]；停用條件是 `run_attempt ≥ MaxReruns + 1` |
| `CancelGraceBeforeForce` | 5 分鐘 | 官方的取消逾時 [W3] |
| `RerunConfirmTimeout` | 5 分鐘 | 設計選擇：重跑受理後應該很快出現新的 attempt，實測 R1 後再調 |
| `RerunConfirmMinResponses` | 3 次 | 設計選擇：只靠時間會在鎖定、離線、全域暫停時誤判放棄 |
| `StatusHeartbeatInterval` | 60 秒 | 設計選擇：每分鐘寫一列，負擔很小 |
| `StatusHeartbeatStale` | 180 秒 | 設計選擇：`StatusHeartbeatInterval` 的 3 倍，容許偶爾一次寫入失敗 |
| `RefreshCompletionMax` | 90 秒 | 設計選擇：冷啟動約 64 個以上的請求（P1 §6.7），受每分鐘 60 個的上限，約 1～1.5 分鐘送完 |
| `CliDbBusyTimeout` | 5 秒 | 設計選擇：預設 30 秒對命令列太長 [W11] |
| `CliRefreshPollInterval` | 500 毫秒 | 設計選擇 |
| `CliRefreshTimeout` | 210 秒 | 設計選擇：App 正在刷新時要等兩次刷新，2 × `RefreshCompletionMax`（90 秒）= 180 秒，再加 30 秒讓 App 強制收尾的結果來得及寫入 |

沿用 P1 的常數：`ActiveRepoInterval`、`QuietMax`、`LockedMinInterval`、`WorkflowsRefresh`、`StaleDataThreshold`、`HttpTimeout`、`MaxRequestsPerMinute`、`RemovalConfirmRounds`（P1 附錄 B）。
