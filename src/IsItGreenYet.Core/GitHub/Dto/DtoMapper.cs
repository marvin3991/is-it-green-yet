using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>
/// DTO → Models。spec §4.2 標明可為 null 的欄位缺值時照規則補（例如 run_attempt 缺值當 1）；
/// 其他用到的欄位缺值或格式不對時，這一筆回 <see langword="null"/>，由呼叫端決定略過這一筆或整份回應不採用。
/// </summary>
internal static class DtoMapper
{
    /// <summary>run_attempt 從 1 開始，schema 非 required，缺值當 1（spec §4.2 E4）。</summary>
    private const int FirstRunAttempt = 1;

    // spec §4.1：時間戳一律當 UTC ISO 8601 解析；沒有時區的寫法視為 UTC。
    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public static AccountInfo? ToAccount(SimpleUserDto? dto) =>
        dto is { Id: { } id } && !IsBlank(dto.Login) ? new AccountInfo(id, dto.Login) : null;

    public static RepoInfo? ToRepo(RepoDto? dto)
    {
        if (dto is not { Id: { } id, Owner: { Id: { } ownerId } owner, Private: { } isPrivate, Fork: { } isFork,
                Archived: { } isArchived, Disabled: { } isDisabled }
            || IsBlank(dto.Name)
            || !IsRepoFullName(dto.FullName)
            || IsBlank(owner.Login)
            || IsBlank(owner.Type)
            || IsBlank(dto.DefaultBranch)
            || IsBlank(dto.HtmlUrl))
        {
            return null;
        }

        // pushed_at 可為 null（spec §4.2 E2）；格式不對也當成沒有。分類交給 RepoDiscovery.Merge，這裡先填 Own。
        // permissions 是選填（第二階段 spec §4.1），不影響這一筆能不能用；push 另外由 ToCanPush 取。
        return new RepoInfo(id, dto.FullName, dto.Name, ownerId, owner.Login, owner.Type, isPrivate, isFork, isArchived,
            isDisabled, dto.DefaultBranch, dto.HtmlUrl, OptionalTime(dto.PushedAt), RepoCategory.Own);
    }

    /// <summary>permissions.push；沒有 permissions 物件（或物件裡沒有 push）時是 null，代表不確定（第二階段 spec §4.1）。</summary>
    public static bool? ToCanPush(RepoDto? dto) => dto?.Permissions?.Push;

    public static WorkflowInfo? ToWorkflow(long repoId, WorkflowDto? dto)
    {
        if (dto is not { Id: { } id, Name: { } name } || IsBlank(dto.Path) || IsBlank(dto.State) || IsBlank(dto.HtmlUrl))
        {
            return null;
        }

        return new WorkflowInfo(repoId, id, name, dto.Path, dto.State, dto.HtmlUrl);
    }

    /// <param name="repoId">查詢的 repo；run 的 RepoId 一律用它。</param>
    public static RunInfo? ToRun(long repoId, RunDto? dto)
    {
        if (dto is not { Id: { } id, WorkflowId: { } workflowId, RunNumber: { } runNumber, DisplayTitle: { } title, Path: { } path,
                HeadRepository.Id: { } headRepoId }
            || IsBlank(dto.Event)
            || IsBlank(dto.HeadSha)
            || IsBlank(dto.HtmlUrl)
            || !TryParseTime(dto.CreatedAt, out var createdAt)
            || !TryParseTime(dto.UpdatedAt, out var updatedAt))
        {
            return null;
        }

        var attempt = dto.RunAttempt is { } value && value >= FirstRunAttempt ? value : FirstRunAttempt;
        var pullRequestNumbers = (dto.PullRequests ?? [])
            .Select(pullRequest => pullRequest?.Number)
            .OfType<int>()
            .Distinct()
            .ToArray();

        return new RunInfo(
            id,
            repoId,
            workflowId,
            dto.Name,
            path,
            runNumber,
            attempt,
            title,
            dto.Event,
            dto.Status,
            dto.Conclusion,
            dto.HeadBranch,
            dto.HeadSha,
            headRepoId,
            dto.HeadRepository.FullName,
            dto.HeadRepository.HtmlUrl,
            dto.Actor?.Id,
            dto.Actor?.Login,
            dto.TriggeringActor?.Login,
            pullRequestNumbers,
            OptionalTime(dto.RunStartedAt),
            createdAt,
            updatedAt,
            dto.HtmlUrl);
    }

    public static PullRequestInfo? ToPullRequest(long repoId, PullRequestDto? dto)
    {
        if (dto is not { Number: { } number, Head: { } head } || IsBlank(head.Ref) || IsBlank(head.Sha) || IsBlank(dto.HtmlUrl))
        {
            return null;
        }

        return new PullRequestInfo(repoId, number, head.Repo?.Id, head.Repo?.FullName, head.Ref, head.Sha, dto.HtmlUrl);
    }

    public static JobInfo? ToJob(JobDto? dto)
    {
        if (dto is not { Id: { } id, Name: { } name } || IsBlank(dto.Status))
        {
            return null;
        }

        return new JobInfo(id, name, dto.Status, dto.Conclusion, OptionalTime(dto.StartedAt), OptionalTime(dto.CompletedAt),
            dto.HtmlUrl);
    }

    /// <summary>只讀 resources.core；伺服器時間取回應的 Date header。</summary>
    public static RateLimitSnapshot? ToRateLimit(RateLimitDto? dto, DateTimeOffset? serverDate) =>
        dto?.Resources?.Core is { } core
            ? new RateLimitSnapshot(core.Limit, core.Remaining, core.Used, core.Reset, "core", serverDate)
            : null;

    /// <summary>永遠不是 null：200 就算檢查完成（需求），缺欄位不能變成 ParseError，由 UpdateCheckRules 判斷。</summary>
    public static ReleaseInfo ToRelease(ReleaseDto dto) => new(dto.TagName, dto.HtmlUrl);

    /// <summary><c>owner/repo</c>：剛好一個斜線，兩邊都不是空白。</summary>
    public static bool IsRepoFullName([NotNullWhen(true)] string? fullName)
    {
        if (IsBlank(fullName))
        {
            return false;
        }

        var parts = fullName.Split('/');
        return parts.Length == 2 && !IsBlank(parts[0]) && !IsBlank(parts[1]);
    }

    /// <summary>UTC ISO 8601；成功時回傳的 Offset 一律是 0。</summary>
    public static bool TryParseTime(string? text, out DateTimeOffset value)
    {
        if (!IsBlank(text)
            && DateTimeOffset.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed.ToUniversalTime();
            return true;
        }

        value = default;
        return false;
    }

    private static DateTimeOffset? OptionalTime(string? text) => TryParseTime(text, out var value) ? value : null;

    private static bool IsBlank([NotNullWhen(false)] string? text) => string.IsNullOrWhiteSpace(text);
}
