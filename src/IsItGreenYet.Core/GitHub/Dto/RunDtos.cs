using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>E4／E5 <c>GET …/actions/runs</c> 的回應。</summary>
internal sealed class RunsPageDto : IListEnvelope
{
    [JsonPropertyName("total_count")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("workflow_runs")]
    public List<RunDto?>? WorkflowRuns { get; init; }
}

/// <summary>一筆 workflow run（E4、E5 的項目，E6 的回應），只取 spec §4.2 E4 列出的欄位。</summary>
internal sealed class RunDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("display_title")]
    public string? DisplayTitle { get; init; }

    [JsonPropertyName("run_number")]
    public int? RunNumber { get; init; }

    [JsonPropertyName("run_attempt")]
    public int? RunAttempt { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }

    [JsonPropertyName("workflow_id")]
    public long? WorkflowId { get; init; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; init; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("actor")]
    public SimpleUserDto? Actor { get; init; }

    [JsonPropertyName("triggering_actor")]
    public SimpleUserDto? TriggeringActor { get; init; }

    [JsonPropertyName("run_started_at")]
    public string? RunStartedAt { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("pull_requests")]
    public List<RunPullRequestDto?>? PullRequests { get; init; }

    /// <summary>required 的 Minimal Repository；不用頂層、非 required 的 head_repository_id（spec §4.2 E4）。</summary>
    [JsonPropertyName("head_repository")]
    public MinimalRepositoryDto? HeadRepository { get; init; }
}

/// <summary>run 的 pull_requests[] 項目，只取編號。</summary>
internal sealed class RunPullRequestDto
{
    [JsonPropertyName("number")]
    public int? Number { get; init; }
}
