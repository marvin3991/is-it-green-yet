# 認證與權限

[English](authentication.md) | 繁體中文

回到 [README](../README.zh-TW.md)。

## 第一次執行：登入

預設沿用 gh CLI 的登入，裝好 [GitHub CLI](https://cli.github.com/) 之後不需要另外設定：

```powershell
gh auth login
```

程式啟動時執行 `gh auth token --hostname github.com` 取得 token，只放在記憶體。不想讓程式用 gh 的 token，可以改用 fine-grained PAT（見下方的 [fine-grained PAT](#fine-grained-pat替代方案)）。沒登入就啟動也可以：登入後按橫幅的「重新檢查」，或等程式每 5 分鐘自動檢查一次（見[未登入時](#未登入時)）。

## gh CLI（預設）

- token 只放在記憶體，不寫入任何檔案；收到 401 時重新取得 token、只重試一次，避免連續送出無效憑證讓 GitHub 暫時擋掉你所有的驗證（連 gh 本身也會被擋）。
- **取捨**：經由 gh 瀏覽器／裝置登入取得的 token，最小 scope 一定含 `repo`，也就是公開與私有 repo 的完整讀寫權限。寫入功能關閉（預設）時本工具只送 `GET`，所以「唯讀」只靠程式自律，權限上沒有保證。要權限上的唯讀，請改用 fine-grained PAT。反過來，開啟寫入功能時，同一把 gh token 不用另外設定就能重跑與取消（scope 不夠時見下方「寫入功能需要的權限」）。
- 環境變數 `GH_TOKEN`／`GITHUB_TOKEN` 會蓋過 gh 自己存的憑證。改了這兩個變數後要重新啟動 IsItGreenYet；設定頁可以改成「執行 gh 時忽略這兩個變數」。
- 在設定頁按「檢查連線」後，才會顯示 `X-OAuth-Scopes` 判讀：header 有值但不含 `repo` 時顯示「這個 token 可能看不到私有 repo」；含 `repo` 或沒有這個 header 時顯示「沒有需要提示的問題」。

## 未登入時

收到 401 後重新取得的 token 和原本相同、取不到，或重送仍然 401，就進入「未登入」：停止所有輪詢、主畫面顯示橫幅、系統匣加角標，既有資料不清空。恢復方式：

- **gh 模式**：在終端機執行 `gh auth login`。程式每 5 分鐘在本機重跑一次 `gh auth token`（不打 API），token 改變時用 `GET /user` 驗證一次，成功就恢復輪詢；不想等就按橫幅（或清單空狀態）的「重新檢查」，立即重新取得 token 並驗證一次。
- **PAT 模式**：到設定頁貼上新的 PAT 並儲存。
- **token 來自環境變數**：更新 `GH_TOKEN`／`GITHUB_TOKEN` 後重新啟動 IsItGreenYet（已在執行的行程收不到環境變數的修改）。

## fine-grained PAT（替代方案）

建立方式（GitHub → Settings → Developer settings → Personal access tokens → Fine-grained tokens；依 GitHub 文件撰寫，網頁上的路徑還沒實際走過）：

1. **Resource owner** 選自己。
2. **Repository access** 選全部（All repositories），或只選要監看的 repo。
3. **Permissions** 只開下面三個唯讀權限；要用寫入功能時，Actions 改成 Read and write：

   | 權限 | 用在哪裡 | 需不需要 |
   |---|---|---|
   | Actions: Read-only | workflow 清單、run 清單與單筆 run、job 清單、下載 job log | 需要 |
   | Metadata: Read-only | repo 清單（`GET /user/repos`） | 需要 |
   | Pull requests: Read-only | 開啟中的 PR 清單（判斷要追蹤哪些 PR 分支） | 需要 |
   | Contents: Read-only | 沒有任何會用到的端點需要，而且它會一併開放讀取程式碼 | **不需要** |
   | Actions: Read and write | 在 App 內重跑、取消 run | 寫入功能需要（寫入功能預設關閉，不用就不必開） |

4. 在設定頁「帳號」切到 PAT、貼上 token（例如 `<YOUR_TOKEN>`）後按「儲存」。儲存前會先用 `GET /user` 驗證，失敗就不儲存；同一個設定頁裡被回 401 的 PAT 不會再送第二次。被 GitHub 限流（全域暫停）或 API 版本停止支援時，不能驗證、儲存 PAT。

PAT 存在 Windows「認證管理員」：一般認證，目標名稱 `IsItGreenYet:github.com`，只在這台電腦、同一個使用者看得到（不漫遊）。`settings.json` 只記認證方式，不存 token。

- 選 PAT 卻沒有可用的 PAT（沒貼新的，認證管理員裡也沒有）時不能儲存，會提示「選擇 PAT 時要先貼上 PAT。」。
- 「清除」要按「儲存」才會從認證管理員刪除，「取消」保持原樣。認證方式還是 PAT 時不能只清除：要先改回 gh CLI 再儲存，或貼上新的 PAT 取代。

**限制**（依 GitHub 官方文件；標「推論」的是文件沒明寫、尚未實測的部分）：

- 一把 fine-grained PAT 只能選一個 resource owner（一個使用者或一個組織），程式目前也只支援一把 PAT。
- 組織 repo 的 outside collaborator 只能用 classic PAT 存取。
- 其他個人帳號擁有、你只是 collaborator 的私有 repo：resource owner 只能選自己，推論讀不到。
- 組織預設要求核准 fine-grained PAT，核准前只能讀公開資源；組織預設的最長效期是 366 天。
- fine-grained PAT 一律帶有所有公開 repo 的唯讀權限，所以權限不足時 repo 清單可能照樣成功、只是少了私有 repo，不會報錯。設定頁的「檢查連線」會顯示「看得到 N 個 repo（其中私有 M 個）」，用來發現這種情況。
- 某個 repo 的 Actions 端點回 404 時，可能是 PAT 沒選這個 repo、還在等組織核准、效期不符或 SSO 未授權；程式會標示原因並改成每小時重試一次。
- 用 fine-grained PAT 時 `GET /user/repos` 是否只回傳選定的 repo，文件沒寫（推論，待實測）。

## 寫入功能需要的權限

重跑與取消要 token 能寫：classic token 或 gh 的 OAuth token 的 scope 要含 `repo`；fine-grained PAT 要 Actions: Read and write。App 不另外送請求檢查，依下面三種情況事先停用或事後停用寫入項目，停用的項目滑鼠停在上面看得到原因：

| 情況 | 怎麼知道 | 處理 |
|---|---|---|
| token 的 scope 不夠 | 回應的 `X-OAuth-Scopes` 有值，但不含 `repo`。只有 `public_repo` 的 classic token 能不能寫公開 repo，GitHub 端點文件只提到 `repo`，所以也當成不夠（推測） | 事先停用這個帳號的所有寫入。`X-OAuth-Scopes` 沒有值（fine-grained token）時不確定，不停用 |
| 你在這個 repo 沒有寫入權 | repo 清單（`GET /user/repos`）回的 `permissions.push` 是 `false` | 事先停用這個 repo 的寫入；回應沒有 `permissions` 時不確定，不停用 |
| fine-grained token 沒有 Actions write | 事先查不到，只有送出後被 GitHub 以「權限不足」（403）拒絕時才知道 | 停用這個 repo 的寫入。只放在記憶體：換 token、重新啟動 App，或到設定頁按「檢查連線」時解除（PAT 的權限可能在網頁上改過，token 字串卻不變）。換 token 只是暫時不套用：換回原本那一把 token 時停用會再生效，要按「檢查連線」（或重新啟動 App）才解除 |

- gh 模式也可能拿到 fine-grained token（例如 `GH_TOKEN`），所以不假設 gh 模式一定能寫。
- 組織政策、SSO、repo 設定讓 Actions 不能用這類情況事先查不到，交給 GitHub 的回應。
- 從沒有寫入功能的舊版本升級後第一次啟動，repo 清單會重抓一次完整回應（不帶 ETag），為了讀到 `permissions`；之後照常帶 ETag。
