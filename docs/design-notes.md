# IsItGreenYet 視覺設計說明

## 設計方向

沿用 SAM 的石墨深色介面與色票，**不新增任何顏色**（spec §16.1）。畫面上唯一要一眼讀出來的是「綠了沒」：狀態一律用「形狀＋顏色」雙重表達，並附 `AutomationProperties.Name` 與 tooltip，不只靠顏色。除了清單列「進行中」的旋轉圓弧，介面不放循環動畫（spec §16.4）。

## 色票

色票與字級全部來自 `src/IsItGreenYet.App/Themes/Design.xaml`（自 SAM 原樣複製）；圓角與間距的既有 token 也在那裡，本專案另外新增的放在 `Controls.xaml`（見「補充控制項樣式」）。本專案用到的語意如下，HEX 與 `Design.xaml` 相同。

| 語意名稱 | Token | HEX | 本專案的用途 |
|---|---|---:|---|
| Window Background | `Brush.Window.Background` | `#12171C` | 視窗主背景、圖示底色、系統匣角標底色 |
| Surface | `Brush.Surface` | `#1A2229` | 卡片、標題列 |
| Surface Hover | `Brush.Surface.Hover` | `#222D36` | 清單列滑入與選取底色 |
| Text Muted | `Brush.Text.Muted` | `#94A2AC` | 取消、略過、完成後未知、無資料 |
| Accent | `Brush.Accent` | `#4FC3B6` | 成功、repo 名稱文字、圖示前景 |
| Accent Foreground | `Brush.Accent.Foreground` | `#071513` | 系統匣圖示上的形狀 |
| Danger | `Brush.Danger` | `#FF7B79` | 失敗 |
| Warning | `Brush.Warning` | `#F3C969` | 進行中、排隊、等待核准、超過預估、連線角標 |

字型：`Consolas, Microsoft JhengHei UI`（`FontFamily.Default`）。2026-10-26 依專案作者指定改掉 SAM 的 `Segoe UI Variable, Microsoft JhengHei UI, Segoe UI`，中文先用程式內嵌的霞鶩文楷等寬 TC；2026-10-27 專案作者覺得中文顯示不如預期，2026-10-29 改成微軟正黑體，而且不內嵌字型檔。內嵌文楷時的做法與量測（字型檔、授權對話框、GDI 私有字型）見合併這些變更的 PR 的 commit 歷史（main 用 squash 合併，main 的歷史裡沒有那些 commit）。

> **換成微軟正黑體後重做的驗證（2026-10-29）**：2026-10-27 起和字型有關的量測都暫停（專案作者覺得文楷的中文顯示不如預期）；2026-10-29 改成微軟正黑體，專案作者看過截圖後確認，用開發機上的量測殼（不在 repo 裡；一般的 WPF `Application` 載入 App 的資源與視窗、假資料，不建立 App、不啟動監看引擎、不連網）重做全部暫停的驗證：
> - 主畫面寬度斷點：每一種狀態需要的列內容寬和換字型前逐筆相同（中文 `933`／`842`／`666`、英文 `951`／`860`／`684`，見下方「寬度斷點的量測」），第二行第一次截斷的視窗寬也相同（英文 `666`、中文 `654`），`Tuning` 的 `980`／`890`／`710` 不用改。中文字在兩種字型都是 `1 em` 寬、英數都是 Consolas，所以寬度不變。
> - 清單列（1100→600 每 1 DIP，兩種語言、四組資料）、摘要列（中文一行 `599`、英文 `742`，放不下時整段換行）、repo 管理頁的窄版（切換點英文 `630`／`620`、中文 `540`／`530`，12 組資料都沒有被切掉）、設定頁與對話框：和換字型前相同，沒有新的截斷。
> - 提示視窗標題行高維持 `24`：混排的行高仍取 Consolas（見上方）。
> - 單一執行檔：`3,355,295`／`159,224,542` bytes（相依框架／自封式，2026-10-29 換字型後的建置），比內嵌文楷時各少 `26,016,256` bytes。
> - 語言選單第 1 輪審查的發現做了第 2 輪複驗，見下方「語言 › 驗證」。

下面各節的數字，沒有註明日期的是換字型前的量測結果；上面列出的項目已經確認換字型後不變。

- WPF 依清單順序逐字找有字形的字型：英文、數字與符號（含 `·`、`…`）用 Consolas；Consolas 沒有的中文與全形標點（`，：（）「」／`）落到微軟正黑體。英文介面用同一組字型（見下方「語言」）。
- 兩個都是 Windows 內建的字型，只用名稱參照、不隨程式散布（見 repo 根目錄的 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)）；單元測試 `ThirdPartyNoticesTests` 擋住 App 專案再出現字型檔。微軟正黑體用介面版 `Microsoft JhengHei UI`：和一般版在同一個字型檔（`msjh.ttc`），字形相同，行距比較緊（`1.27 em`，一般版 `1.33 em`）。
- 字重：樣式只用 `Normal` 與 `FontWeight.Emphasis`（`575`，在 `Design.xaml`；原本的 `SemiBold` 8 處與 repo 名稱連結的 `Bold` 1 處都改用它）。575 原本是為了只有 Regular、Medium 兩個字重的文楷取的，換成微軟正黑體後照用：2026-10-29 查 WPF 的 `GlyphTypeface`，575 時 Consolas 是 `CONSOLAB.TTF`、微軟正黑體 UI 是 `MSJHBD.TTC`，都沒有 `StyleSimulations`（不是假粗體），結果和原本的 `SemiBold` 一樣；量測殼掃描全部畫面也沒有一段假粗體。
- 行高與基線取清單第一個字型 Consolas（行距 `1.1709 em`、基線 `0.920 em`；微軟正黑體 UI 自己是 `1.27 em`、`1.016 em`）。2026-10-29 實測：用 `FontFamily.Default` 的 TextBlock 不論內容是英文、中文或混排，14 DIP 一行都是 `16.39`、12 DIP 都是 `14.05`，和只用 Consolas 相同；中文字寬 `1 em`。比 Segoe UI Variable（`1.33 em`）矮：14 DIP 從 `18.62` 變 `16.39`、12 DIP 從 `15.96` 變 `14.05`。
- 滑鼠停留的提示文字（ToolTip）另外用隱含樣式設成同一組字型（`Controls.xaml`，外觀見下方「彈出畫面」）；WPF 內建的右鍵選單（輸入框的剪下／複製／貼上、捲軸的捲動選單）套主畫面的選單樣式，字型也同上。系統匣右鍵選單見下方「提示視窗與系統匣」。錯誤對話框是程式自己的視窗（`Views/ErrorDialog.xaml`），字型同上；只有它顯示失敗、退回系統 `MessageBox` 時是系統字型。

### 字型的驗證（2026-10-29）

用開發機上的量測殼（不在 repo 裡；一般的 `Application` 載入 App 的資源字典與視窗，假資料，不建立 App、不連網），中英文各截 35 張畫面：主視窗（1000 與 600 寬、各種橫幅、篩選、訊息條）、Status 與自動更新下拉、run 選單、空狀態、設定頁（720 與 480 寬）、repo 管理頁（720 與 480 寬）、提示視窗（單筆、長標題、彙總、鎖定期間）、錯誤對話框、系統匣選單。

- 逐一掃描實際畫出的文字段（`GlyphRun`）：中文介面用到的字型檔只有 `consola.ttf`、`consolab.ttf`、`msjh.ttc`、`msjhbd.ttc`，`3,081` 個中文與全形標點全部是微軟正黑體，`6,807` 個英文字母與數字全部是 Consolas；英文介面 `30` 個中文與 `14,454` 個英文字母與數字，同樣全部符合；沒有一段是假粗體。
- 截斷：中文介面的截斷清單和換字型前（同一個量測殼、同一天截的改前版本）逐筆相同。
- 系統匣選單（GDI）的字型槽是 Consolas 與 Microsoft JhengHei UI（見下方「提示視窗與系統匣」）。

## 狀態色對應

### 清單列的狀態圖示（spec §7.1）

| 顯示狀態 | status／conclusion | 判定類別 | 圖示形狀 | 顏色 |
|---|---|---|---|---|
| 排隊 | `queued`、`requested`、`pending` | 未完成 | 空心圓＋中心點 | Warning |
| 進行中 | `in_progress` | 未完成 | 3/4 圓弧旋轉（唯一的動畫，每秒最多 30 幀；關閉動畫效果時改成靜態圓弧） | Warning |
| 未知（顯示原字串） | 其他非 `completed` 的值、或 status 為 null | 未完成 | 空心圓＋問號 | Warning |
| 等待核准 | `waiting`，或 `completed`＋`action_required` | 等待 | 圓＋時鐘指針 | Warning |
| 成功 | `completed`＋`success` | 成功 | 實心圓＋✓ | Accent |
| 失敗／失敗（逾時）／失敗（啟動失敗） | `failure`、`timed_out`、`startup_failure`（`startup_failure` 出現在 status 也算） | 失敗 | 實心圓＋✕ | Danger |
| 取消 | `cancelled` | 灰 | 空心圓＋斜線 | Text Muted |
| 略過／略過（neutral）／略過（stale） | `skipped`、`neutral`、`stale` | 灰 | 空心圓＋短橫線 | Text Muted |
| 未知（顯示原字串） | `completed`＋null 或不認得的結論 | 灰 | 空心圓＋問號 | Text Muted |

不認得的狀態在完成之前一律當「未完成」（黃），避免 GitHub 新增代表執行中的值時誤顯示綠燈。

### 其他用到狀態色的地方

| 位置 | 對應 | 依據 |
|---|---|---|
| 系統匣圖示（整體狀態） | 32 × 32 格線上的圓角方塊（半徑 8）填狀態色，上面用 Accent Foreground 畫形狀：失敗 Danger＋✕、進行中或排隊 Warning＋「…」、全部成功 Accent＋✓、無資料 Text Muted＋「–」。形狀一律用線段與圓畫，不用文字。靜態，不做動畫 | spec §7.3、§12；`Services/TrayIconFactory.cs` |
| 系統匣角標（連線問題） | 右下角半徑 7 的小圓：Window Background 底＋Warning「!」，外圈用狀態色描 1.5，在深色工作列上也看得出輪廓。有角標時形狀往左上移 5（32 格線），讓出右下角。疊加在狀態色上，不取代狀態色 | spec §7.3；`Services/TrayIconFactory.cs` |
| 橫幅框線 | Surface 底；框線一般是 Warning，API 版本錯誤與未預期錯誤用 Danger | spec §10.2；`MainViewModel.BannerTone` |
| 提示視窗框線 | 失敗或含失敗的彙總 Danger；成功 Accent；灰色結論 Text Muted | spec §9.4 |
| ETA 進度條 | 平常用 `Brush.Text.Secondary`；已執行時間超過預估時，滿格改用 Warning | spec §8.2；`Style.ProgressBar.Thin` |

系統匣的右鍵選單和主畫面的選單一樣是深色（Surface 底、Surface Hover 的選取底色與分隔線、Border 框線、Text Primary 文字，停用的項目 Text Muted）；外觀與字型見下方「提示視窗與系統匣」。

## 對比值

數值照 spec §16.2。新的組合依 WCAG 的 sRGB 相對亮度公式計算：

- 每個通道 `v = 8 位元值 ÷ 255`；`v ≤ 0.04045` 時 `c = v ÷ 12.92`，否則 `c = ((v + 0.055) ÷ 1.055)^2.4`。WCAG 2.x 原文的門檻寫 `0.03928`，對 8 位元色值結果相同：`10/255 = 0.0392`、`11/255 = 0.0431`，都落在兩個門檻的同一側。
- 相對亮度 `L = 0.2126 × R + 0.7152 × G + 0.0722 × B`（R、G、B 是上面的 `c`）。
- 對比值 `= (L較亮 + 0.05) ÷ (L較暗 + 0.05)`，四捨五入到小數 2 位。

2026-10-25 用這個算式重算表中 13 個組合，以及 SAM 既有的 `16.46`（Text Primary／Window Background）、`5.36`、`6.41`（Danger／Surface）、`10.25`（Warning／Surface）、`8.72`，全部和 spec 吻合。

| 組合 | 對比值 | 用途 |
|---|---:|---|
| Accent / Window Background | `8.42:1` | 清單列狀態圖示 |
| Accent / Surface | `7.52:1` | 卡片內狀態圖示 |
| Accent / Surface Hover | `6.56:1` | 滑入列的狀態圖示 |
| Danger / Window Background | `7.18:1` | 同上 |
| Danger / Surface Hover | `5.59:1` | 同上 |
| Warning / Window Background | `11.48:1` | 同上 |
| Warning / Surface Hover | `8.93:1` | 同上 |
| Text Muted / Window Background | `6.89:1` | 同上 |
| Text Muted / Surface Hover | `5.36:1` | 同上（SAM 既有值） |
| Accent Foreground / Accent | `8.72:1` | 系統匣全部成功圖示的形狀（SAM 既有值） |
| Accent Foreground / Danger | `7.43:1` | 系統匣失敗圖示的形狀 |
| Accent Foreground / Warning | `11.88:1` | 系統匣進行中圖示的形狀 |
| Accent Foreground / Text Muted | `7.13:1` | 系統匣無資料圖示的形狀 |
| Text Primary / Surface Hover | `12.82:1` | 滑鼠停留提示的文字（2026-10-26 新增，同一個算式計算） |

最低的組合是 Text Muted / Surface Hover 的 `5.36:1`，高於非文字元件需要的 `3:1`，也高於正文的 `4.5:1`。彈出畫面另外用到的組合都是 SAM 既有值：Text Muted / Surface `6.15:1`（提示視窗標頭的名稱、系統匣選單停用的項目）、Text Secondary / Surface `9.27:1`（錯誤對話框的訊息）、Danger / Surface `6.41:1`（錯誤圖示的實心圓、失敗提示的框線）、Accent Foreground / Accent `8.72:1`（強調色按鈕的字）；錯誤圖示上的叉是上表的 Accent Foreground / Danger `7.43:1`。

## 應用程式圖示概念

圖示採「圓環＋打勾」，也就是「綠了沒」的答案。輪廓粗、不依賴細節或文字，縮到 `16 × 16` 仍能辨識。由 `tools/generate-icon.ps1` 產生，輸出 16、20、24、32、48、64、128、256 八種尺寸，組成 `src/IsItGreenYet.App/Assets/app.ico`（同 SAM）。

- ViewBox：`0 0 256 256`
- 背景：`#12171C` 的圓角方塊，半徑 `48`
- 前景：`#4FC3B6`
- 填色規則：`EvenOdd`（讓圓環中空）
- 可直接指定給 WPF `Path.Data` 的幾何路徑：

```text
F0 M 32,128 A 96,96 0 1 1 224,128 A 96,96 0 1 1 32,128 Z M 58,128 A 70,70 0 1 1 198,128 A 70,70 0 1 1 58,128 Z M 77,131 L 101,107 L 116,122 L 154,84 L 178,108 L 116,170 Z
```

### 和 spec §16.3 的差異：打勾加粗

spec 的預覽在 `16 × 16` 時打勾略糊，spec 允許實作時加粗。圓環照 spec 不變；只改打勾，推導如下（座標都在 256 的設計尺寸上）：

| 項目 | spec §16.3 | 本版 |
|---|---|---|
| 圓環 | 圓心 `(128,128)`，外圓半徑 96、內圓半徑 70，粗 26 | 同左 |
| 短臂兩邊（斜率 +1） | `y − x = 12`、`y − x = 48` | `y − x = 6`、`y − x = 54` |
| 長臂兩邊（斜率 −1） | `x + y = 244`、`x + y = 280` | `x + y = 238`、`x + y = 286` |
| 兩臂寬度（兩條平行邊的截距差 → 垂直於臂的寬度） | 截距差 36 → 36 ÷ √2 ≈ 25.5 | 截距差 48 → 48 ÷ √2 ≈ 33.9 |
| 短臂末端 | `x + y = 208` | 同左 |
| 長臂末端 | `x − y = 76` | `x − y = 70`（內縮 6） |
| 頂點（上面直線的交點） | `(80,128)`、`(98,110)`、`(116,128)`、`(160,84)`、`(178,102)`、`(116,164)` | `(77,131)`、`(101,107)`、`(116,122)`、`(154,84)`、`(178,108)`、`(116,170)` |
| 打勾離內圓最近的點 | `(178,102)`：距圓心 √(50² + 26²) ≈ 56.4，離內圓 13.6 | `(178,108)`：距圓心 √(50² + 20²) ≈ 53.9，離內圓 16.1 |

- 兩臂往兩側各加寬 6，中心線不變（短臂 `y − x = 30`、長臂 `x + y = 262`）。
- 長臂末端內縮 6，是為了讓打勾和內圓之間至少留 16，也就是 `16 × 16` 時的 1 px（256 ÷ 16 = 16），兩者才不會糊在一起。
- 打勾的外框是 x 77–178、y 84–170，中心 `(127.5, 127)`，和圓心 `(128,128)` 幾乎重合（spec 版的中心是 `(129, 124)`）。

預覽（`pwsh -File tools/generate-icon.ps1 -PreviewPath docs/icon-preview.png` 產生；左起 256、32 放大 8 倍、16 放大 16 倍，最近鄰放大）：

![圖示預覽](icon-preview.png)

spec 原版的預覽在 [specs/2026-10-24-icon-preview.png](specs/2026-10-24-icon-preview.png)，可以並排比較。

## 主畫面版面

依 `src/IsItGreenYet.App/MainWindow.xaml` 的實際配置畫的文字示意。預設 `1000 × 640 DIP`，最小 `600 × 400 DIP`，可縮放；雙擊標題列最大化／還原，最大化時去掉圓角、不蓋住工作列。

中文字在等寬字型裡佔兩格，右側不畫框線，各區塊的說明列在圖下方。示意圖列出每一列所有的元素；預設寬度下觸發者會被寬度斷點隱藏（見下方「寬度不夠時依序隱藏」）。

```text
┌─ ① ──────────────────────────────────────────────────────────────
│ IsItGreenYet                               [置頂] [repo 管理] [設定] [✕]
├─ ② ──────────────────────────────────────────────────────────────
│ 離線 · 最後更新 10:07:33                                     [立即重試]
├─ ③ ──────────────────────────────────────────────────────────────
│ 24 筆執行 · 2 進行中 · 1 失敗 · 最後更新 10:07:33 · API 剩餘 4870/5000   [自動更新：自動 ▾]
├─ ④ ──────────────────────────────────────────────────────────────
│ [Repo ▾] [Workflow ▾] [Event ▾] [Status ▾] [Branch ▾] [Actor ▾] [搜尋（Ctrl+F）] [ ] 只看需要注意 [清除篩選]
├─ ⑤ ──────────────────────────────────────────────────────────────
│ ✕  修正登入流程                                                   今天 08:32
│    sample-app · CI #128 · Pull request #17 · by alice  [第 2 次嘗試] [feature/login]  3m 12s  [⋯]
│ ◔  更新相依套件                                                   5 分鐘前
│    sample-app · Lint #57 · Commit dcf27ad · by alice  [main] [鈴鐺]  已執行 4m 12s · 約剩 3m  [⋯]
│                                                                    ▓▓▓▓▓▓░░░
│ ✓  …
└──────────────────────────────────────────────────────────────────
```

| 區塊 | 內容 |
|---|---|
| ① 標題列 | 高 `44 DIP`、整條可拖曳；按鈕依序是置頂、repo 管理、設定、✕。置頂中的圖示用 Accent；✕ 的 tooltip 依「關閉時收到系統匣」與系統匣是否可用，寫「收到系統匣」或「結束」 |
| ② 橫幅 | 有事才出現，只顯示一則，一般依嚴重度挑最重要的（`BannerKind` 的宣告順序：未登入、主額度用完、次級限流、離線、5xx、API 版本錯誤、API 版本即將停止支援、快取資料庫復原、設定檔復原、未預期錯誤）。例外是監看引擎的三種一次性提示（API 版本即將停止支援、監看引擎讀寫時發現損毀而重建的快取資料庫復原、未預期錯誤）之間是較新的優先，同一份快照之前一起發生的才照宣告順序（`MonitorEngine.SelectBanner`；和 spec §10.2 的差異見[和設計規格的差異](spec-deviations.md)）。右邊依種類出現「重新檢查」「開啟設定」「立即重試」「開啟 repo 管理」「開啟資料夾」；API 版本即將停止支援、快取資料庫復原、設定檔復原、未預期錯誤這四種另外有 ✕ |
| ③ 摘要列 | 計數不受篩選影響；還沒有任何 core 回應時不顯示「API 剩餘」；「N 個 repo 無法更新」有值時才加在後面；右邊是「自動更新：<模式>」下拉。寬度不夠時整段換到下一行、不截斷（見下方「摘要列換行」）；tooltip 與報讀名稱是全文 |
| ④ 篩選列 | 6 個多選下拉（有勾選時按鈕顯示數量，例如「Repo（2）」）、搜尋框（提示字「搜尋（Ctrl+F）」）、「只看需要注意」；有任何篩選、搜尋或「只看需要注意」在作用時，最後多一個「清除篩選」chip。整列是 `WrapPanel`，寬度不夠時自動換行 |
| ⑤ 清單 | 每列兩行；進行中的列顯示已執行時間、ETA 與細進度條；虛擬化。清單空的時候在中間顯示空狀態 |

每一列是四欄、兩行的 Grid：

| 欄 | 第一行 | 第二行 |
|---|---|---|
| 狀態圖示 | 跨兩行、靠上 | （同左） |
| 內容（佔滿剩餘寬度） | `display_title`：換行改成空白、過長截斷加「…」，tooltip 顯示全文 | 左邊是文字段（過長截斷加「…」，整行的 tooltip 是全文），右邊依序是「第 N 次嘗試」小標（`run_attempt > 1`）、分支 chip、已標記「完成時通知我」時的鈴鐺 |
| 時間（各列等寬、靠右） | 開始時間；tooltip 是完整的本地時間 `yyyy-MM-dd HH:mm:ss` | 耗時；進行中改成「已執行 4m 12s · 約剩 3m」，下方是細進度條；排隊中是「排隊 1m 05s」 |
| ⋯ 按鈕 | 跨兩行、垂直置中 | （同左） |

- 第二行的文字段依序：repo 名稱 → `· workflow 名稱 #run_number` → `· 觸發說明` → `· by 觸發者`。
  - repo 名稱：粗體、Accent 文字，點了開 Actions 頁。自己的 repo 只顯示名稱，組織與協作的 repo 顯示 `owner/name`；tooltip 一律是 `full_name`（`ViewModels/RunRowViewModel.cs`）。
  - 觸發說明的 PR 編號與 commit 可點（`Style.Hyperlink.Inline`），其他事件是純文字。
  - `triggering_actor` 和 `actor` 不同時，觸發者的 tooltip 加「重跑者：…」。
- 分支 chip：fork 來的分支顯示成 `owner:branch`。
- 寬度不夠時依序隱藏（repo 名稱與狀態圖示永遠可見；斷點是 Core 的 `Tuning.HideActorBelow`／`HideBranchBelow`／`HideTimeDetailBelow`，spec 附錄 B 同步）：

  | 列內容寬度低於 | 換算成視窗寬低於 | 隱藏 |
  |---:|---:|---|
  | `980 DIP` | `1047 DIP` | 觸發者 |
  | `890 DIP` | `957 DIP` | 分支 chip |
  | `710 DIP` | `777 DIP` | 時間細節：第一行的開始時間；進行中的列只留已執行時間，不顯示 ETA 與進度條 |

  - 比較的「列內容寬度」是清單 `ScrollViewer.ViewportWidth`，再扣掉每一列的內距與外框（`Padding.Record` 左右各 10、`Thickness.Border.Standard` 左右各 1）。直向捲軸的寬度一律扣掉，捲軸沒出現時也扣，否則在斷點附近會「隱藏欄位 → 列變矮 → 捲軸消失 → 變寬 → 顯示欄位」不停來回切換（`MainWindow.UpdateListWidth`）。
  - 列內容寬度是視窗寬度減 `67`（2026-10-26 測試殼量的：視窗 `1400` 時列內容 `1333`，捲軸出現中）。所以預設寬度 `1000`（列內容 `933`）時觸發者已經隱藏，分支 chip 與時間細節顯示；視窗寬 `1047` 以上才顯示觸發者。
  - 截斷時的 tooltip：標題的 tooltip 是全文；第二行整行的 tooltip 也是全文（`RunRowViewModel.MetaTooltip`），而且一律含觸發者、「第 N 次嘗試」與分支（被斷點隱藏時也列出），例如「octo-org/sample-app · CI #128 · Pull request #17 · by octocat · 第 2 次嘗試 · feature/login-重新整理」；滑到 repo 名稱、觸發說明、觸發者上時仍是它們自己的 tooltip（`full_name`、多個 PR、完整 sha、重跑者）。
  - 斷點的量測與取捨見下方「寬度斷點的量測」。
- 空狀態（尚未登入、正在載入、離線且沒有快取、沒有資料、全部被篩選掉）在清單區置中顯示一行標題、必要時一行說明，加對應按鈕（spec §10.11）。有快取資料時照常顯示清單，不顯示空狀態。
- 訊息條（例如「已複製 run 網址。」）疊在畫面最下方，4 秒後自動消失。

### 寬度斷點的量測（2026-10-26）

換成 Consolas 之後拉丁字變寬（`12 DIP` 的字在 Display 文字模式下每字 `7 DIP`），原本 spec 的初值 `860／720／560` 在斷點之間會截斷，英文在預設寬度 `1000` 就截斷，所以依兩種語言重新量測、重訂斷點。

- 測試殼：一般的 `Application` 載入 App 的資源字典與正式的 MainWindow，假資料，不建立 App、不啟動 Engine、不連網；視窗高 `640`。
- 資料：同上一輪審查那一列偏長的假資料——組織 repo（顯示 `octo-org/sample-app`）、`CI #128`、`Pull request #17`、`by octocat`、「第 2 次嘗試」小標、分支 `feature/login-重新整理`；另一列進行中、有 ETA（時間欄各列等寬，這一列把整欄撐寬：中文「已執行 4m 14s · 約剩 3m」`151`、英文「Elapsed 4m 14s · ~3m left」`175`，加左邊距 `12`）；其餘 24 列填到出現捲軸。
- 方法：在寬視窗（`1400`）把每一列強制設成四種隱藏狀態，量第二行文字不受寬度限制時的寬度，加上同一列其他部分（狀態圖示欄、小標與 chip、時間欄、⋯）的實際寬度，就是第二行不截斷需要的列內容寬度。再把視窗從 `1100` 每 `1 DIP` 縮到 `600`，看每一列實際畫出的文字有沒有「…」來印證：換斷點前，前三個狀態第一次截斷的位置和量出來的值差 `1 DIP` 以內；最後一個狀態實際截斷得比量出來的晚 `8 DIP`：量的時候（寬視窗、列隱藏了 chip 而變矮）畫出了「排隊 12h 30m」（英文「Queued 12h 30m」）那一列，把時間欄撐到英文 `110`；實際縮到 `660` 時清單變矮（篩選列換行，清單高 `508 → 474`），那一列沒有畫出來，時間欄是 `103`。時間欄各列等寬、只算畫出來的列，所以同一份資料在不同視窗高度下需要的寬度也會差幾個 DIP。
- 結果（列內容寬度，DIP；括號是視窗寬）：

  | 狀態 | 中文 | 英文 |
  |---|---:|---:|
  | 全部顯示 | `933`（`1000`） | `951`（`1018`） |
  | 隱藏觸發者 | `842`（`909`） | `860`（`927`） |
  | 再隱藏分支 chip | `666`（`733`） | `684`（`751`） |
  | 再隱藏時間細節 | `595`（`662`） | `607`（`674`） |

- 斷點取兩種語言較大的值（斷點不分語言），往上取到 10 的倍數，多留約一個字（例如 run 編號多一位數，`7 DIP`）：當時取 `951 → 960`、`860 → 870`、`684 → 690`。
- 取捨：預設寬度 `1000` 不截斷，英文的「全部顯示」要 `951`、大於 `933`，所以預設寬度下觸發者隱藏（中文剛好 `933`，也因為斷點不分語言而隱藏）。觸發者仍在第二行的 tooltip 與列的報讀名稱裡。
- 審查後重訂（同一天）：審查回報英文在視窗寬 `757`（列內容 `690`）時第二行截斷，`690` 其實沒有餘裕。原因是進行中那一列的時間：上表的資料是「Elapsed 4m 14s · ~3m left」，分鐘都是 1 位數；審查的測試殼跑了 10 分鐘以上，已執行變成「10m 0Xs」，時間欄多 1 個字。已執行與剩餘都是 2 位數分鐘很常見（10～59 分鐘的 CI），改用「Elapsed 12m 34s · ~45m left」重量，時間欄英文 `201`、中文 `177`，都比 1 位數寬 `14`（2 個字）：
  - 英文用舊斷點從 `1100` 每 `1 DIP` 縮到 `600`，視窗寬 `1031`～`1027`、`940`～`937`、`764`～`757` 截斷，需要的列內容寬度是 `965`／`874`／`698`（上表加 `14`）。中文沒有截斷（上表加 `14` 是 `947`／`856`／`680`，推得、沒有直接量）。
  - 斷點改成 `965 → 980`、`874 → 890`、`698 → 710`（同樣多留約一個字再取到 10 的倍數），換算視窗寬 `1047`／`957`／`777`。預設寬度 `1000` 的隱藏狀態不變：觸發者隱藏，分支 chip 與時間細節顯示。剩餘時間 3 位數（`~120m left`）多 1 個字，還在多留的範圍內。
  - 重跑全寬掃描（審查的測試殼，`1100`～`600` 每 `1 DIP`，兩種語言）：1 位數與 2 位數的資料都只在三項都隱藏之後才截斷（英文 `617`／`624` 以下、中文 `623`／`630` 以下）。比較平常的資料（PR 四位數、觸發者 `dependabot[bot]`）英文在 `1051`～`1047` 截斷（舊斷點是 `1051`～`1027`），中文沒有截斷（舊斷點在 `1027`）：這份資料的觸發者比上表的長。
  - 上表那一套測試殼（1 位數的資料）重跑：每一個寬度的隱藏狀態都和 `Tuning` 一致，第二行只在三項都隱藏之後才截斷（英文 `666`、中文 `654` 以下；自己的 repo 英文 `603` 以下、中文不截斷），全部通過。
- 調整成 `960／870／690` 時，同一套測試殼從 `1100` 每 `1 DIP` 縮到 `600`：
  - 中文：視窗寬 `655` 以上沒有任何截斷；`654`～`600` 只有偏長那一列的第二行截斷（三項都已隱藏）。英文：`667` 以上沒有截斷；`666`～`600` 同上。標題都沒有截斷。每一個寬度的隱藏狀態都和 `Tuning` 的斷點一致。
  - 換斷點前（`860／720／560`）：中文視窗寬 `927`～`999`、`787`～`908`、`600`～`732` 截斷；英文 `787`～`1017`、`600`～`750` 截斷，預設寬度的英文就截斷。
  - 自己的 repo（只顯示 `sample-app`）：中文到 `600` 都不截斷，英文 `603` 以下才截斷。
- 已知限制（量了、沒有為它調斷點）：進行中的 run 超過預估時，時間欄變成「已執行 9m 40s · 超過預估約 3m」「Elapsed 9m 40s · ~3m over estimate」，整欄撐到中文 `199`、英文 `250`。同一份資料加這樣一列，需要的寬度變成中文 `969／878／702／588`、英文 `1014／923／747／600`：預設寬度 `1000` 仍不截斷，但斷點之間會截斷——`960／870／690` 時中文視窗寬 `1027`～`1035`、`937`～`944`、`757`～`768`，英文 `1027`～`1080`、`937`～`989`、`757`～`813`（截斷的列有 tooltip）。改成 `980／890／710` 之後重跑：中文需要的寬度都在斷點以下，不再截斷；英文仍在 `1080`～`1047`、`989`～`957`、`813`～`777` 截斷。要涵蓋英文這種情況得把斷點提高到約 `1020／930／750`，平常沒有超過預估時觸發者、分支 chip 與時間細節都會更早隱藏，所以沒有這樣做。repo、workflow、分支名稱比這份資料長時，同樣會在斷點之間截斷。
- 單元測試（`Repository/WidthBreakpointTests`）：三個斷點由寬到窄（觸發者 → 分支 chip → 時間細節）、spec 附錄 B 記的值和 `Tuning` 一致。

### 「取消中」的寬度重量（2026-10-30，第二階段）

第二階段 spec §9.3：取消中的列，進行中的預估剩餘換成「取消中」（已執行時間照常），排隊中與等待中的時間文字換成「取消中」，英文是 `Cancelling`。時間欄各列等寬，要看最寬的那一列。時間細節被斷點隱藏（列內容寬低於 `710`）時，進行中的列的「取消中」跟著隱藏，只剩已執行時間，「取消中」只留在狀態圖示的 tooltip 與報讀名稱；排隊中與等待中的「取消中」不受影響（[已知限制](known-limitations.zh-TW.md)第 35 項）。

- 測試殼與方法同上一節：寬視窗（`1400`）量四種隱藏狀態需要的列內容寬，再把視窗從 `1100` 每 `1 DIP` 縮到 `600` 印證；當時的 `Tuning` 是 `980`／`890`／`710`。
- 資料：上一節的資料（`base`），另外四組：
  - `waiting`：多一列等待中（沒有時間文字），是取消中的對照組。
  - `cancel`：`waiting` 的進行中（已執行 4 分多、約剩 3m）、排隊中、等待中三列都在取消中。
  - `base2`／`cancel2`：同 `waiting`／`cancel`，但進行中那一列的已執行與剩餘都是 2 位數分鐘（12m 36s、約剩 45m，上一節重訂斷點時用的那種）。
- 時間文字的寬度（DIP）：英文「Elapsed 4m 14s · Cancelling」`189`、「Elapsed 12m 36s · Cancelling」`196`，比「Elapsed 4m 14s · ~3m left」`175`、「Elapsed 12m 36s · ~45m left」`189` 寬 `14`、`7`；中文「已執行 4m 14s · 取消中」`142`，比「已執行 4m 14s · 約剩 3m」`151` 窄 `9`。排隊中與等待中的「取消中」`36`、`Cancelling` `70`，比排隊時間（「排隊 3h 30m」`73`、「Queued 3h 30m」`91`）窄。
- 結果（列內容寬度，DIP；依序是全部顯示／隱藏觸發者／再隱藏分支 chip／再隱藏時間細節）：

  | 資料 | 中文 | 英文 |
  |---|---|---|
  | `base` | `933`／`842`／`666`／`595` | `951`／`860`／`684`／`607` |
  | `waiting` | `933`／`842`／`666`／`588` | `951`／`860`／`684`／`600` |
  | `cancel` | `924`／`833`／`657`／`557` | `965`／`874`／`698`／`579` |
  | `base2` | `947`／`856`／`680`／`588` | `965`／`874`／`698`／`600` |
  | `cancel2` | `931`／`840`／`664`／`564` | `972`／`881`／`705`／`579` |

  - `base` 和 2026-10-29 量的逐筆相同。最後一個狀態 `base` 比 `waiting` 多 `7`：多一列之後「排隊 12h 30m」那一列沒有畫出來（同上一節，時間欄只算畫出來的列）。
- 斷點不改：最寬的是英文 `cancel2` 的 `972`／`881`／`705`，都在 `980`／`890`／`710` 以下（差 `8`／`9`／`5`）。全寬掃描兩種語言各 7 組資料（加上原本的 `overrun`、`own`）：每一個寬度的隱藏狀態都和 `Tuning` 一致，預設寬度 `1000` 不截斷；除了 `overrun`（上一節的已知限制，數字和上一節相同），第二行都只在三項都隱藏之後才截斷（第一次截斷的視窗寬：中文 `cancel` `623`、`cancel2` `630`，英文兩組都是 `645`）。

### 摘要列換行

摘要列原本是一個 TextBlock，寬度不夠時尾端截斷。排成一行需要的視窗寬：中文 `755`、英文 `940`（「26 runs · 2 in progress · 1 failed · Last updated 21:57:03 · API 4870/5000 left · 2 repos can't be updated」一行 `742`，加「自動更新」下拉 `166` 與邊距），比清單第二行更早截斷。

- 改成一段一段排（`MainViewModel.SummaryParts`，除了最後一段，每段結尾帶「 · 」，接起來就是 `SummaryText`；`ItemsControl` 加 `WrapPanel`）：寬度不夠時整段換到下一行，不在一段中間斷開（中文也不會在「無法更新」之類的字中間斷開）。單獨一段比整列還寬時才截斷（最長的一段中文 `143`、英文 `168`；最小寬度 `600` 時摘要可用中文 `444`、英文 `402`，實際上不會發生）。
- tooltip 與報讀名稱（`AutomationProperties.Name`）是全文 `SummaryText`。內容沒變時沿用同一份清單，每 30 秒重算時不重建每一段。
- 2026-10-26 同一套測試殼：排成一行時和原本一個 TextBlock 一樣寬（中文都是 `599`、英文都是 `742`：段尾的空白有算進寬度，間距不變）；中文視窗寬 `754` 以下兩行，英文 `939`～`625` 兩行、`624` 以下三行；`1100`～`600` 都沒有截斷。
- 執行中換語言（`Loc.Apply`）後，摘要列各段、它的 tooltip 與第二行的 tooltip 都換成新的語言（兩個方向都核對）。上一步的語言測試殼（主畫面、設定頁、repo 管理頁、提示、對話框、系統匣選單的截圖與字型掃描，以及執行中切換）重跑全部通過。

## 提示視窗與系統匣

- 提示視窗：Surface 底、框線色見上方「其他用到狀態色的地方」；寬 `180`–`420 DIP`，標題最多兩行（超過用省略號，tooltip 顯示全文）。彙總的每一行沒有狀態圖示，只靠文字開頭的顯示狀態（例如「失敗：」「成功：」「取消：」）區分。
  - 最上面一行是標頭（同 Windows 11 通知）：App 圖示（`Size.Icon` 的 16，程式依 DPI 從 `app.ico` 挑不小於顯示像素的那一格，100% 用 16、150% 用 24）加 Text Muted 的「IsItGreenYet」，右邊是 ✕；標題在下一行。標頭算空白處（單筆開那筆 run、彙總開主視窗）。
  - ✕ 是 `28 × 28`（同列上的 ⋯），`Margin.Toast.Close` 讓它的中心離右緣 `26`、離上緣 `20`，和 App 圖示的中心（離左緣 `26`、離上緣 `20`）對稱；2026-10-26 測試殼量到的差是 `0`。
  - 外框交給 DWM（見下方「彈出畫面」）：不透明視窗，圓角、陰影與框線色由 DWM 畫，框線色每次換內容時跟著語氣重設。不透明視窗不能用 `Opacity = 0` 藏著先量大小（會在預設位置閃出一個帶陰影的方塊），所以顯示前先建立 HWND、量好大小、移到右下角，最後才顯示；2026-10-26 用測試殼攔視窗訊息確認，第一次與隱藏後再顯示，顯示的那一刻都已經在最後的位置與大小。
  - 標題行高固定 `24`（兩行 `48`）：原本照 Segoe UI Variable 的自然行高取的，換字型後自然行高約 `21.08`，維持 `24` 讓提示高度不變。
  - 視窗開了版面取整（`UseLayoutRounding`）：寬度跟著文字是小數時，✕ 會落在半個像素上，✕ 的框對齊像素、圖示沒有，兩者差 `0.4 px`（2026-10-26 量測）。
- 系統匣圖示的幾何見上方「其他用到狀態色的地方」；100% 縮放時系統匣把 32 × 32 的圖示縮成 16 × 16、150% 時縮成 24 × 24（`TrayIconFactory` 的註解）。
- 系統匣右鍵選單（WinForms）的外觀由 `Services/TrayMenuRenderer.cs` 畫（顏色與尺寸都從 WPF 資源讀，見下方「彈出畫面」）：不顯示左邊的圖示欄；項目高 `32`（同主畫面選單項目），選取時畫圓角 `Radius.Control` 的底色，左右離外框內側 `Padding.Menu`；第一項上方與最後一項下方也是外框內側加 `Padding.Menu`（WinForms 的下拉選單不理會 `Padding`，2026-10-26 實測設了也不變，改用第一項與最後一項的邊距補）。項目是用上下內距撐高到 `32` 的，但下拉選單算文字區時不算這些內距，文字區仍擺在項目上方（2026-10-26 實測文字區 Y=2、高 19，文字上方留 4 px、下方留 13 px），所以畫文字前把文字區移到項目高度的正中間（`TrayMenuRenderer.CenterTextVertically`，逐字挑字型的畫法與退回系統字型的畫法都經過）；修正後用測試殼量文字墨水的上下留白：全中文的項目 `8`／`9`、「repo 管理」`8`／`8`，滑過時相同；清單裡的字型都沒裝、退回系統字型時，每一項的文字都往下移 `3`，行框上下各留 `5`／`6`（墨水高低依字形而異）。選單本身仍是 NotifyIcon 開的 ContextMenuStrip：在游標位置開啟、點外面或 Esc 關閉、方向鍵與 Enter 都照 WinForms，沒有改。
- 系統匣右鍵選單的字型同 `FontFamily.Default`，字級維持系統選單字級。WinForms 的字型只能指定一個，缺字時由 GDI 自己找替代字型（不一定是清單裡的中文字型），所以 `Services/MixedFontMenuRenderer.cs` 照 WPF 的規則逐字挑字型、分段畫、基線對齊（GDI 的 `tmAscent`）；選單本身的 `Font` 設成清單第一個字型（Consolas），WinForms 用它量寬度，中文不論哪個字型都是全形一個字寬。哪個字型有哪些字，用 WPF 讀同一個字型檔的字元表判斷；只用裝在這台電腦上的字型（照名稱建立 GDI+ 字型），清單裡的字型都用不到時維持系統字型。
- 2026-10-29 量測殼驗證（不建立 NotifyIcon、不碰系統匣，照 `TrayIconController` 的做法把 renderer 套到 ContextMenuStrip）：兩個字型槽是 Consolas 與 Microsoft JhengHei UI，選單的 `Font` 是 Consolas 12pt；中英文介面、一般與暫停中的選單各截一張，項目高 `32`，中英文的基線對齊。2026-10-26 換字型前的驗證：全中文的項目和「整個選單用中文槽的字型」逐像素相同（含滑過時的選取狀態），「repo 管理」的 `repo` 和「整個選單用 Consolas」逐像素相同。

## 語言（英文／繁體中文）

2026-10-26 依專案作者要求（「還要新增語言選單 英文、繁體中文」）加上。spec 沒有這一項；[使用說明的「外觀與語言」](usage.zh-TW.md#外觀與語言)已補上（2026-10-29）。

### 設定與預設

- 設定頁新增「語言」卡片（在「一般」與「資料」之間），兩個選項一律用自己的語言顯示：「English」「繁體中文」；標題附上另一種語言（中文介面「語言／Language」、英文介面「Language / 語言」），切錯語言時也找得到。和其他設定一樣按「儲存」才生效。
- settings.json 新欄位 `language`：`zh-Hant`／`en`（`AppSettings.Language`，寫在 `minimizeToTray` 之後）。讀到不認得的值（`zh-TW`、`EN`、空字串、數字、null、物件、陣列）退回繁體中文，不算損毀、不影響其他欄位（同 `refreshMode` 的轉換器）。
- 預設：settings.json 已經存在但沒有這個欄位（既有使用者）→ 繁體中文；第一次執行（沒有 settings.json）→ Windows 顯示語言是任何一種中文（`TwoLetterISOLanguageName` 是 `zh`，含 `zh-CN`、`zh-HK`）用繁體中文，其他用英文。Windows 顯示語言取程式啟動時 UI 執行緒的 `CurrentUICulture`，App 在改掉它之前先記下來交給 `SettingsService`。設定檔損毀、改名保留後回到預設值時算既有使用者（繁體中文）。
- 建立資料夾、開啟快取資料庫失敗這兩個最早的錯誤訊息，在完整讀取 settings.json 之前就要顯示：App 先用 `SettingsService.PeekLanguage` 只讀出語言（不改動任何檔案，損毀的設定檔也不改名，規則同上），讀完整份設定後再套用一次。`PeekLanguage` 和 `Load` 一樣整份反序列化，結果和之後讀到的語言相同：審查回報原本只讀語言欄位，`{"language":"en","alwaysOnTop":"yes"}` 這種其他欄位型別錯誤的設定檔，偷看是英文、`Load` 當成損毀回到繁體中文；已補單元測試（4 種型別錯誤）。

### 字串表

- 所有畫面文字集中在 Core 的 `Localization/TextTable.cs`（327 項，2026-11-03）：一個類別，每一項都是 `required` 屬性；`TextTables.TraditionalChinese.cs`、`TextTables.English.cs` 各一個實例，少寫任何一項就編譯不過。放在 Core（不是 App）：Core 的單元測試要能檢查兩種語言，測試專案不參考 WPF 的 App。不用 resx：不需要衛星組件，字串就在 `IsItGreenYet.Core.dll` 裡，單一執行檔一定帶得到。
- 繁體中文照搬原本程式與 spec 的原文，沒有改寫。api.log、error.log 的內容（除錯用）與內部錯誤的例外訊息（程式寫錯才會出現的 `ArgumentException` 等）不翻，維持中文。
- 格式字串（`{0}`）用 InvariantCulture 填數字（`4870/5000` 不加千分位）。英文的單數與複數成對（名稱 `…One`／`…Other`，`UiText.Plural`），中文兩項相同。
- XAML 用 `{l:T 名稱}`（App 的 `Localization/TExtension.cs`）：繫結到 `Loc.Instance.Table.名稱`，換語言時 `Loc` 通知 `Table` 改變，所有繫結即時更新。ViewModel 與程式碼用 `Loc.Texts.名稱`（名稱由編譯器檢查）。
- 單元測試：兩種語言的項目一致、沒有空值、格式字串的參數一致而且都填得進、單數與複數成對、英文表沒有中文（語言選項與標題除外）、XAML 用到的名稱都在字串表裡、XAML 沒有寫死的中文（`AppXamlTextTests` 讀 repo 裡的 XAML 原始檔）。

### 語言從哪裡來

- Core 有一個共用的目前語言 `UiText.Current`。有文字的 Core 單元（`RunStateMapper`、`DisplayFormatter`、`StatusAggregator.TrayTooltip`、`NotificationQueue.TryFlush`、`RepoDiscovery.SkipReasonText`、`AppPaths.EnsureCreated`、`GhCliTokenProvider`、`PatTokenProvider`）都可以明確傳入語言；沒有語言參數的多載讀 `UiText.Current`，給 App（UI 執行緒）用，也讓既有的測試照原本的寫法在繁體中文下跑（測試組件的 ModuleInitializer 設成繁體中文；要改共用語言的測試放在不平行的 xUnit 集合）。
- Engine 不讀 `UiText.Current`，也不看執行緒的 CurrentUICulture：一律用自己設定裡的語言（`EngineSettings.Language`，由 `EngineDependencies.Settings` 與 `EngineCommand.SettingsChanged` 帶入）明確傳入，快照的列、橫幅、系統匣的原因行、repo 的問題標示、提示、複製失敗 log 與檢查連線的結果都是。取不到 token 的原因（例如「gh 沒有登入」）每次取 token 時讀 App 目前的設定（`AppHost` 傳給 token provider 的 `Func<AppLanguage>`）。

### 執行中切換

按「儲存」之後：

1. 設定頁把 `language` 寫進 settings.json，送 `SettingsChanged` 給 Engine（同其他設定）。
2. 設定頁關閉後 `MainWindow.ApplySavedSettings` 呼叫 `Loc.Apply`：設定 `UiText.Current`、把 UI 執行緒的 CurrentUICulture 換成 `zh-TW`／`en-US`、通知 XAML 繫結，再逐一觸發 `Loc.LanguageChanged`（其中一個處理失敗，其他照樣做完才往外丟）。
3. 訂閱 `LanguageChanged` 的地方重算程式算出來的文字：主視窗（置頂與 ✕ 的說明；`MainViewModel.RefreshLanguage`：自動更新、摘要列、空狀態、篩選欄位與 Status 選項、每一列的文字與報讀名稱、App 自己的橫幅）、系統匣（`TrayIconController.RefreshLanguage`：選單項目、用最近一次的快照重組 tooltip）、開著的 repo 管理頁與設定頁。清單列的顯示狀態先依目前的語言重新產生（`RunStateMapper.InLanguage`），不等 Engine 的快照。
4. Engine 處理 `SettingsChanged` 時語言不同就重建記住的文字（`MonitorEngine.ApplyLanguage`）：全域暫停的橫幅（保留恢復時間）、三種一次性提示（保留識別碼）、這次執行判定的 repo 問題標示（重新寫進 problem_note）。這一步結尾發布的快照就是新的語言；合併視窗裡還沒送出的提示在送出時才組文字，也是新的語言。新的一次性提示在橫幅的鎖裡才依語言組文字（`MonitorEngine.SetNotice`，同全域暫停的橫幅）：審查回報（推測、時間窗極短，沒有重現）原本在鎖外先讀語言，使用者操作的工作回報未預期錯誤時若剛好和換語言交錯，可能在重建之後才存進舊的語言。這一項只讀程式驗證，沒有單元測試（要在讀語言與取得鎖之間插入另一個執行緒，得加測試用的掛鉤）。

使用者關掉的提示不會因為換語言又出現：`BannerInfo` 新增公開的 `NoticeId`（一次性提示的識別碼，用 `BannerInfo.NextNoticeId()` 產生；每次快照重新組的橫幅是 0），Engine 與 App 換語言時用 `with` 只換文字、保留識別碼，主畫面記住關掉的識別碼（沒有識別碼的仍用參考比對）。App 的啟動提示（DB／設定檔復原）改成依語言產生（`AppHost.StartupWarning` 的型別從 `BannerInfo?` 改成 `Func<AppLanguage, BannerInfo>?`），背景錯誤的橫幅依種類重組文字。

沒有即時換的（換語言之後、下一次產生之前維持舊的語言）：

- 正在顯示的提示視窗、訊息條、設定頁的錯誤與檢查連線結果、已經放進剪貼簿的失敗 log：都是當時的結果。
- 上一次執行留下、這次還沒重新判定的 problem_note：DB 只存文字，不知道是哪一種問題，維持寫入時的語言，到這個 repo 下一次成功時清掉或失敗時重寫。
- WPF 內建的輸入框右鍵選單（剪下／複製／貼上，英文 Cut／Copy／Paste）：WPF 第一次用到時依 UI 執行緒的 CurrentUICulture 載入、之後沿用；App 啟動時就先套用設定的語言，所以重新啟動後一定是設定的語言，執行中切換不保證跟著換。這些文字在 WPF 的衛星資源：相依框架版用目標電腦 .NET Desktop Runtime 裡的；自封式版本原本因為 `SatelliteResourceLanguages` 只寫 `en`，一個語系資料夾都沒包，繁體中文介面時是英文（審查回報），改成 `en;zh-Hant` 之後包進 17 個 zh-Hant 衛星組件（見下方「驗證」；第一版不發自封式，見[和設計規格的差異](spec-deviations.md)）。退回系統 MessageBox 時的「確定」按鈕由 Windows 依顯示語言畫，App 改不了。

### 英文的寫法

- 顯示狀態：Queued、In progress、Waiting for approval、Succeeded、Failed（Failed (timed out)、Failed (startup failure)）、Cancelled、Skipped（Skipped (neutral)、Skipped (stale)）、Unknown（Unknown (值)）。
- 開始時間：Just now、5 min ago、Today 08:32、Oct 23 23:50（今年）、Dec 31, 2025（更早）。時間一律 24 小時制（同中文版、摘要列與 api.log），不用 AM/PM；tooltip 的完整時間 `yyyy-MM-dd HH:mm:ss` 兩種語言相同。
- 耗時與 ETA：`45s`、`3m 12s`、`1h 05m` 兩種語言相同；Elapsed 4m 12s · ~3m left、Elapsed 9m 40s · ~3m over estimate、< 1m left；Queued 1m 05s。
- 數量：6 runs／1 run、2 repos can't be updated、Copied the logs of 2 failed jobs. 1 couldn't be fetched.、12 repos visible (3 private)、7 runs changed (2 failed, 3 succeeded, 2 other)、Loading repos (0 of 1 done)（句型不跟數量配合，只有 1 個 repo 時也通）。
- 2026-10-26 審查後修正的寫法（中文不變）：
  - 次級限流的數字是退避等級（第一次限流就是 1，安靜一段時間會降回來），不是連續次數：「Rate limited by GitHub (backoff level 1). Retrying at 10:45; …」，原本是「(1 in a row)」。
  - 分號只用在連接兩個子句：一個 repo 有多個問題時各自成句（`ProblemSeparator` 是「. 」），「Insufficient permissions (requires actions=read)」「No workflows, or Actions is disabled (can't tell which)」「Can't be force included: Archived」項目裡不再用分號。原本「Missing the Pull requests permission, or not accessible; Insufficient permissions; requires actions=read」看起來像 3 個問題。
  - 訊息條上的完整句子一律加句號：This run has no failed job logs to copy.、Copied the log of 1 failed job.、Couldn't get this run's job list (HTTP 502).（同 Run URL copied.）。
  - 同一個東西同一個說法：設定頁「Close to tray」（同 ✕ 的 tooltip）；run 選單的「Notify me」就是設定頁「Notify when a run marked "Notify me" finishes」引用的名稱（單元測試檢查兩種語言的引用都和選單一致）。
  - 報讀與標籤：已標記時 run 選單項目的報讀名稱「Notify me (on)」（原本「…, on」）、置頂中按鈕的 tooltip「Don't keep on top」、repo 類別「Mine」（原本 Own）。
  - 系統匣的原因行縮短：Not signed in、Rate limited, retry 10:45、GitHub errors、App outdated（原本 Sign-in required、Rate limited, retrying at 10:45、GitHub is returning errors、App update required）。NotifyIcon.Text 最多 127 字元，原本限流、5xx 與 12 個無法更新同時出現時尾端被截掉（中文同樣情況 85 字元）。測試殼逐一組合（5 種原因的所有組合 × 無法更新 0／1／12／123 × 進行中與失敗 1／12／123，共 381 種）：被截短的從 214 種減到 120 種，最長從 198 字元變 169；計數 2 位數以內、原因最多 3 個（含無法更新）的 57 種原本有 13 種被截短，現在全部放得下（`EngineTextTests.系統匣tooltip在常見情況下放得下`）。其他組合（例如 4 個以上的原因同時出現）仍照 spec §7.4 先截短原因行，主畫面有完整資訊。中文所有組合都放得下（最長 112）。
- 為了在最小寬度（設定頁、repo 管理頁的 480）不被切掉而縮短的：設定頁的「Ignore GH_TOKEN/GITHUB_TOKEN when running gh」「Notify when a run marked "Notify me" finishes」，repo 管理頁的類別「Org」、取消強制納入「Stop forcing」。原本的寫法在 480 寬時右緣超出可見寬（`607`、`559` 對 `437`，測試殼量的），縮短後是 `399`、`407`。repo 管理頁的這次量測只有組織與自己的 repo，沒有協作 repo、已靜音與已強制納入的列，「480 寬不被切掉」不成立（審查回報）；只靠縮短不夠，改了版面，見下方「repo 管理頁的窄版」。第二階段（2026-10-30）同樣縮短設定頁寫入開關的「Allow re-running and cancelling runs in the app」→「Allow re-running and cancelling runs」：原本 480 寬時核取方塊的內容寬 `406`、只看得到 `403`，結尾被切掉（測試殼量的）。
- Repo、Workflow、Event、Status、Branch、Actor 與 `Pull request #17`、`Commit dcf27ad`、`by alice` 兩種語言相同（spec 原文就是英文）。

英文介面用同一組字型（`FontFamily.Default`）：英文、數字用 Consolas，中文（語言選項「繁體中文」、標題的「語言」、使用者資料裡的中文標題）落到微軟正黑體（2026-10-29 起；之前是內嵌的文楷）。

### 驗證

2026-10-26：

- 單元測試 `1,334` 個全部通過（原本 `1,210` 個，新增 `124` 個）：字串表、各 Core 單元的英文、設定檔的 `language`、Engine 在英文設定下的橫幅／原因行／問題標示／提示／操作結果、換語言後的重建（同一個識別碼、恢復時間不變、合併視窗裡的提示）。原本的測試只改了一個斷言：settings.json 的欄位清單多了 `language`。
  - 審查修正後（同一天）：`1,344` 個全部通過（修正開始時 `1,336` 個；新增 8 個：偷看語言和 Load 一樣把其他欄位型別錯誤的設定檔當成損毀 4 個、設定頁引用的選單名稱和 run 選單一致 1 個、英文的數量與分項寫法 1 個、系統匣 tooltip 在常見情況下放得下兩種語言 2 個；改了英文用語的斷言跟著改）。
  - 2026-10-29 換成微軟正黑體（不內嵌）時，刪掉 3 個檢查內嵌字型授權的測試、新增 2 個「App 專案不得有字型檔」的測試：`1,343` 個全部通過。
- 測試殼（一般的 `Application` 載入 App 的資源字典與視窗、假資料；不建立 App、不啟動 Engine、不建立 NotifyIcon；系統匣選單照 `TrayIconController` 的做法另建一份 ContextMenuStrip）兩種語言各拍：主畫面（含 600 寬）、未登入的空狀態、run 選單、Status 篩選、設定頁（上、下、480 寬）、repo 管理頁（含 480 寬；只有 3 個組織與自己的 repo，沒有涵蓋協作 repo 與兩種按鈕的另一個狀態，見下方「repo 管理頁的窄版」）、提示視窗（單筆、彙總）、錯誤對話框（啟動失敗的訊息）、第三方授權（當時還有）、系統匣選單。掃描畫出的文字段：英文介面 `4,458` 個英文字母與數字全部是 Consolas、`18` 個中文字全部是內嵌的文楷；中文介面 `2,247` 個與 `883` 個，同樣全部符合（都是換字型前的結果；換成微軟正黑體後的掃描見上方「字型的驗證」）。系統匣 tooltip 最長的英文原因行（所有原因加上無法更新）截到 `127` 個字元（NotifyIcon.Text 的上限）。
- 執行中切換：從中文開始，`Loc.Apply(English)` 之後檢查主畫面、repo 管理頁、設定頁共 24 項（XAML 繫結的與 ViewModel 算的），全部換成英文；UI 執行緒的文化換成 `en-US`；關掉的啟動提示與 Engine 的一次性提示（同一個識別碼、換成英文）仍不顯示，新的一則照常顯示；換回中文後全部回到原本的文字。
  - 審查回報：Status 篩選下拉在換語言之前開過一次，換語言後再開，畫面上的選項仍是舊的語言（報讀名稱與 ViewModel 已經換了；上面 24 項讀的是 ViewModel 的文字）。原因是選項的標題範本 `Text="{Binding}"` 只在建立時讀一次 `ToString()`，選單開過一次後項目會沿用。`Style.MenuItem.FilterOption` 改用繫結 `Text` 的標題範本後，測試殼讀畫出來的 TextBlock 重驗：中文 → 英文 → 中文、英文 → 中文 → 英文，Status 與 Repo 下拉畫面、報讀名稱、ViewModel 三者一致；Event 選項的 `pull_request`、`workflow_dispatch` 底線照常畫出來。
- 單一執行檔：`dotnet publish`（同 `tools/publish.ps1` 相依框架版的參數）只輸出一個 `IsItGreenYet.exe`，exe 的位元組裡找得到兩種語言的字串（例如 `Waiting for approval`、`等待核准`，UTF-16）；沒有執行 exe。自封式版本沒有另外發行（字串在 Core.dll 裡，兩版包進去的方式相同）。
- WPF 內建文字的衛星資源（審查後，同一天）：用 `tools/publish.ps1` 的參數（`-c Release -r win-x64`、`PublishSingleFile`、`IncludeNativeLibrariesForSelfExtract`、`DebugType=none`）發行到暫存資料夾，restore 指向空的套件來源、只用本機快取（沒有下載）。`SatelliteResourceLanguages` 從 `en` 改成 `en;zh-Hant`：自封式 `183,949,377 → 185,240,798` bytes（多 `1,291,421`），bundle 裡從沒有任何 `*.resources.dll` 變成 17 個 `zh-Hant/*.resources.dll`（PresentationFramework、PresentationCore、System.Windows.Forms 等，等於 runtime pack 的 zh-Hant 資料夾全部）；相依框架版前後逐位元相同（`29,371,551` bytes）。這兩個大小含這一輪的程式改動，和上方「色票」記的 `tools/publish.ps1` 輸出不是同一次。沒有執行 IsItGreenYet 的 exe（單一實例鎖會通知正在執行的那一份）；另外寫了一個同樣發行成自封式單一執行檔的小程式，UI 文化 `zh-TW` 時讀 WPF 的資源：只寫 `en` 時輸入框選單是「Cu_t／_Copy／_Paste」，`en;zh-Hant` 時是「剪下(_T)／複製(_C)／貼上(_P)」。2026-10-29 換字型後的 `tools/publish.ps1` 輸出：自封式 `159,224,542` bytes，bundle 裡同樣有這 17 個 zh-Hant 衛星組件（第 2 輪複驗讀 bundle 清單確認，和本機 runtime pack 的檔案逐位元相同）。
- 第 2 輪複驗（2026-10-29，收斂式：只確認第 1 輪的修正與回歸，中等以上才修）：三個角度（版面、執行期與核心、英文用字）各一位審查者，讀程式與 diff，並用當天量測殼的紀錄與截圖、`dotnet test`（`1,343` 個通過）、自封式 exe 的 bundle 清單驗證。第 1 輪的發現全部判定已解決：英文 repo 管理頁窄版（medium）、Status 篩選下拉換語言（medium）、自封式缺 zh-Hant 衛星資源（medium）、`HideTimeDetailBelow` 的餘裕、`PeekLanguage` 與 `Load` 一致、一次性提示的語言鎖（只讀程式）、英文用語與標點的 7 項、系統匣 tooltip 長度。沒有中等以上的新問題。記成已知限制的低嚴重度項目：英文的 repo 問題標示最後一則沒有句號，GitHub 403 的訊息本身以句號結尾時可能出現「..」（推測）；設定頁「檢查連線」那一行的英文有的有句號、有的沒有；系統匣 tooltip 在「計數 2 位數、Not updated yet、限流＋5xx＋99 個無法更新」時剛好 `127` 個字元。兩處過時的程式註解（`BannerSecondary` 的參數說明、`SaveProblemNote` 的分隔符號）已更正。

### repo 管理頁的窄版

2026-10-26 審查回報：英文介面的 repo 管理頁在寬度 640 以下，右邊四欄（類別、狀態、靜音、強制納入）把名稱欄擠到幾乎沒有寬度；最小寬度 480 時名稱只剩「o…」、原因一字一行，按鈕被擠出可見範圍。上面「英文的寫法」那一次 480 寬的量測資料沒有協作 repo，所以沒發現。

修正前用測試殼重量（一般的 `Application` 載入 App 的資源字典與正式的 RepoManagerWindow；假資料 9 個 repo：組織、自己的、協作、fork 四種類別，含「取消靜音」「取消強制納入」、很長的原因與兩個問題的組合）：

- 英文、視窗高 900（沒有捲軸），從 720 每 10 DIP 縮到 480：名稱欄 720 時 `270`、600 時 `150`、560 時 `93`；550～500 每一列的「Force include／Stop forcing」右緣超出清單的可見範圍（被切掉）；490 時名稱欄只剩 `37`。
- 中文也會被切掉：預設高度 560（有捲軸）時從 720 一次拉到 480，每一列都排成 `486` 寬，清單可見寬只有 `435`，「強制納入」被切掉；一格一格縮到 480 時不會。原因是清單虛擬化：列變高之後排不進快取範圍的兩列沒有重新量，仍是 720 時量的寬度（`486`、`478`），清單又把每一列都排成最寬那一列的寬度（等了 3 秒也沒有恢復）。

改法（`Views/RepoManagerWindow.xaml` 與 `.xaml.cs`）：

1. 窄版（`RepoManagerWindow.CompactRows`）：名稱欄能用的寬度比 `Size.RepoRow.NameMinWidth`（`160`，Body 字級的 Consolas 20 個字，一個字 `8 DIP`）窄時，每一列的名稱與原因佔滿整列，類別、狀態與兩個按鈕換到第三行、靠右，和上一行、彼此之間隔 `Margin.RepoRow.Compact`（`6,6,0,0`）。四欄的寬度與對齊不變，Tab 的順序不變。
2. 名稱欄能用的寬度只看清單寬度與語言：清單 ScrollViewer 的內側寬度一律扣掉直向捲軸（不看捲軸當下有沒有出現，同主畫面的寬度斷點），再扣掉每一列的內距與外框（`22`）、右邊四欄。四欄的寬度由一列看不見的 `ColumnSizer` 撐到目前語言最寬的標籤（英文 Collaborator、Monitored、Unmute、Force include），不看清單畫出了哪幾列。
   - 先試過用每一列實際的名稱欄寬度判斷：寬版與窄版的列高不同，捲軸會跟著出現或消失，名稱欄寬度跟著變，中文 7 個 repo、視窗寬 550 時同一次排版裡在窄版與寬版之間切了 7 次，最後停在哪一個看事件的次數；而且 SharedSizeGroup 只算畫出來的列，捲動時欄寬也會變。
   - 代價：四欄一律留到最寬的標籤，沒有協作 repo 時類別欄也是 Collaborator 的寬度。好處是捲動時欄寬不再跳。
   - `ColumnSizer` 是 `Visibility="Hidden"`（照常量寬度、不畫、不能點）、高度 0。它的小標在報讀樹只出現在原始檢視：測試殼查 AutomationPeer，`IsControlElement`、`IsContentElement` 都是 false、`IsOffscreen` 是 true（清單裡看得見的小標是 true、true、false）。
3. 每一列最寬是清單的可見寬度（`RepoManagerWindow.RowMaxWidth`，清單的 `ScrollChanged` 回報可見寬度改變時更新）：沒有重新量的列也照新的寬度重新量。

結果（2026-10-26，同一套測試殼）：

- 6 組資料（全部、只有組織與自己的、加一個協作、沒有協作、沒有協作也沒有已靜音，再加視窗高 900 的全部）、兩種語言，每一組從 720 每 10 DIP 縮到 480、再放回 720，最後從 720 一次拉到 480：每一個寬度、每一列的類別、狀態與兩個按鈕都在清單可見範圍內（右緣離可見範圍的右緣 `11`，就是列的內距加外框）；放回時窄版與否和縮窄時相同；排版完成後再等 300 ms，接下來 500 ms 裡窄版與列寬都沒有再變（只有一個寬度在閒置時又排了 8 次版，當時滑鼠正在移動，是列的滑過效果）。
- 切換點和資料、視窗高度無關：英文 630 以上是寬版（名稱欄 `163`，捲軸出現時）、620 以下窄版；中文 540 以上寬版（`161`）、530 以下窄版。窄版的名稱在 480 寬時可用 `413`，9 個 repo 只有 55 個字的那一個截斷（tooltip 全名）；寬版在切換點的名稱欄放得下 `octo-org/sample-app`（`152`）。
- 開著時換語言：中文 480 換英文（窄版 → 窄版）、英文 600 換中文（窄版 → 寬版）、中文 600 換英文（寬版 → 窄版），換回原本的語言時回到原本的版面，都沒有被切掉。
- 頂端的數量文字（例如「9 repos · 4 monitored · 5 skipped」）沒有改，仍是截斷加 tooltip 全文：英文 590 以下、中文 530 以下會截斷。

## 補充控制項樣式

`Design.xaml` 只涵蓋 SAM 主畫面需要的控制項；本專案在 `Design.xaml` 改了 `FontFamily.Default`（見「色票」下方的字型），新增 `FontWeight.Emphasis`（`575`）並把 SAM 樣式裡的 `SemiBold` 都換成它（同上），拿掉 `Radius.Chip`（`999`），新增膠囊的 `Converter.CapsuleRadius`、`Style.Border.Capsule`、`Style.Rectangle.Capsule`（見下方「膠囊」）。`src/IsItGreenYet.App/Themes/Controls.xaml` 的前半段（檔內註解「以下是 IsItGreenYet 新增的樣式」之前）是自 SAM 複製的補充樣式，其中有用到的是 `Style.TextBox`（主畫面搜尋框，也是 `Style.TextBox.Input` 的基礎）與 `Style.CheckBox`（勾的幾何往左上移 `0.5`，在 `20 × 20` 的框裡置中）；前半段另外含 Danger 的 hover／pressed 兩個寫死的色值（`Brush.Danger.Hover` `#FF9694`、`Brush.Danger.Pressed` `#E56260`，不在上面的色票表裡），第二階段起由確認對話框取消類按鈕的 `Style.Button.Danger`（以 `Style.Button.Danger.Subtle` 為基礎）使用，[和設計規格的差異](spec-deviations.md)有記（第二階段 spec D12）。本專案新增的樣式接在後面，顏色一律引用上面的色票 token（spec §16.1）；圓角另外新增 `Radius.None`（0）、`Radius.RadioButton`（10）與 `Radius.Control`（4，見下方「彈出畫面」），間距與尺寸的新 token（`Margin.*`、`Padding.*`、`Size.*` 等）也定義在 `Controls.xaml` 的新增區段，和 spec §16.1「只引用既有的 token」不同（[和設計規格的差異](spec-deviations.md)有記）。下表只列本專案新增、實際有用到的：

| 樣式 | 用途 |
|---|---|
| `Style.Window.Resizable` | 主視窗：以 `Style.Window.Main` 為基礎，`ResizeMode=CanResize`，WindowChrome 的 `ResizeBorderThickness` 為 `6 DIP`；尺寸取自 Core 的 `Tuning` |
| `Style.Window.Dialog.Resizable` | repo 管理頁、設定頁：預設 `720 × 560`、最小 `480 × 360` |
| `Style.Path.StatusIcon` | 清單列狀態圖示（`Controls/StatusIcon.xaml`） |
| `Style.Button.Chip.Branch` | 分支 chip（以 `Style.Button.Chip` 為基礎） |
| `Style.ProgressBar.Thin` | ETA 細進度條（`Tag="Overrun"` 時改用 Warning） |
| `Style.Banner` | 橫幅（`Tag` 是 `Warning` 或 `Danger`，決定框線色） |
| `Style.ToggleButton.Filter` | 篩選列的 6 個下拉按鈕，以及摘要列的「自動更新」下拉按鈕 |
| `Style.ContextMenu`、`Style.MenuItem` | 下拉按下後展開的選單（篩選選項、自動更新選項），以及 run 的右鍵／⋯ 選單：外框方角、內距 `Padding.Menu`，圓角與陰影交給 DWM；項目圓角 `Radius.Control`。項目面板是 `Themes/MenuItemsPanel.cs`：選單裡沒有任何可見項目會顯示勾時，收掉左邊的勾選欄（見下方「彈出畫面」）。項目有快速鍵文字（`InputGestureText`）時顯示在右邊，Text Muted、`FontSize.Caption`。run 選單第二階段加了分隔線，項目改用 `Themes/MenuItemStyleSelector.cs` 套 `Style.MenuItem`（同下一列，分隔線不套） |
| `Style.ContextMenu.Builtin`、`Style.MenuItem.Builtin`、`ContextMenu` 的隱含樣式 | WPF 內建的右鍵選單（輸入框的剪下／複製／貼上、捲軸的捲動選單）：外觀同 `Style.ContextMenu`；項目標題處理存取鍵（例如「剪下(T)」的 T 加底線）。項目用 `Themes/MenuItemStyleSelector.cs` 套樣式，分隔線不套（直接設 `ItemContainerStyle` 會被套到分隔線上而丟例外） |
| 選單分隔線（鍵是 `MenuItem.SeparatorStyleKey`） | Surface Hover 的 1 px 線，上下各隔 `Margin.Menu.Separator` 的 4（同系統匣選單的分隔線）。用在捲軸的內建選單，以及寫入功能開啟時 run 選單寫入項目前的那一條（第二階段 spec §9.1；寫入功能關閉時不顯示） |
| `ToolTip` 的隱含樣式 | 所有滑鼠停留提示：Surface Hover 底、Border 框線、Text Primary、`FontSize.Caption`、內距 `Padding.ToolTip`，小圓角與陰影交給 DWM |
| `Style.Button.Accent` | 設定頁的「儲存」、錯誤對話框的「確定」、確認對話框重跑類的執行按鈕（取消類用前半段的 `Style.Button.Danger`）：以 `Style.Button.Primary` 為基礎（Accent 底，hover、pressed、停用都同 Primary），尺寸、內距、字級改成同 `Style.Button.Secondary`。鍵盤焦點框改用 `Style.FocusVisual.Button`，範本另寫一份、只少了 Primary 的 `IsKeyboardFocused` 觸發 |
| `Style.FocusVisual.Link` | 設定頁最下方文字連結的鍵盤焦點框：`Style.Hyperlink.Repo` 把焦點框設成 null，在 XAML 蓋回來。顏色、粗細和什麼時候畫同 `Style.FocusVisual.Button`，圓角 `Radius.Control`，左右往外 `Margin.FocusVisual.Link` 的 `4`（焦點框的範圍就是文字的範圍，不往外就壓在頭尾的字上）；捲動區會裁掉超出左邊界的部分，所以連結左邊留 `Margin.Gap.Left` |
| `Style.FocusVisual.Button` | `Style.Button.Accent` 的鍵盤焦點框：2 px 的 Text Primary、圓角 `Radius.Button`，和 Primary 鍵盤焦點時的框一樣；WPF 只在最近一次輸入是鍵盤（或系統設定一律顯示焦點框）時才畫，點滑鼠或程式設定初始焦點時不畫 |
| `Style.TextBox.Message` | 錯誤對話框、確認對話框（`Views/ConfirmDialog.xaml`，外框同錯誤對話框，第二階段 spec §9.2）與授權與第三方聲明（`Views/NoticesDialog.xaml`）的訊息：唯讀、可以選取，看起來是一般文字，超過最大高度時用細捲軸捲動；不是 Tab 停駐點，鍵盤捲動與 `Ctrl+C` 由對話框處理（`Views/DialogMessageKeys.cs`，見下方「彈出畫面」的錯誤對話框） |
| `Style.Border.Badge`、`Style.Text.Badge` | 「第 N 次嘗試」小標；repo 管理頁的類別與狀態標示 |
| `Style.Hyperlink.Repo`、`Style.Hyperlink.Inline` | 清單第二行的 repo 名稱連結（粗體 `FontWeight.Emphasis`、Accent），與 PR 編號／commit 連結 |
| `Style.Text.Placeholder` | 搜尋框沒有文字時的提示字（主畫面、repo 管理頁） |
| `Style.Border.Snackbar` | 主畫面的訊息條 |
| `Style.Button.ToastLine` | 提示視窗裡可點的一行 |
| `Style.TextBox.Input` | 設定頁的數字欄位、repo 管理頁的搜尋框（外觀同 `Style.TextBox`，內距只算一次） |
| `Style.PasswordBox` | 設定頁的 PAT 輸入框 |
| `Style.RadioButton` | 設定頁的認證方式 |
| `Style.Path.Icon`、`Style.Path.Icon.Filled` | 線條圖示（標題列四顆、橫幅與對話框的 ✕、鈴鐺）與實心的 ⋯：固定 `16 × 16`，描邊或填色跟著按鈕的前景色 |
| `Style.Border.Capsule`、`Style.Rectangle.Capsule`（在 `Design.xaml`） | 膠囊外框與膠囊焦點虛線框 |

另外新增的線條圖示幾何：`Geometry.Repos`（標題列的 repo 管理）、`Geometry.Bell`（列上的鈴鐺）、`Geometry.More`（⋯）、`Geometry.MenuCheck`（選單的勾號）、`Geometry.Chevron.Down`（下拉按鈕的箭頭）。狀態圖示的圓與叉（`Geometry.Status.Circle`、`Geometry.Status.Cross`）錯誤對話框也用，從 `Controls/StatusIcon.xaml` 搬到 `Controls.xaml`；其他狀態形狀留在 `StatusIcon.xaml`。

### 彈出畫面（Windows 11 風格）

2026-10-26 依專案作者要求評估程式的彈出畫面、美化成接近原生 Windows 11 的風格，先盤點再改。做法只有一招：外框一律方角加 1 px 框線，圓角、原生陰影與框線色交給 DWM（`Interop/DwmFrame.cs` 呼叫 `DwmSetWindowAttribute`：`DWMWA_WINDOW_CORNER_PREFERENCE`、`DWMWA_BORDER_COLOR`，另外設深色框架），顏色只用既有 token，不新增顏色。

- 為什麼範本是方角：分層（`AllowsTransparency`）的 popup 疊上 DWM 圓角時，WPF 自己的圓角會在邊角多出一圈框；方角框線和 DWM 的框線同色、疊在同一圈像素上，放大比對看不出差別。DWM 不支援這些屬性時（例如 Windows 10）回傳錯誤碼、不丟例外，畫面就是方角加框線。
- 圓角：選單、提示視窗、錯誤對話框用 `Round`，滑鼠停留提示用 `RoundSmall`。2026-10-26 在 Windows 11（build 26300、100% 縮放）從截圖頂邊三列的背景像素數反推分別約 `8 px`、`4 px`；DWM 只收「圓角／小圓角」這種偏好，沒有數值可設，所以沒有對應的圓角 token。
- 彈出層裡的控制項圓角用 `Radius.Control`（`4`，Windows 11 控制項的圓角）：選單項目、系統匣選單的選取底色。主畫面的 `Radius.Button`（`12`）、`Radius.Card`（`14`）與膠囊不變；錯誤對話框的「確定」沿用 App 按鈕的 `12`，和設定頁的按鈕一致（見下方「沒有改的」）。
- 選單的勾選欄：Windows 11 的選單只有在有項目可以勾選時才在文字左邊留勾選欄。選單的項目面板（`Themes/MenuItemsPanel.cs`）量測時看可見的項目，沒有任何一項會顯示勾（可勾選、已勾選，或 `IsChecked` 有綁定）就收掉勾選欄，文字離選單外緣 `16`（外框 1＋`Padding.Menu` 4＋項目框線 1＋內距 10）；有的話保留 `22` 寬的勾選欄，文字離外緣 `38`。完成的 run 的選單收掉；進行中的 run（有「這筆完成時通知我」）、篩選與自動更新下拉保留。在量測時決定而不是在選單的 `Opened` 事件：popup 建立視窗時就同步畫出第一個畫面，`Opened` 在那之後才觸發（2026-10-26 測試殼攔 `CompositionTarget.Rendering` 確認）。
- 鍵盤焦點框：錯誤對話框一開就把焦點給「確定」（Enter、Space 與報讀軟體都靠它），原本 `Style.Button.Primary` 的 `IsKeyboardFocused` 觸發因此一開就畫 2 px 的白框；Windows 11 只在用鍵盤操作後才畫焦點框。`Style.Button.Accent` 改用 `Style.FocusVisual.Button`（WPF 只在最近一次輸入是鍵盤時畫），設定頁的「儲存」也一樣。主畫面其他按鈕的焦點框沒有改。
- 新 token：`Radius.Control`（4）、`Padding.Menu`（4，選單外框與項目的距離，系統匣選單也讀這個值）、`Padding.ToolTip`（8,5）、`Margin.MenuItem.Gesture`（20,0,0,0，快速鍵文字和標題的距離）、`Margin.Menu.Separator`（0,4）、`Size.Dialog.Width`（480）、`Size.Dialog.MessageMaxHeight`（320）、`Size.Dialog.Icon`（20）、`Padding.Dialog`（24）、`Margin.Dialog.Icon`（0,0,12,0）、`Thickness.Border.Top`（0,1,0,0，選單分隔線與對話框按鈕列的上緣）。`Size.MenuItem.CheckColumn`（22）從 `GridLength` 改成數字。數值的理由寫在 `Controls.xaml` 各 token 上方。（第三方授權對話框用的 `Size.Dialog.License.*` 與 `Margin.FocusVisual.Link`，2026-10-29 隨對話框一起移除。）

| 彈出畫面 | 改了什麼 |
|---|---|
| 系統匣右鍵選單 | 原本是 WinForms 預設的淺色 Office 外觀（白底、淺灰漸層圖示欄、淺藍方框選取）。改成深色，見上方「提示視窗與系統匣」；選單建立 HWND 時設 DWM 圓角、陰影與 Border 框線色 |
| run 的右鍵／⋯ 選單、篩選下拉、「自動更新」下拉 | 外框圓角原本是 `12`、沒有陰影，改成 DWM 的圓角與陰影；項目圓角 `12 → 4`，外框內距 `6 → 4`（`Padding.Menu`）。項目高度不變。沒有項目可以勾選時收掉勾選欄（見上方） |
| 輸入框（主畫面與 repo 管理頁的搜尋框、設定頁的數字欄位與 PAT、錯誤對話框的訊息）的「剪下／複製／貼上」、捲軸的「捲動這裡…」 | 原本是系統主題的淺色選單（`#F5F5F5` 底、方角、沒有 DWM 圓角）。改成和 run 選單一樣的深色選單：標題的存取鍵照常（例如「剪下(T)」），右邊顯示快速鍵（`Ctrl+X` 等），沒有勾選欄，捲軸選單的分隔線是深色。這些選單是 WPF 自己建立的：捲軸的是一般的 ContextMenu，`Controls.xaml` 的隱含樣式就套得上；輸入框的是 WPF 內部的子類別，隱含樣式只認完全相同的型別，`Themes/BuiltinMenus.cs` 啟動時用它的型別再登記一次同一個樣式。兩者都在選單建立時套上，popup 的第一個畫面就是深色。開不開、開在哪裡（唯讀時只在選取範圍上才開、Shift+F10 開在插入點旁）仍照 WPF 原本的做法 |
| 滑鼠停留提示（ToolTip） | 原本是系統預設的淺色底、灰框、方角。改成深色（見上方「補充控制項樣式」） |
| 提示視窗 | 見上方「提示視窗與系統匣」：DWM 外框、標頭、顯示順序。標頭的 App 圖示有報讀名稱「IsItGreenYet 圖示」（英文 IsItGreenYet icon） |
| 錯誤對話框 | 原本是系統 `MessageBox`（淺色、Windows 7 式的按鈕與圖示）。改成 `Views/ErrorDialog.xaml`，仿 Windows 11 的 ContentDialog：內容區 Surface 底、錯誤圖示（清單列失敗圖示的形狀放大到 20）加標題「IsItGreenYet」、可以選取複製的訊息；按鈕列另一層 Window Background 底、上緣 Surface Hover 分隔線，「確定」用強調色、放在右半邊，Enter 與 Esc 都能關；`Ctrl+C` 複製標題加整段訊息，同系統 MessageBox，只有焦點在訊息、又有選取文字時才只複製選取的部分（訊息失去焦點後選取範圍看不到，這時也複製整段）；訊息不是 Tab 停駐點（Tab 只在按鈕之間移動，同系統 MessageBox）；訊息超過最大高度時，焦點不論在「確定」還是訊息，`↑`／`↓` 捲一行、`PageUp`／`PageDown` 捲一頁、`Home`／`End`（含 `Ctrl`）到頭尾（對話框的 `PreviewKeyDown` 處理，焦點不移動、不出現多的焦點框；訊息的插入點不顯示，交給訊息自己處理時 `↑`／`↓` 只移動看不到的插入點、到邊緣才捲，所以焦點在訊息時也由對話框處理），加 `Shift` 的鍵照常交給訊息延伸選取，訊息不需要捲動時這些鍵照常交給焦點所在的元素；「確定」的焦點框只在用鍵盤操作後出現（見上方）；播系統的錯誤音效。有使用中的視窗（沒有就用看得到的主視窗）時以它為 owner、置中在它上面；都沒有時（啟動失敗、收在系統匣）在游標所在的螢幕置中並出現在工作列。對話框建立或顯示失敗、或對話框開著時又來一個錯誤，退回系統 `MessageBox`（`App.ShowError`） |
| 設定頁的「儲存」 | 原本和「取消」一樣是次要按鈕。改用 `Style.Button.Accent`（Windows 11 對話框的主要動作用強調色）；驗證 PAT 期間停用時是 Surface Hover 底、Text Muted 字 |
| 第三方授權（2026-10-26 新增，2026-10-29 移除） | 內嵌文楷時，為了 OFL 第 2 條在設定頁加了「授權」卡片與第三方授權對話框（`Views/LicenseDialog.xaml`）。改用 Windows 內建的微軟正黑體、不再散布字型後，對話框、卡片、連結樣式與相關字串一起移除，原本的做法見合併這些變更的 PR 的 commit 歷史 |
| 授權與第三方聲明（2026-11-05 新增） | 設定頁最下方的入口開 `Views/NoticesDialog.xaml`：外框用 `Style.Window.Dialog.Resizable`（可以縮放，同設定頁），內文用 `Style.TextBox.Message`、字級 `FontSize.Caption`，按鍵用 `Views/DialogMessageKeys.cs`；「關閉」用 `Style.Button.Accent`，同錯誤對話框的 `IsDefault`＋`IsCancel`。內容來自 Core 的 `EmbeddedNotices`，沒有翻譯，英文介面看到的聲明也是繁體中文 |

沒有改的：

- 主視窗、設定頁、repo 管理頁的外框：改成不透明視窗加 DWM 圓角會影響縮放、最大化、拖曳與每秒更新的畫面，也牽涉 spec Q16 的既有決定（先用透明圓角，效能不好才改），要先問專案作者。
- WPF 內建的 Fluent 主題（`Application.ThemeMode`）：.NET 10 仍標 `[Experimental("WPF0001")]`，會換掉所有預設範本、和 SAM 的色票與自訂樣式衝突，也改不到系統匣選單與系統 MessageBox。
- 加 `app.manifest` 讓系統 MessageBox 套上 Windows 11 的按鈕與圖示：仍是淺色；錯誤對話框改用自己的視窗後，MessageBox 只剩退路會用到。
- 系統匣圖示的 tooltip、檔案總管、瀏覽器：由 Windows 殼層或其他程式繪製，App 改不了。
- 主畫面的訊息條：本來就是深色。
- 錯誤對話框加「開啟資料夾」按鈕：是新增的行為、spec 沒有，要做先問專案作者。
- 錯誤對話框「確定」的尺寸：維持 App 按鈕的圓角 `12`、高 `36`，和設定頁的按鈕一致；Windows 11 對話框的按鈕是圓角 `4`、高 `32`。要不要改由專案作者決定。

2026-10-26 用測試殼（正式的 MainWindow、TrayIconController、ToastWindow、App.ShowError、SettingsWindow 加假資料，不連網）在 Windows 11 build 26300、100% 縮放驗證：選單、ToolTip、系統匣選單、提示視窗、錯誤對話框的頂邊中間是 token 的框線色、左上角是 DWM 圓角、底邊下方有陰影（墊灰底截圖量像素）；系統匣選單的項目高、上下距離、選取底色的內縮與圓角、停用文字色，鍵盤 ↓↑ 與 Enter、Esc、點外面（本程式的另一個視窗）關閉、暫停／恢復的文字、7 個項目的動作（按鍵與點擊用 PostMessage 送給測試殼自己的視窗，沒有動真的滑鼠與鍵盤）；提示視窗顯示當下的位置、換語氣時的框線色、✕ 與圖示對稱、點擊；錯誤對話框的 owner 與置中、長訊息捲動、Enter／Esc 的登記、退回 MessageBox 的兩種情況。沒有驗到的（真的滑鼠點系統匣、選單的鍵盤焦點框、150% 與 Windows 10）列在[手動測試清單](testing/manual-checklist.md)的 M42。

同一天審查後的修正，用同一套測試殼驗證（這時螢幕保護程式在跑、拍不到螢幕，截圖改用 PrintWindow 與 WPF 自己畫的點陣圖，只看得到視窗自己的內容，沒有 DWM 的圓角與陰影）：系統匣選單文字上下留白（見上方「提示視窗與系統匣」）；run 選單完成時文字離外緣 `16`、進行中 `38`，篩選與自動更新 `38`，而且 popup 畫的第一個畫面就是這個位置；輸入框（搜尋框、PAT、數字欄位、對話框訊息）與捲軸（清單、對話框訊息）的內建選單第一個畫面就是深色、勾選欄收掉、存取鍵與快速鍵、深色分隔線、DWM 圓角偏好已設；錯誤對話框開啟時「確定」沒有焦點框、把最近的輸入設成鍵盤再按 Tab 才出現 2 px 的 Text Primary 框，訊息不是 Tab 停駐點；提示視窗的 App 圖示在 UI Automation 裡名稱是「IsItGreenYet 圖示」。對話框 `Ctrl+C` 的判斷只讀過程式（測試殼不能送 Ctrl、也不寫剪貼簿），列在 M42。

上一段的「訊息不是 Tab 停駐點」讓只用鍵盤的人捲不到超出最大高度的部分（審查回報的回歸），改成在對話框層級用鍵盤捲動（見上表的錯誤對話框）。2026-10-26 用新的測試殼（一般的 `Application` 載入 App 的資源字典與視窗，不建立 App、不啟動 Engine）驗證，長訊息（可見高 `320`、內容 `1479`）：開啟時鍵盤焦點在「確定」、「確定」與訊息都沒有焦點框；焦點在「確定」時 `↓` 捲 `16`（一行）、`PageDown` 捲 `320`（一頁）、`↑`、`PageUp`、`End`（最後一行在可見區內）、`Home` 都照預期，焦點不移動；焦點在訊息時同樣的鍵照樣捲動、插入點不動；`←`、單按 `C` 不被當成捲動鍵；Tab 的行為不變（訊息按 Tab 到「確定」、再按仍在「確定」，這時才出現焦點框）；短訊息不需要捲動時 `↓`、`PageDown`、`End` 不被對話框處理。同一套檢查在修正前有 10 項失敗。`Ctrl+Home`／`Ctrl+End` 與 `Shift` 加方向鍵（測試殼送不出修飾鍵）要在實機確認；實機要有長到出現捲軸的訊息，目前只有 UI 執行緒的未預期例外會產生（沒有不改程式的觸發方法），所以這兩組鍵列在[手動測試清單](testing/manual-checklist.md)後面「沒有列成步驟」的項目。

### 圖示置中

線條圖示都畫在 `16 × 16` 座標系、以 `(8,8)` 為中心，`Path` 一律用 `Style.Path.Icon` 固定成 `16 × 16`（`Size.Icon`）。`Stretch="None"` 的 `Path` 版面大小只算到幾何的右下邊界、不算左上的留白，放進按鈕置中後整個圖示會偏右下；2026-10-26 量測（100% 縮放，滑過時的框＝整顆按鈕）修正前標題列 ✕ 的左右、上下邊距都差 `4 px`，⋯ 上下差 `8 px`。

- 上下不對稱的幾何微調成以 `(8,8)` 為中心：置頂的直線下端 `13.5 → 13`（`App.xaml`）、repo 管理的書上緣 `2.5 → 2`、鈴鐺整個上移 `0.5`。
- 列上的 ⋯ 按鈕只有 `28 × 28`：內距歸零，`16 × 16` 的圖示才放得下。
- 下拉箭頭固定 `10 × 6`（`Size.Chevron.Width`、`Size.Chevron.Height`），墨水正好在中間。
- 修正後用測試殼（假資料）量：標題列四顆、橫幅 ✕、⋯、設定頁與 repo 管理頁的 ✕、提示 ✕、核取方塊的勾，幾何的邊距差都是 `0`；整個視窗畫成 100% 縮放的點陣後量筆畫邊界，邊距差都在 `0.17 px` 以內。

### 膠囊

chip（分支、清除篩選、repo 管理頁的靜音與強制納入）、篩選下拉與「自動更新」下拉、「第 N 次嘗試」與 repo 管理頁的小標、細進度條，都是兩端半圓、中間直線的膠囊。圓角不能寫死一個很大的值（原本的 `Radius.Chip` 是 `999`）：`Border` 的圓角超過邊長時會依比例縮小，寬 `124`、高 `28` 的「自動更新」下拉變成水平半徑 `62`、垂直半徑 `14` 的橢圓；`Rectangle` 的 `RadiusX`／`RadiusY` 各自限制在寬、高的一半，焦點虛線框也是橢圓。現在圓角綁到元素自己的寬高（`Themes/CapsuleRadiusConverter.cs`：較短那一邊的一半），細進度條（高 `4`，原本圓角 `3`）也一起改成膠囊。2026-10-26 用測試殼量：各元素的實際圓角都等於高的一半，頂邊中段是直線（例如「自動更新」下拉 `98 px`，修正前 `20 px`）。

`Controls.xaml` 裡還有下面這些定義了、程式卻沒有用到的資源（在 `src/IsItGreenYet.App` 的所有 .xaml／.cs 搜尋，除了定義處、註解，以及同一批沒用到的資源之間的互相引用，沒有其他地方引用）：

- 本專案新增的 `Style.Border.Dropdown`（原本預定給篩選下拉展開後的面板）：篩選下拉實作成 `ToggleButton` 加 `ContextMenu`，用的是上表的 `Style.ContextMenu`／`Style.MenuItem`。
- 前半段自 SAM 複製的 3 個樣式：`Style.Window.Dialog`、`Style.ListBoxItem.Record.Compact`、`Style.Bar`。

要不要刪掉這些由專案作者決定。
