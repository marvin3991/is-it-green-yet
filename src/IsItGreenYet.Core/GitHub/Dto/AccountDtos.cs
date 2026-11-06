using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>
/// Simple User（E1 的回應、repo 的 owner、run 的 actor、PR 的 user）。
/// 所有欄位都宣告成可為 null：缺欄位由 <see cref="DtoMapper"/> 判斷，不在反序列化時丟例外。
/// </summary>
internal sealed class SimpleUserDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("login")]
    public string? Login { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

/// <summary>Minimal Repository（run 的 head_repository、PR 的 head.repo）。</summary>
internal sealed class MinimalRepositoryDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}

/// <summary>錯誤回應的 body，只取 message。</summary>
internal sealed class ErrorDto
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
