using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>E8 <c>GET …/actions/runs/{run_id}/jobs</c> 的一頁。</summary>
internal sealed class JobsPageDto : IListEnvelope
{
    [JsonPropertyName("total_count")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("jobs")]
    public List<JobDto?>? Jobs { get; init; }
}

internal sealed class JobDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}
