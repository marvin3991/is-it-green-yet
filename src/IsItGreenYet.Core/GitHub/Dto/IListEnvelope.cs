namespace IsItGreenYet.Core.GitHub.Dto;

/// <summary>
/// 把清單包在物件裡回傳的端點（E3、E4、E5、E8：<c>{"total_count":…,"workflows":[…]}</c>）。
/// 列表回 204 或空 body 時，GitHubClient 把這種型別當成 0 筆（spec §4.1 回應容錯），而不是 JSON 錯誤。
/// </summary>
internal interface IListEnvelope;
