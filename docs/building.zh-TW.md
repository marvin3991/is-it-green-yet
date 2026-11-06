# 自行建置

[English](building.md) | 繁體中文

回到 [README](../README.zh-TW.md)。

## 建置

需求：.NET SDK 10（`global.json` 指定 `10.0.401` 起、允許較新的 feature band）、PowerShell 7。開發與測試環境是 Windows 11（x64）。

```powershell
git clone https://github.com/marvin3991/is-it-green-yet.git IsItGreenYet
cd IsItGreenYet
pwsh -File tools/publish.ps1
```

腳本會先跑完整單元測試，測試沒過就不產生輸出；接著重新產生圖示，最後一次輸出兩個 win-x64 單一執行檔到 `dist\`：

## 兩個執行檔

| 檔案 | 是什麼 | 大小 | 需要什麼 |
|---|---|---|---|
| `IsItGreenYet.exe` | App | 3,656,192 bytes（約 3.49 MiB） | 電腦需已安裝 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `IsItGreenYet-cli.exe` | [命令列](cli.zh-TW.md) | 3,332,620 bytes（約 3.18 MiB） | 電腦需已安裝 .NET 10 Runtime（裝了 .NET 10 Desktop Runtime 就有：Desktop Runtime 的安裝程式包含 .NET Runtime） |

大小是版本 `0.3.0` 某一次建置的值（MiB 以 1024² bytes 換算），每次建置都可能不同。兩個都是相依框架版（framework-dependent），不含 .NET Runtime；第一版不發自封式（self-contained），和規格不同的地方見[和設計規格的差異](spec-deviations.md)。安裝 Runtime 的指令與缺 Runtime 時的提示見 [README 的系統需求](../README.zh-TW.md#系統需求)。`dist\` 只會有這兩個檔案。命令列的 `status --json` 只讀 App 寫的狀態，命令列自己不打 GitHub API，所以 `status --json` 要搭配執行中的 App 使用（見[命令列](cli.zh-TW.md)）。

## 執行檔的注意事項

- 單一執行檔、沒有安裝程式，程式本身不寫 Program Files：裝好 Runtime 之後，把 exe 複製到資料夾直接執行。不要放在別人或瀏覽器能寫入的資料夾（例如下載資料夾），exe 旁邊的同名 DLL 或 `gh.exe` 會被載入（[已知限制第 37 項](known-limitations.zh-TW.md)）。程式自己的資料只寫在 `%LOCALAPPDATA%\IsItGreenYet`、HKCU 的開機自啟值與 Windows 認證管理員（使用 PAT 時），所以 exe 本身依設計不需要系統管理員權限（以一般權限執行還沒有實測）；安裝 Runtime 可能需要（見 [README 的系統需求](../README.zh-TW.md#系統需求)）。
- 需要的原生 DLL（SQLite 的 `e_sqlite3.dll`）已包進 exe，啟動時解壓到 `%TEMP%\.net\<執行檔名稱>\<雜湊>\`。每換一個版本就多一個雜湊子資料夾，舊的不會自動清除；程式沒在執行時，可以手動刪掉 `%TEMP%\.net\IsItGreenYet*\` 底下的舊資料夾。
- 建議把執行檔放在固定位置：Windows 11 依執行檔路徑記住系統匣圖示的釘選位置（第三方文件的說法，未經官方確認）。
- 系統匣圖示第一次出現時在 Windows 11 的「隱藏的圖示」區，要不要釘到工作列由你決定。「設定 > 個人化 > 工作列 > 其他系統匣圖示」裡顯示的名稱是 `IsItGreenYet`。
- 執行檔沒有程式碼簽章，見下一節。

## 沒有程式碼簽章

發行的 exe 沒有程式碼簽章。SmartScreen 的信譽看檔案雜湊與憑證，沒有簽章時每個新版本都從零開始（[微軟文件](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)），所以每一版都可能跳警告。使用者的處理順序如下，指令見 [README](../README.zh-TW.md#沒有程式碼簽章)：

1. 只從本專案的 Releases 下載。
2. Edge 攔下下載時，確認網址之後在下載清單選擇保留（確切的按鈕文字還沒有驗證）；Edge 明白說檔案有害時不要保留。
3. 移到自己專用的資料夾，不要留在下載資料夾。
4. 比對雜湊。這只防下載損毀，不防掉包。
5. 雙擊 `IsItGreenYet.exe`；`IsItGreenYet-cli.exe` 要在 PowerShell 裡執行。
6. 只有畫面寫的是無法辨識的應用程式、發行者不明，才按「仍要執行」。
7. 紅色封鎖，或 Microsoft Defender 把檔案隔離時，停下來開 issue。

開著 [Smart App Control](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview) 的電腦可能完全跑不起來（沒有實測），公司政策也可能擋下。文件不教關閉 SmartScreen、不用萬用字元的 `Unblock-File`、不教繞過公司政策。SmartScreen 的畫面在 0.3.0 的[發版流程](#發版流程)第 4 步看過，文字補在 README；Edge 的下載畫面還沒看到。

## `publish.ps1` 的參數

| 參數 | 說明 |
|---|---|
| `-SkipTests` | 略過單元測試（會顯示警告） |
| `-OutputDirectory <路徑>` | 輸出資料夾，預設 `dist\`。會整個刪掉重建（不經過資源回收桶），所以不能指到磁碟根目錄、repo 根目錄或 repo 的上層資料夾；路徑是檔案，或資料夾裡有兩個發行檔以外的項目（包括隱藏檔與子資料夾，舊版留下的 `-selfcontained` exe 也算，要先手動刪除）時，跑測試之前就中止並列出第一個不符的名稱，什麼都不刪 |

## 開發時的一般指令

```powershell
dotnet build
dotnet test
dotnet run --project src/IsItGreenYet.App
dotnet run --project src/IsItGreenYet.Cli -- status --json
```

## 只重新產生圖示

只重新產生圖示（`-PreviewPath` 另外輸出 256／32／16 的放大預覽圖）：

```powershell
pwsh -File tools/generate-icon.ps1 -PreviewPath docs/icon-preview.png
```

## 發版流程

`.github/workflows/release.yml` 在推送 `v*.*.*` tag 時，把發行檔建成 **draft** release；專案作者確認內容之後，才手動發佈。

1. 改 `src/IsItGreenYet.App/IsItGreenYet.App.csproj` 與 `src/IsItGreenYet.Cli/IsItGreenYet.Cli.csproj` 的 `<Version>`，兩個要相同。
2. 開 PR、合併。
3. 在 `main` 上打 tag 並推送，tag 是 `v` 加上版本（例如 `v0.3.0`）：`git tag v0.3.0`、`git push origin v0.3.0`。
4. workflow 跑完後，到 Releases 頁面確認 draft 內容：兩個 exe、`SHA256SUMS.txt`、`LICENSE`、`THIRD-PARTY-NOTICES.md`，以及自動產生的 release notes。接著親手驗 CI 建的 exe：
   - 看 build job 的「檢查發行的 exe」步驟（`tools/check-release-exes.ps1`）。它確認 bundle 清單、`e_sqlite3.dll` 的雜湊、host 區段與著作權欄位都符合 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) 的依據，不符時 workflow 就中止，不會有 draft。把它印出的 SDK 版本、apphost 的來源與版本字串，和上一版與聲明檔比對，連同兩個 exe 的雜湊記到 [testing/acceptance.md](testing/acceptance.md)。不一樣時先查原因；還沒 Publish 的 draft 可以刪掉，刪掉 tag 再重來。
   - 用瀏覽器下載 draft 的 exe 與 `SHA256SUMS.txt`。要用瀏覽器才有網路標記（`Zone.Identifier`）：`curl.exe` 與 `Invoke-WebRequest` 不寫（2026-11-05 本機實測），`gh release download` 也不寫（2026-11-06 實跑）。
   - 移到自己專用的資料夾，不要留在下載資料夾。在同一顆磁碟搬移會保留網路標記（推論，沒有實測）。
   - 照 [README](../README.zh-TW.md#沒有程式碼簽章) 的指令驗 SHA256。
   - 先從系統匣結束正在跑的 App：App 是單一實例，第二份只會叫出第一份就結束，看到的會是舊版。啟動 draft 的 App，等主視窗出現後再跑命令列（`IsItGreenYet-cli.exe status --json`），確認 `appVersion` 是這次 tag 的版本，而且 `reason` 不是 `app_not_running` 或 `stale`。這兩個 `reason` 代表 `appVersion` 是上一份 App 留下的值：新的 App 還沒寫入或沒有跑起來，等幾秒再跑一次。記下 SmartScreen 與 Edge 的畫面，把實際的文字補進 README 與上面的[沒有程式碼簽章](#沒有程式碼簽章)。
   - 網路標記測試：用下面的指令把 exe 複製兩份，分別寫入 `ZoneId=2`、`ZoneId=3` 的 `Zone.Identifier`，從檔案總管雙擊，比較兩份跳不跳警告；App 是單一實例，換另一份之前先結束正在跑的那一份。電腦要連網。Zone 3 也沒跳警告時，這次比較不算數，不能拿來判斷 Zone 2；同一個雜湊先前按過「仍要執行」會不會影響結果，沒有資料。結果記下來：winget 裝的檔案會標成 Zone 2（讀 winget-cli 原始碼，要雜湊相符而且來源受信任），所以 Zone 2 仍跳警告時，winget 套件消不掉警告（推論）。測完刪掉這兩份與 `%TEMP%\.net\IsItGreenYet-zone*`。

     ```powershell
     Copy-Item .\IsItGreenYet.exe .\IsItGreenYet-zone2.exe
     Copy-Item .\IsItGreenYet.exe .\IsItGreenYet-zone3.exe
     Set-Content -LiteralPath .\IsItGreenYet-zone2.exe -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=2"
     Set-Content -LiteralPath .\IsItGreenYet-zone3.exe -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
     ```

   - 把自動產生的 release notes 換成手寫的中英短版，寫明要下載哪個檔、沒有簽章、聲明在哪裡。
5. 按 Publish。

tag 含 `-`（例如 `v0.3.0-beta.1`）時會標成 prerelease。`/releases/latest` 不回 prerelease，所以 App 不會通知這種版本。

發佈之後不撤下、不刪除 release，有問題就發更新的修正版。App 只記一個通知過的版本，撤下之後，比它舊而沒通知過的版本不會跳提示（[已知限制](known-limitations.zh-TW.md)第 45 項）。

以下任一項不符時，workflow 中止，不會產生 release：

- tag 去掉 `v` 之後，要等於兩個 csproj 的 `<Version>`，而且是合法的 SemVer。
- tag 指向的 commit 要在 `main` 上，而且建立 release 時 tag 還指向這次建置的 commit（建置期間被移動就中止）。
- `THIRD-PARTY-NOTICES.md` 不能有 `RELEASE-BLOCKER` 標記。這個標記表示第三方聲明還有待辦：有新的待辦時加上，確認之後刪掉那一行。
- `tools/check-release-exes.ps1` 的檢查要全部通過（見第 4 步）。
- 不能有同名的 release（包括 draft），不會覆蓋。

打 tag 之前可以先跑 dry-run：到 Actions 頁面手動執行 Release，或執行 `gh workflow run release.yml --ref main`（`--ref` 也可以是分支）。它會檢查兩個 csproj 的版本彼此一致、跑完整單元測試、建置並檢查兩個 exe、產生雜湊，把產物上傳成 artifact `release-files`（保留 7 天），不建 release；有 `RELEASE-BLOCKER` 標記只顯示警告。
