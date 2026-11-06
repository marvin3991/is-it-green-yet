using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>E3 <c>GET /repos/{owner}/{repo}/actions/workflows</c>。</summary>
internal sealed class WorkflowsPageDto : IListEnvelope
{
    [JsonPropertyName("total_count")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("workflows")]
    public List<WorkflowDto?>? Workflows { get; init; }
}

internal sealed class WorkflowDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}
