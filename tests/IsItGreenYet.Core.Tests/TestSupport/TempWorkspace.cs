using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.TestSupport;

/// <summary>每個測試都在自己的暫存資料夾裡跑，彼此完全隔離（同 SAM）。</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "iigy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public string PathTo(string fileName) => Path.Combine(Root, fileName);

    public void Dispose()
    {
        // SQLite 的連線池會抓住檔案握柄，不釋放就刪不掉。
        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
