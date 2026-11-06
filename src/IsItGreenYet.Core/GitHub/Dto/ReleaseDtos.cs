using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>Release（E15），只取檢查新版本用到的兩個欄位。</summary>
internal sealed class ReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}
