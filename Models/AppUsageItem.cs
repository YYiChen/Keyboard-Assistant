namespace XAssistant.Models;

public class AppUsageItem
{
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// 面向用户的名称，由 <c>AppInfoResolver</c> 解析得到
    /// （原先这里只是把进程名首字母大写，所以界面上是一屏看不懂的小写英文串）。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>应用图标，取不到时为 null，界面用首字母色块兜底。</summary>
    public System.Windows.Media.ImageSource? Icon { get; set; }

    /// <summary>展示名首字符，用于无图标时的占位色块。</summary>
    public string Initial { get; set; } = "?";

    public long TotalSeconds { get; set; }

    /// <summary>
    /// 进程存活但不在前台的时间（v1.3.0 新增）。
    ///
    /// 用来区分「真的在用它」与「只是开着」：截图工具这类后台常驻程序
    /// 前台时长接近 0、后台时长很大；真正在用的程序则相反。
    /// 两者之和 ≤ 进程存活时长。
    /// </summary>
    public long BackgroundSeconds { get; set; }

    /// <summary>后台时长的可读文本；不足 1 分钟时显示「—」而不是「0 分钟」。</summary>
    public string FormattedBackground =>
        BackgroundSeconds >= 60 ? FormatSeconds(BackgroundSeconds) : "—";

    /// <summary>是否有值得展示的后台时间（用于决定界面是否显示这一段）。</summary>
    public bool HasBackground => BackgroundSeconds >= 60;

    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    /// <summary>占当日总时长的百分比（0–100），直接绑到进度条。</summary>
    public double Percent { get; set; }

    public string PercentText => Percent >= 1 ? $"{Percent:F0}%" : "<1%";

    public string FormattedUsage => FormatSeconds(TotalSeconds);

    public string FormattedStartTime => StartTime?.ToString("HH:mm:ss") ?? "--";

    public string FormattedEndTime => EndTime?.ToString("HH:mm:ss") ?? "--";

    private static string FormatSeconds(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes} 分钟";
        return $"{ts.Seconds} 秒";
    }
}
