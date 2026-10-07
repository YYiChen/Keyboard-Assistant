using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XAssistant.Services;

/// <summary>一个应用的展示信息。</summary>
public sealed class AppDisplayInfo
{
    public required string ProcessName { get; init; }

    /// <summary>面向用户的名称（如 "Microsoft Word"）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>应用图标。取不到时为 null，界面用首字母色块兜底。</summary>
    public ImageSource? Icon { get; init; }

    /// <summary>展示名首字符，用于无图标时的占位色块。</summary>
    public required string Initial { get; init; }
}

/// <summary>
/// 把进程名解析成用户能认出的名称与图标。
///
/// 背景：数据库里只存了进程名（<c>winword</c>、<c>msedge</c>、<c>snow_shot</c>…），
/// 原界面只是把首字母大写就直接显示，结果是一屏看不懂的英文小写串。
///
/// 解析顺序（逐级降级，任一环失败都不影响整体）：
///   1. 找到该进程当前可执行文件的路径 → 读版本信息里的 FileDescription（真实产品名）
///      + 提取 exe 关联图标
///   2. 内置常见应用映射表（针对已经退出、无法查到路径的进程）
///   3. 兜底：进程名首字母大写
///
/// 结果带缓存 —— 解析涉及跨进程读取版本信息与图标提取，不应每次刷新都重做。
/// </summary>
public sealed class AppInfoResolver
{
    private readonly ConcurrentDictionary<string, AppDisplayInfo> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 常见应用映射。用于"进程已退出、查不到 exe 路径"的场景 ——
    /// 这种情况下拿不到 FileDescription，只能靠这张表。
    /// </summary>
    private static readonly Dictionary<string, string> KnownApps = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // 办公
        ["winword"] = "Microsoft Word",
        ["excel"] = "Microsoft Excel",
        ["powerpnt"] = "Microsoft PowerPoint",
        ["outlook"] = "Microsoft Outlook",
        ["onenote"] = "Microsoft OneNote",
        ["wps"] = "WPS Office",
        ["et"] = "WPS 表格",
        ["wpp"] = "WPS 演示",
        // 浏览器
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["firefox"] = "Mozilla Firefox",
        ["iexplore"] = "Internet Explorer",
        // 开发
        ["code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["pycharm64"] = "PyCharm",
        ["idea64"] = "IntelliJ IDEA",
        ["windowsterminal"] = "Windows 终端",
        ["cmd"] = "命令提示符",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell",
        ["git-bash"] = "Git Bash",
        ["mintty"] = "Git Bash",
        // 系统
        ["explorer"] = "文件资源管理器",
        ["notepad"] = "记事本",
        ["mspaint"] = "画图",
        ["calc"] = "计算器",
        ["snippingtool"] = "截图工具",
        ["taskmgr"] = "任务管理器",
        ["control"] = "控制面板",
        ["regedit"] = "注册表编辑器",
        ["searchapp"] = "Windows 搜索",
        ["startmenuexperiencehost"] = "开始菜单",
        ["shellexperiencehost"] = "Windows 外壳",
        ["applicationframehost"] = "系统应用宿主",
        // 通讯 / 社交
        ["weixin"] = "微信",
        ["wechat"] = "微信",
        ["qq"] = "QQ",
        ["dingtalk"] = "钉钉",
        ["feishu"] = "飞书",
        ["lark"] = "飞书",
        ["telegram"] = "Telegram",
        ["discord"] = "Discord",
        ["slack"] = "Slack",
        ["zoom"] = "Zoom",
        ["teams"] = "Microsoft Teams",
        ["ms-teams"] = "Microsoft Teams",
        // 媒体
        ["spotify"] = "Spotify",
        ["potplayermini64"] = "PotPlayer",
        ["vlc"] = "VLC",
        ["foobar2000"] = "foobar2000",
        // 国内常用
        ["wechatdevtools"] = "微信开发者工具",
        ["youdaodict"] = "网易有道词典",
        ["qqmusic"] = "QQ 音乐",
        ["cloudmusic"] = "网易云音乐",
        ["bilibili"] = "哔哩哔哩",
        ["aliworkbench"] = "阿里旺旺",
        ["baidunetdisk"] = "百度网盘",
        ["thunder"] = "迅雷",
        ["sogouinput"] = "搜狗输入法",
        ["wetype"] = "微信输入法",
        // 专业软件（本机常见）
        ["sldworks"] = "SolidWorks",
        ["bambu-studio"] = "Bambu Studio",
        ["chatgpt"] = "ChatGPT",
        ["workbuddy"] = "WorkBuddy",
        ["snow_shot"] = "Snow Shot",
        ["hipstray"] = "Hipstray",
        ["radeonsoftware"] = "AMD 软件",
    };

    public AppDisplayInfo Resolve(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return Fallback(string.Empty);

        return _cache.GetOrAdd(processName, ResolveCore);
    }

    private static AppDisplayInfo ResolveCore(string processName)
    {
        // ── 优先：内置映射表 ──
        //
        // 这张表是面向中文使用习惯人工整理的，比 exe 自己声明的 FileDescription
        // 更贴合期望 —— 例如微信的 FileDescription 是 "Weixin"（拼音），
        // 而用户想看到的是"微信"；WPS 的若干组件同理。
        // 图标仍然尽力从 exe 提取，两者互不冲突。
        if (KnownApps.TryGetValue(processName, out string? known))
        {
            string? exePathForIcon = FindExecutablePath(processName);
            var icon = exePathForIcon is not null ? TryExtractIcon(exePathForIcon) : null;
            return Create(processName, known, icon);
        }

        // ── 其次：从正在运行的进程反查 exe，读真实产品名 + 图标 ──
        string? exePath = FindExecutablePath(processName);
        if (exePath is not null)
        {
            var fromFile = ResolveFromFile(processName, exePath);
            if (fromFile is not null)
                return fromFile;
        }

        // ── 兜底 ──
        return Fallback(processName);
    }

    private static AppDisplayInfo? ResolveFromFile(string processName, string exePath)
    {
        string? display = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            display = FirstNonEmpty(vi.FileDescription, vi.ProductName);
        }
        catch
        {
            // 读版本信息失败（文件被占用、无权限）时继续尝试其他来源
        }

        display ??= Path.GetFileNameWithoutExtension(exePath);

        return Create(processName, display, TryExtractIcon(exePath));
    }

    private static AppDisplayInfo Create(string processName, string displayName, ImageSource? icon) =>
        new()
        {
            ProcessName = processName,
            DisplayName = displayName,
            Icon = icon,
            Initial = displayName.Length > 0 ? displayName[..1].ToUpperInvariant() : "?",
        };

    private static AppDisplayInfo Fallback(string processName)
    {
        if (string.IsNullOrEmpty(processName))
            return Create(string.Empty, "未知应用", null);

        // 至少把首字母大写，别原样吐小写进程名
        string name =
            processName.Length > 0
                ? char.ToUpperInvariant(processName[0]) + processName[1..]
                : processName;

        return Create(processName, name, null);
    }

    /// <summary>找到该进程名对应可执行文件的完整路径。</summary>
    private static string? FindExecutablePath(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var p in processes)
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return path;
                }
                catch
                {
                    // 访问其他用户的进程/系统进程会抛异常，跳过即可
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // 枚举进程本身失败时放弃这一级
        }

        return null;
    }

    /// <summary>提取 exe 的关联图标并转成 WPF 可用的 ImageSource。</summary>
    private static ImageSource? TryExtractIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
                return null;

            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // 立即解码，不依赖流存活
            image.StreamSource = ms;
            image.EndInit();
            // 解析发生在后台线程，必须 Freeze 才能交给 UI 线程使用
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        }
        return null;
    }
}
