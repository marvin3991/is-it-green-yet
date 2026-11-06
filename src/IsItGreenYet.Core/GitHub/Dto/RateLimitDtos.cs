using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>E10 <c>GET /rate_limit</c>。只讀 resources.core（2026-03-10 版已移除頂層的 rate，spec §4.1）。</summary>
internal sealed class RateLimitDto
{
    [JsonPropertyName("resources")]
    public RateLimitResourcesDto? Resources { get; init; }
}

internal sealed class RateLimitResourcesDto
{
    [JsonPropertyName("core")]
    public RateLimitResourceDto? Core { get; init; }
}

internal sealed class RateLimitResourceDto
{
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("remaining")]
    public int? Remaining { get; init; }

    [JsonPropertyName("used")]
    public int? Used { get; init; }

    /// <summary>UTC epoch 秒。</summary>
    [JsonPropertyName("reset")]
    public long? Reset { get; init; }
}
