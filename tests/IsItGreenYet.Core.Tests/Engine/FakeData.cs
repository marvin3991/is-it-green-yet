using System.Globalization;
using System.Text.Json.Nodes;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>手寫的假 API 資料（假帳號、假 repo、假 sha），欄位名稱照 GitHub REST 的 snake_case。</summary>
internal static class FakeData
{
    public const long MyId = 1001;
    public const string MyLogin = "alice";

    public const long OrgId = 3001;
    public const string OrgLogin = "octo-org";

    public const long RepoId = 2001;
    public const string RepoName = "octo-org/sample-app";

    public const long WorkflowId = 161335;
    public const string WorkflowName = "CI";
    public const string WorkflowPath = ".github/workflows/ci.yml";

    public const string MainSha = "dcf27ad0c1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6";
    public const string FeatureSha = "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d";

    public static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static JsonObject User(long id = MyId, string login = MyLogin) => new()
    {
        ["login"] = login,
        ["id"] = id,
        ["type"] = "User",
    };

    /// <param name="push">有值時輸出 <c>permissions</c> 物件（第二階段 spec §4.1）；null 時不輸出，和第一階段的假資料相同。</param>
    public static JsonObject Repo(
        long id,
        string fullName,
        DateTimeOffset? pushedAt,
        long ownerId = OrgId,
        string ownerType = "Organization",
        bool fork = false,
        bool archived = false,
        bool isPrivate = false,
        string defaultBranch = "main",
        bool? push = null)
    {
        var owner = fullName.Split('/')[0];
        var name = fullName.Split('/')[1];
        var repo = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["full_name"] = fullName,
            ["owner"] = new JsonObject { ["login"] = owner, ["id"] = ownerId, ["type"] = ownerType },
            ["private"] = isPrivate,
            ["visibility"] = isPrivate ? "private" : "public",
            ["fork"] = fork,
            ["archived"] = archived,
            ["disabled"] = false,
            ["pushed_at"] = pushedAt is { } pushed ? Time(pushed) : null,
            ["default_branch"] = defaultBranch,
            ["html_url"] = "https://github.com/" + fullName,
        };

        if (push is { } canPush)
        {
            repo["permissions"] = new JsonObject { ["admin"] = false, ["push"] = canPush, ["pull"] = true };
        }

        return repo;
    }

    public static JsonObject Workflow(long id = WorkflowId, string name = WorkflowName, string path = WorkflowPath, string state = "active",
        string repo = RepoName) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["path"] = path,
        ["state"] = state,
        ["html_url"] = $"https://github.com/{repo}/blob/main/{path}",
    };

    public static JsonObject Run(
        long id,
        int number,
        string status,
        string? conclusion,
        DateTimeOffset created,
        DateTimeOffset? updated = null,
        string branch = "main",
        string sha = MainSha,
        string eventName = "push",
        long workflowId = WorkflowId,
        string? name = WorkflowName,
        long headRepoId = RepoId,
        string repo = RepoName,
        long actorId = MyId,
        string actorLogin = MyLogin,
        int attempt = 1,
        int[]? pullRequests = null,
        string title = "Fix login button")
    {
        var pulls = new JsonArray();
        foreach (var number2 in pullRequests ?? [])
        {
            pulls.Add(new JsonObject { ["number"] = number2 });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["display_title"] = title,
            ["run_number"] = number,
            ["run_attempt"] = attempt,
            ["event"] = eventName,
            ["status"] = status,
            ["conclusion"] = conclusion,
            ["workflow_id"] = workflowId,
            ["head_branch"] = branch,
            ["head_sha"] = sha,
            ["path"] = WorkflowPath,
            ["actor"] = new JsonObject { ["login"] = actorLogin, ["id"] = actorId, ["type"] = "User" },
            ["triggering_actor"] = new JsonObject { ["login"] = actorLogin, ["id"] = actorId, ["type"] = "User" },
            ["run_started_at"] = Time(created),
            ["created_at"] = Time(created),
            ["updated_at"] = Time(updated ?? created),
            ["html_url"] = $"https://github.com/{repo}/actions/runs/{id}",
            ["pull_requests"] = pulls,
            ["head_repository"] = new JsonObject
            {
                ["id"] = headRepoId,
                ["full_name"] = repo,
                ["html_url"] = "https://github.com/" + repo,
            },
        };
    }

    /// <summary>把 run 改成完成狀態（status、conclusion、updated_at）。</summary>
    public static void Complete(JsonObject run, string conclusion, DateTimeOffset updated)
    {
        run["status"] = "completed";
        run["conclusion"] = conclusion;
        run["updated_at"] = Time(updated);
    }

    public static JsonObject Pull(int number, string headRef, string headSha, long headRepoId = RepoId, string repo = RepoName) => new()
    {
        ["number"] = number,
        ["html_url"] = $"https://github.com/{repo}/pull/{number}",
        ["head"] = new JsonObject
        {
            ["ref"] = headRef,
            ["sha"] = headSha,
            ["repo"] = new JsonObject { ["id"] = headRepoId, ["full_name"] = repo, ["html_url"] = "https://github.com/" + repo },
        },
        ["user"] = new JsonObject { ["login"] = MyLogin, ["id"] = MyId, ["type"] = "User" },
    };

    public static JsonObject Job(long id, string name, string? conclusion, string status = "completed") => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["status"] = status,
        ["conclusion"] = conclusion,
        ["started_at"] = "2026-10-24T01:50:00Z",
        ["completed_at"] = status == "completed" ? "2026-10-24T01:55:00Z" : null,
        ["html_url"] = $"https://github.com/{RepoName}/actions/runs/9001/job/{id}",
    };

    public static string[] LogLines(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"{prefix} line {i}")).ToArray();
}
