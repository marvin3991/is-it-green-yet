using System.Text.Json.Serialization;

namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>E2 <c>GET /user/repos</c> 的一個項目，只取 spec §4.2 E2 列出的欄位，加上第二階段 spec §4.1 的 permissions。</summary>
internal sealed class RepoDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }

    [JsonPropertyName("owner")]
    public SimpleUserDto? Owner { get; init; }

    [JsonPropertyName("private")]
    public bool? Private { get; init; }

    [JsonPropertyName("fork")]
    public bool? Fork { get; init; }

    [JsonPropertyName("archived")]
    public bool? Archived { get; init; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }

    /// <summary>可為 null（spec §4.2 E2）。時間一律先收成字串，由 DtoMapper 解析，單一欄位格式錯誤不影響整份回應。</summary>
    [JsonPropertyName("pushed_at")]
    public string? PushedAt { get; init; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    /// <summary>選填；沒有這個物件時不確定能不能寫（第二階段 spec §4.1）。</summary>
    [JsonPropertyName("permissions")]
    public RepoPermissionsDto? Permissions { get; init; }
}

/// <summary>repo 的 permissions 物件，只取 push（有這個物件時是必填的 boolean，第二階段 spec §4.1）。</summary>
internal sealed class RepoPermissionsDto
{
    [JsonPropertyName("push")]
    public bool? Push { get; init; }
}
