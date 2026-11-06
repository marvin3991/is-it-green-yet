using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>E7 <c>GET /repos/{owner}/{repo}/pulls</c> 的一個項目。</summary>
internal sealed class PullRequestDto
{
    [JsonPropertyName("number")]
    public int? Number { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("head")]
    public PullRequestRefDto? Head { get; init; }
}

internal sealed class PullRequestRefDto
{
    [JsonPropertyName("ref")]
    public string? Ref { get; init; }

    [JsonPropertyName("sha")]
    public string? Sha { get; init; }

    /// <summary>文件標 required，保守起見仍當成可為 null（來源 fork 被刪掉時）。</summary>
    [JsonPropertyName("repo")]
    public MinimalRepositoryDto? Repo { get; init; }
}
