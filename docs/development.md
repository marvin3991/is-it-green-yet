# 開發說明

給開發者看的文件，只有繁體中文。使用者文件見 [README](../README.zh-TW.md)。

文件裡的 commit 編號（例如 `adccd2b`）與 PR 編號（例如 #24）是公開前在 private repo 開發時的紀錄，公開的 repo 從單一 commit 開始，找不到這些編號。

## 進度與驗收摘要

> **目前進度（2026-11-06）**：版本 `0.3.0`。第一階段（唯讀監看）與第二階段（在 App 內重跑與取消 run、命令列 `IsItGreenYet-cli.exe`，見[第二階段設計規格](specs/2026-10-29-phase2-design.md)）都已實作完成。檢查新版本（[設計規格](specs/2026-10-24-design.md) §9.6）也已實作，連到真實 GitHub 的部分要等公開之後照手動測試清單的 M65 驗證。單元測試 2,417 個，2026-11-06 本機 Debug 與 Release 都跑了 2,415 個，全部通過（另外 2 個 `預設環境檢查完不持有Mutex`、`預設環境開得到App建立的刷新事件` 在 App 開著時要排除）。兩個階段的實機驗收紀錄見 [testing/acceptance.md](testing/acceptance.md)；第二階段 23 項裡通過 10 項、部分通過 12 項、沒有照設想走到 1 項（2026-10-31 補測強制取消之後）。
>
> App 端沒有自動化 UI 測試，畫面與 Windows 整合的行為要照[手動測試清單](testing/manual-checklist.md)在實機確認；第一階段的手動測試清單在 2026-10-30～31 做了不需要人在場的部分（52 項裡通過 15、部分通過 15，其餘要人在場或另外的環境，見[第一階段的手動測試](testing/acceptance.md#第一階段的手動測試)），設計規格 §18 另列的實測沒有另外建沙盒：fork PR 的 run 欄位與待核准 run 的狀態改讀公開 repo 的回應，repo 改名已實測（見[第一階段的驗收](testing/acceptance.md#第一階段的驗收)）。已知的問題見[已知限制](known-limitations.zh-TW.md)，和設計規格不同的地方見 [spec-deviations.md](spec-deviations.md)。

## 專案結構

```
src/IsItGreenYet.Core          純邏輯（GitHub API 用戶端、監看引擎、輪詢與節流、追蹤與彙總、通知判定、資料存取、設定、日誌遮罩、兩種語言的字串表），無 UI 相依
src/IsItGreenYet.App           WPF 介面、系統匣、提示視窗、Windows 專屬服務（單一實例、開機自啟、認證管理員、鎖定與睡眠事件、命令列刷新事件）
src/IsItGreenYet.Cli           命令列 IsItGreenYet-cli.exe（主控台程式）：讀狀態表、輸出 JSON 與退出碼；只參考 Core
tests/IsItGreenYet.Core.Tests  xUnit，Core 與 Cli 的測試，2,417 個（2026-11-06 跑了 2,415 個全部通過，另外 2 個見上方）
tools/generate-icon.ps1        由向量路徑產生多尺寸 app.ico（可另外輸出預覽圖）
tools/publish.ps1              跑測試 → 產圖示 → 輸出兩個單一執行檔（App 與命令列，都是相依框架）到 dist\
tools/check-markdown-links.ps1 檢查所有 Markdown 的相對連結與錨點都存在（CI 會跑）
tools/check-lock-files.ps1     確認鎖定模式開著、NuGet lock 檔沒有被產生或改寫（CI 與發版會跑）
tools/check-release-exes.ps1   唯讀檢查發行的兩個 exe：bundle 清單、e_sqlite3.dll 雜湊、host 區段、著作權欄位（CI 與發版會跑）
tools/release-check.ps1        發版前檢查：版本號、tag 在 main 上、第三方聲明的待辦標記
tools/release-package.ps1      產生 SHA256SUMS.txt，把要附加到 release 的檔案放進一個資料夾
docs/                          給使用者看的文件（usage、cli、authentication、building、behavior、known-limitations，中英各一份）、開發說明（development.md）、設計規格、視覺設計說明（design-notes.md）、和設計規格的差異（spec-deviations.md）、實機驗收紀錄與手動測試清單（testing/）、英文與中文 README 的截圖、圖示預覽
Directory.Build.props          NuGet lock 檔與鎖定模式的設定（見「鎖版本」）
.github/workflows/ci.yml       CI：windows-latest 檢查文件連結、還原、lock 檔、建置、單元測試、發行（只做檢查）
.github/workflows/release.yml  發版：推送 v*.*.* tag 時建 draft release，手動觸發是 dry-run（見 building.zh-TW.md 的「發版流程」）
```

時間一律經 `TimeProvider` 注入：排程用單調時鐘、和 GitHub 時間戳比較用 UTC 牆鐘、顯示用本地時區，所以時鐘被調整、跨午夜、日光節約等邊界都能直接測試。

數值不寫 magic number：可調的數值集中在 Core 的 `Tuning`（對應設計規格附錄 B，第二階段新增的對應第二階段設計規格附錄 A）；App 端的視覺與平台常數是各檔案內的具名常數並註明出處，例如訊息條停留 4 秒、結束時最多等監看引擎 5 秒、提示視窗離螢幕邊 16 DIP、系統匣 tooltip 上限 127 字元、系統匣圖示的幾何；XAML 的尺寸與間距是 `Themes/` 裡的資源。

## 相依套件

直接引用的套件：

| 套件 | 版本 | 用途 |
|---|---|---|
| Microsoft.Data.Sqlite | 10.0.13 | SQLite 存取 |
| xunit | 2.9.3 | 單元測試（僅測試專案） |
| xunit.runner.visualstudio | 3.1.4 | 測試執行器（僅測試專案） |
| Microsoft.NET.Test.Sdk | 17.14.1 | 測試主機（僅測試專案） |
| coverlet.collector | 6.0.4 | 涵蓋率收集（僅測試專案） |

Microsoft.Data.Sqlite 會間接帶進下面這些套件（執行階段也會用到）。版本是 NuGet 依 Microsoft.Data.Sqlite 10.0.13 宣告的相依解析出來的，鎖在 `src\IsItGreenYet.Core\packages.lock.json`（見下面的「鎖版本」）：

| 套件 | 版本 | 說明 |
|---|---|---|
| Microsoft.Data.Sqlite.Core | 10.0.13 | 間接相依 |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | 間接相依 |
| SQLitePCLRaw.core | 2.1.13 | 間接相依 |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | 原生 `e_sqlite3.dll` 來自這個套件（`runtimes\win-x64\native\`），也就是包進單一執行檔、啟動時解壓到 `%TEMP%\.net\` 的那一個 |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | 間接相依 |

除此之外沒有直接引用的第三方套件。執行階段其餘都是 .NET 共享框架內建的元件：WPF、WinForms（`NotifyIcon`／`ContextMenuStrip` 做系統匣，`Screen.AllScreens` 用來檢查還原的視窗矩形是否和任何一個螢幕的工作區相交）、`Microsoft.Win32.SystemEvents`（鎖定、睡眠與喚醒事件）、`HttpClient`、`System.Text.Json`。

### 鎖版本

每個專案都有 `packages.lock.json`，鎖住直接與間接相依的版本與內容雜湊；設定都在根目錄的 `Directory.Build.props`。

- **鎖定模式：** GitHub Actions 會設 `CI=true`，所以 CI 與發版的還原都是鎖定模式：專案的相依或平台和 lock 檔不符時失敗（`NU1004`），內容雜湊不對時也失敗（`NU1403`）。本機沒有這個環境變數，還原時會照常更新 lock 檔。
- **鎖定模式擋不到的：** lock 檔不見時，還原不會失敗，而是重新產生一份，所以 CI 與發版會跑 `tools/check-lock-files.ps1`，確認鎖定模式開著、lock 檔沒有被產生或改寫。手改 lock 檔的 `resolved` 不一定會在還原這一步失敗，所以 PR 裡的 lock 檔差異要看。
- **更新套件：** 改 csproj 的版本，執行 `dotnet restore IsItGreenYet.slnx --force-evaluate`，把 lock 檔一起 commit；再同步上面的表與 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。換了 SQLite 相關的套件或 bundle 裡的檔案時，`tools/check-release-exes.ps1` 的 bundle 清單與 `e_sqlite3.dll` 雜湊的預設值也要一起改（單元測試會比對這個預設值和聲明檔）。
- **沒有鎖的：** .NET SDK 與 runner。`global.json` 允許較新的 SDK，換 SDK 時 exe 會不同（2026-11-06 本機比對 10.0.402 與 10.0.403，兩個 exe 的大小相同、雜湊不同）。`apphost` 也來自 SDK：2026-11-06 本機與 CI 都是 SDK 10.0.403 附的 10.0.14，見 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) 待確認第 4 項。SDK 相同時，同一個 commit 的本機建置和 CI 建置逐位元相同（2026-11-06 比對分支上的 `02b2462`，兩個 exe 的 SHA-256 都相同，n=1）。
- **單一檔案分析器關著：** `PublishSingleFile` 預設會開它，它要還原 `Microsoft.NET.ILLink.Tasks`，版本跟著 SDK 走。這個套件留在 lock 檔的話，runner 的 SDK 一更新，發行就會報 `NU1004`，所以 `Directory.Build.props` 把它關掉。分析器只產生警告（`IL3000` 這一類），開著時也是 0 個（2026-11-06）。動到讀取組件路徑或資源的程式時，可以手動跑一次。兩個參數都要帶，少了 `EnableRuntimePackDownload` 時分析器不會執行，也不會有任何提示；lock 檔有被改寫才代表它真的跑了，跑完要還原：

  ```powershell
  dotnet publish src/IsItGreenYet.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=true -p:EnableRuntimePackDownload=true -o <暫存資料夾>
  dotnet publish src/IsItGreenYet.Cli -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=true -p:EnableRuntimePackDownload=true -o <暫存資料夾>
  git checkout -- '*packages.lock.json'
  ```

- **`RuntimeIdentifiers` 的副作用：** 發行時的還原帶 `-r win-x64`，所以 lock 檔要有這個平台，`Directory.Build.props` 因此設了 `RuntimeIdentifiers`。它有兩個副作用，都在設定裡處理了：
  - 每次還原會多下載 3 個 runtime pack（解開約 305 MiB，版本跟著 SDK 走，不在 lock 檔裡）。相依框架版用不到，所以不是自封式時用 `EnableRuntimePackDownload` 關掉。這個條件只看得到命令列的值：發自封式要用命令列的 `--self-contained true`，寫在 csproj 時要連這行一起改。
  - Core 不再算和平台無關，發行時會被建成 x64 專用，所以 Core 的 csproj 設 `IsRidAgnostic` 留在原來的樣子。這樣 Core 和一般的 Release 建置共用輸出資料夾，所以 `tools/publish.ps1` 發行前會先刪掉 Core 的 Release 輸出，否則先建過 Release 時，exe 裡的 Core.dll 會帶本機的 pdb 路徑。

加 lock 檔前後，本機在同一個 commit、同一個路徑的乾淨 clone 比對（2026-11-06）：兩個 exe 的 bundle 各 9 個檔逐位元相同，host 區段遮掉 bundle header offset 後也相同；exe 的雜湊、大小與 bundle ID 不同，因為 `deps.json` 在 bundle 裡的位置不同。

## 開發備註

- 目標框架：`net10.0`（Core／Cli／測試）、`net10.0-windows`（App）
- 視覺主題在 `src/IsItGreenYet.App/Themes/`，色票、狀態色對應、對比值、圖示幾何與主畫面版面記在 [design-notes.md](design-notes.md)
- 設計規格：[specs/2026-10-24-design.md](specs/2026-10-24-design.md)
- 第二階段設計規格：[specs/2026-10-29-phase2-design.md](specs/2026-10-29-phase2-design.md)
- 實作計畫沒有公開；文件與程式註解裡提到的計畫段落（例如 Task 編號、Review Focus、「和 spec 的差異與補充」）找不到原文；需要的決定請看設計規格、[和設計規格的差異](spec-deviations.md)與該則註解本身
- 測試 fixture 全部是手寫的假資料；token、私人 repo 名稱與真實 API 回應不進版控。README 的截圖也是假資料（`octo-org/sample-app` 等）

## CI

`.github/workflows/ci.yml` 在 PR、push 到 `main` 與手動觸發時，用 `windows-latest` 還原、檢查 lock 檔、建置（Release）、跑單元測試，再跑一次發行並檢查兩個 exe（輸出不上傳）；只做檢查，不部署、不用 secrets。一次約 8～19 分鐘（2026-11-02～05 最近 40 次成功 run 的 job 是 448～1,104 秒，之後加的 lock 檢查與發行再多約 32 秒；2026-10-29～30 的 7 次 run 是 3m 12s～6m 05s，第一階段 2026-10-26 的 4 次 run 是 2m 14s～2m 49s）。依 GitHub 文件 [GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)，公開 repo 使用 GitHub 代管的標準 runner 不計費，私有 repo 會用掉帳號方案每月內含的 Actions 額度

## commit hook

repo 內附 `commit-msg` hook（`.githooks/`），會移除 AI 工具的署名。clone 後執行一次 `git config --local core.hooksPath .githooks` 啟用

## 從舊 README 搬來的開發紀錄

使用者文件只留對使用有幫助的內容，下面這些開發過程的紀錄是從舊 README 搬過來的；左欄是這段文字現在對應到的位置。

| 對應位置 | 從舊 README 搬來的內容 |
|---|---|
| [README](../README.zh-TW.md) 的「下載與安裝」 | 目前沒有發佈 release（也沒有預先建好的執行檔），要自行建置。 |
| [使用說明](usage.zh-TW.md#範圍)的「範圍」 | Actions 以外的外部 check 排在第三階段。 |
| [自行建置](building.zh-TW.md#兩個執行檔)的「兩個執行檔」 | 現在的大小是 2026-11-06 在本機量的（版本 `0.3.0`，發版前分支上的 `c30cf82`，兩個檔案的 ProductVersion 都是 `0.3.0+c30cf8212fb005d25472db16f69a1bfdde5bfeef`）；之後的 commit 只改不嵌進 exe 的文件。發 release 時看 workflow「檢查發行的 exe」印的大小，不同就更新。上一版的紀錄：2026-11-05 用 main 的 `3fa5841` 量（版本 `0.2.0`），`IsItGreenYet.exe`、`IsItGreenYet-cli.exe` 是 `3,539,615`、`3,211,948` bytes。之前有四個 exe 時的紀錄：2026-10-30 用 main 的 `c37402d` 建置（ProductVersion 都是 `0.2.0+c37402dbdea706249515ab7a8182c272a7391f16`），`IsItGreenYet.exe`、`IsItGreenYet-selfcontained.exe`、`IsItGreenYet-cli.exe`、`IsItGreenYet-cli-selfcontained.exe` 依序是 `3,501,215`、`159,370,462`、`3,181,740`、`76,556,169` bytes。和上一版（加上介面調整與語言選單之後，`3,355,295`、`159,224,542` bytes）相比，兩個 App 都多了 `145,920` bytes 的程式（重跑與取消、狀態表等）。 |
| [已知限制](known-limitations.zh-TW.md)的「測試範圍」 | 以一般（未提升）權限執行還沒實測，列在[手動測試清單](testing/manual-checklist.md)。 |
| [命令列](cli.zh-TW.md) | 細節見[第二階段設計規格](specs/2026-10-29-phase2-design.md) §10。「主控台程式會等結束、視窗程式不會」是第二階段設計規格 §11.1 用實驗程式量的結果；這個 exe 在各種 shell 的測試步驟是[手動測試清單](testing/manual-checklist.md)的 M60，2026-10-30 的結果見[第二階段的實測結果](testing/acceptance.md#第二階段的實測結果)的 R12。 |
| [命令列](cli.zh-TW.md)與[例外處理](behavior.zh-TW.md#例外處理)的「舊版本」 | 使用者文件寫的「還沒有狀態表的舊版本」與「沒有刷新用事件的舊版本」，都指第一階段的版本。 |
| [認證與權限](authentication.zh-TW.md#寫入功能需要的權限)的「舊版本」 | 「從沒有寫入功能的舊版本升級」指升級到第二階段。「換 token 只是暫時不套用」和第二階段設計規格 §4.3 不同，記在[和設計規格的差異](spec-deviations.md)。 |
| [行為規格](behavior.zh-TW.md#行為規格)的「鎖定、睡眠、喚醒」 | 「間隔超過 120 秒也當成剛喚醒」是設計規格 §6.5 的推論，未實測；[手動測試清單](testing/manual-checklist.md)的 M2。 |
| [行為規格](behavior.zh-TW.md#行為規格)的「耗時」 | 驗收時和網頁比對。 |
| [資料位置](behavior.zh-TW.md#資料位置)的「快取」 | 狀態表（`status_snapshot`）、repo 的寫入權限、待確認的重跑是第二階段加上的。 |
| [已知限制](known-limitations.zh-TW.md)第 18 項 | 設計規格 §6.5 的推論，未實測；[手動測試清單](testing/manual-checklist.md)的 M12 用來確認。 |
| [已知限制](known-limitations.zh-TW.md)第 28 項 | 量測資料是 2026-10-29 的。 |
| [已知限制](known-limitations.zh-TW.md)第 29 項 | App 看到「未完成」就結束特別追蹤，是照第二階段設計規格 §6.2。第二階段實作計畫的 Review Focus 第 1 項，[第二階段的實測結果](testing/acceptance.md#第二階段的實測結果)的 R7 時實機看過。 |
| [已知限制](known-limitations.zh-TW.md)第 30 項 | GitHub 文件寫 workflow 停用時不能重跑，第二階段設計規格 [W2]。 |
| [已知限制](known-limitations.zh-TW.md)第 33 項 | 資料庫重建是 2026-10-30 實測的。 |
| [已知限制](known-limitations.zh-TW.md)第 34 項 | job 層級 `if: always()` 的實測是 2026-10-31 做的。 |
| [已知限制](known-limitations.zh-TW.md)第 35 項 | 照第二階段設計規格 §9.3。第二階段實作計畫 Task 12 重量寬度時的審查找到的。 |
| [已知限制](known-limitations.zh-TW.md)第 36 項 | 2026-10-30 在 Claude 桌面 App 實測 1 次，重建的部分是 2026-10-31 重現實驗推得。 |
| [已知限制](known-limitations.zh-TW.md)第 37 項 | 探針實測是 2026-11-03 做的。 |
| [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | 隨執行檔散布的 NuGet 套件（Microsoft.Data.Sqlite、SQLitePCLRaw 等）與 `apphost`，授權聲明在發 release 之前補齊（目前沒有發 release）。舊 README 另外寫了自封式版本裡的 .NET 執行階段；第一版不發自封式（見[和設計規格的差異](spec-deviations.md)）。 |

已知限制的來源（舊 README 的開頭）：

> 下面的問題都不會連續送出無效憑證。第 1～28 項經過審查，影響都評為「低」（要特定時機或少見的條件才會發生，或只影響顯示），依影響由大到小排列。第 29～35 項來自 2026-10-30 的第二階段驗收，第 36 項來自 2026-10-30～31 的手動測試，接在最後，沒有和前面一起排序；它們的影響評級寫在各項「會怎樣」欄的最後，是驗收時訂的，不是程式審查的評級。第 37、38 項來自 2026-11-03 的代碼審查，同樣接在最後，影響評級是那次審查訂的。第 35 項是重量寬度時用量測工具與假資料看到的，其他是實機驗收。標「未實測」的是讀程式推得、沒有實際重現，標「推測」的是連會不會發生都還沒確認；「實測 1 次」是只觀察過一次（n=1）。
