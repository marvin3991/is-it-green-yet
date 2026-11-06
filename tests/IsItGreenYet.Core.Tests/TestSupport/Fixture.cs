namespace IsItGreenYet.Core.Tests.TestSupport;

/// <summary>讀取 Fixtures 資料夾的手寫假資料（不得含真實 token 或私人 repo 內容）。</summary>
public static class Fixture
{
    /// <summary>測試用的假 token，格式像 classic PAT，供遮罩測試使用。</summary>
    public const string FakeToken = "ghp_TESTTOKEN000000000000000000000000000000";

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
