using System.Globalization;

namespace IsItGreenYet.Core.Tests.Repository;

/// <summary>
/// 主清單的寬度斷點（spec §10.10、附錄 B；值在 <see cref="Tuning"/>）：隱藏順序固定是觸發者 → 分支 chip → 時間細節，
/// spec 附錄 B 記的值要和程式一致（改了 Tuning 卻忘了改 spec 時，這裡會失敗）。
/// 測試專案不參考 WPF 的 App 專案，所以讀 repo 裡的 spec 原始檔（同 ThirdPartyNoticesTests 找 repo 根目錄的做法）。
/// </summary>
public class WidthBreakpointTests
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string SpecFile = "docs/specs/2026-10-24-design.md";

    /// <summary>附錄 B 寬度斷點那一列的開頭（表格第一欄是三個常數的名稱）。</summary>
    private const string SpecRowPrefix = "| `HideActorBelow` / `HideBranchBelow` / `HideTimeDetailBelow` |";

    [Fact]
    public void 寬度斷點依觸發者_分支chip_時間細節的順序由寬到窄()
    {
        Assert.True(Tuning.HideActorBelow > Tuning.HideBranchBelow, "觸發者要比分支 chip 先隱藏");
        Assert.True(Tuning.HideBranchBelow > Tuning.HideTimeDetailBelow, "分支 chip 要比時間細節先隱藏");
        Assert.True(Tuning.HideTimeDetailBelow > 0, "時間細節的斷點要是正數");
    }

    [Fact]
    public void spec附錄B的寬度斷點和Tuning一致()
    {
        var row = File.ReadLines(Path.Combine(RepoRoot(), SpecFile))
            .SingleOrDefault(line => line.StartsWith(SpecRowPrefix, StringComparison.Ordinal));

        Assert.NotNull(row);
        var expected = string.Create(
            CultureInfo.InvariantCulture,
            $"| {Tuning.HideActorBelow} / {Tuning.HideBranchBelow} / {Tuning.HideTimeDetailBelow} DIP");
        Assert.Contains(expected, row, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepoMarker)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"從 {AppContext.BaseDirectory} 往上找不到 {RepoMarker}。");
    }
}
