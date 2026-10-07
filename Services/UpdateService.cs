using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XAssistant.Services;

/// <summary>检查更新的结果。</summary>
public sealed class UpdateCheckResult
{
    /// <summary>是否检查成功（网络/解析失败时为 false）。</summary>
    public bool Success { get; init; }

    /// <summary>是否存在比当前更新的版本。</summary>
    public bool HasUpdate { get; init; }

    /// <summary>最新版本号（已归一化，如 "1.2.0"）。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>当前版本号。</summary>
    public string CurrentVersion { get; init; } = string.Empty;

    /// <summary>Release 页面地址，供用户手动下载。</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>更新说明（已截断）。</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>失败原因（面向用户的文案）。</summary>
    public string? ErrorMessage { get; init; }

    public static UpdateCheckResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// 基于 GitHub Releases 的更新检测。
///
/// 机制参考同账号下「文言助手」（wenyan-word-training）的 tools/update_service.py，
/// 保留其关键约束：
///   · 只访问 github.com 及其资源域（白名单），不跟随任意跳转
///   · 版本号用严格正则解析，不认识的格式一律视为「无更新」而非崩溃
///   · 请求带超时，失败只提示不抛异常（更新检查绝不该影响主功能）
///
/// 差异：只做「检测 + 引导」，不做自动下载替换进程。
/// 自动替换 exe 需要处理"程序正在运行无法覆盖自身"、下载完整性校验、
/// 回滚等一连串问题，收益不足以匹配风险 —— 个人工具，跳转到 Release 页手动更新更稳。
/// </summary>
public sealed class UpdateService
{
    /// <summary>只允许这些主机，防止 API 返回的链接把我们引到别处。</summary>
    private static readonly string[] AllowedHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    /// <summary>形如 v1.2.3 或 1.2.3（可带 -beta.1 之类的预发布后缀）。</summary>
    private static readonly Regex VersionPattern = new(
        @"^v?(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.\-]+)?$",
        RegexOptions.Compiled
    );

    private const int TimeoutSeconds = 8;
    private const int MaxNotesLength = 2000;

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        // GitHub API 要求带 User-Agent，否则返回 403
        _http.DefaultRequestHeaders.Add("User-Agent", "KeyboardAssistant-UpdateChecker");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        string current = AppInfo.VersionText;

        try
        {
            string url = $"https://api.github.com/repos/{AppInfo.RepositorySlug}/releases/latest";

            if (!IsAllowed(url))
                return UpdateCheckResult.Fail("更新地址不在允许范围内。");

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult
                {
                    Success = true,
                    HasUpdate = false,
                    CurrentVersion = current,
                };

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Fail($"服务器返回 {(int)response.StatusCode}。");

            var release = await response
                .Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (release?.TagName is null)
                return UpdateCheckResult.Fail("未能解析版本信息。");

            var latest = NormalizeVersion(release.TagName);
            var mine = NormalizeVersion(current);

            // 任一侧版本号不规范时，保守地认为"无需更新"，并如实说明
            if (latest is null || mine is null)
                return new UpdateCheckResult
                {
                    Success = true,
                    HasUpdate = false,
                    CurrentVersion = current,
                    LatestVersion = release.TagName,
                };

            string releaseUrl = release.HtmlUrl ?? AppInfo.RepositoryUrl;
            if (!IsAllowed(releaseUrl))
                releaseUrl = AppInfo.RepositoryUrl;

            return new UpdateCheckResult
            {
                Success = true,
                HasUpdate = Compare(latest, mine) > 0,
                LatestVersion = latest,
                CurrentVersion = current,
                ReleaseUrl = releaseUrl,
                ReleaseNotes = Truncate(release.Body, MaxNotesLength),
            };
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Fail("请求超时，请检查网络或代理设置。");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Fail($"网络不可达：{ex.Message}");
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Fail($"检查失败：{ex.Message}");
        }
    }

    private static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && Array.Exists(AllowedHosts, h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var m = VersionPattern.Match(raw.Trim());
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : null;
    }

    /// <summary>逐段比较版本号。</summary>
    private static int Compare(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < 3; i++)
        {
            int cmp = int.Parse(pa[i]).CompareTo(int.Parse(pb[i]));
            if (cmp != 0)
                return cmp;
        }
        return 0;
    }

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max] + "\n…（已截断）";

    /// <summary>GitHub Releases API 的响应子集。</summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}
