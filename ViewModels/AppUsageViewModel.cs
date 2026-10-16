using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services;

namespace XAssistant.ViewModels;

public partial class AppUsageViewModel : ViewModelBase
{
    private static readonly string DbPath = Path.Combine(
        AppDataPathHelper.GetAppDataFolder(),
        "app_usage.db"
    );
    private readonly DispatcherTimer _timer;
    private readonly ILogger<AppUsageViewModel> _logger;
    private readonly AppInfoResolver _appInfo;
    private readonly ActivityDataService _activityData;

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private ObservableCollection<AppUsageItem> _appUsageList = new();

    /// <summary>前台使用的应用（后台占比 &lt; 60%），按前台时长排序。</summary>
    [ObservableProperty]
    private ObservableCollection<AppUsageItem> _foregroundApps = new();

    /// <summary>后台常驻的应用（后台占比 ≥ 60%），按后台时长排序。</summary>
    [ObservableProperty]
    private ObservableCollection<AppUsageItem> _backgroundApps = new();

    /// <summary>
    /// 电脑使用时长（键鼠活动的并集，天然不重叠）。
    ///
    /// 原先这里显示的是"各应用时长求和"，那是个**没有意义**的指标：
    /// 电脑上多个软件会同时开启，同一段时间会被重复计入多次。
    /// 实测 10-02 那天求和得到 607.9 小时 —— 而一天只有 24 小时。
    ///
    /// 所以主指标改为"电脑使用时长"（= 人在电脑前多久），
    /// 各应用的时长仍各自列出（那是"我在哪个应用上花了时间"）。
    /// </summary>
    [ObservableProperty]
    private string _computerUsageText = "—";

    /// <summary>各应用时长之和。会大于电脑使用时长（因为重叠），仅作参考。</summary>
    [ObservableProperty]
    private string _appSumText = "—";

    /// <summary>当天出现过的应用数量。</summary>
    [ObservableProperty]
    private int _appCount;

    /// <summary>占用时间最多的应用名（一眼看出今天主要在用哪个软件）。</summary>
    [ObservableProperty]
    private string _topAppText = "—";

    private volatile bool _isRefreshing;

    private const int RefreshIntervalSeconds = 2;

    public AppUsageViewModel(
        ILogger<AppUsageViewModel> logger,
        AppInfoResolver appInfo,
        ActivityDataService activityData
    )
    {
        _logger = logger;
        _appInfo = appInfo;
        _activityData = activityData;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        // 依赖日期的展示属性需要同步刷新
        OnPropertyChanged(nameof(DateDisplayText));
        OnPropertyChanged(nameof(IsToday));
        OnPropertyChanged(nameof(CanGoNext));
        _ = RefreshAsync();
    }

    // ══════════════ 日期切换 ══════════════
    // 原本用 WPF 原生 DatePicker，外观与整体风格冲突，且它是最难样式化的
    // 内置控件之一（需连 Calendar 弹出层一起重做）。
    // 改为「前一天 / 后一天 / 回到今天」三键式：覆盖绝大多数查看场景，
    // 操作比"展开日历再点日期"更快，视觉上也与设计系统一致。

    /// <summary>形如 "10-08 周三"；当天额外标注。</summary>
    public string DateDisplayText =>
        SelectedDate.Date == DateTime.Today
            ? $"今天 · {SelectedDate:MM-dd}"
            : $"{SelectedDate:MM-dd} {WeekdayName(SelectedDate.DayOfWeek)}";

    public bool IsToday => SelectedDate.Date == DateTime.Today;

    /// <summary>不允许翻到未来（未来的应用使用数据必然为空）。</summary>
    public bool CanGoNext => SelectedDate.Date < DateTime.Today;

    [RelayCommand]
    private void PreviousDay() => SelectedDate = SelectedDate.AddDays(-1);

    [RelayCommand]
    private void NextDay()
    {
        if (CanGoNext)
            SelectedDate = SelectedDate.AddDays(1);
    }

    [RelayCommand]
    private void GoToToday() => SelectedDate = DateTime.Today;

    /// <summary>
    /// 轻量范围统计（供首页「活跃应用」卡使用）。
    ///
    /// 与 <see cref="LoadAppUsageAsync"/> 分开：首页只需要「应用数 + 用时最多」
    /// 两个数字，不需要拉完整列表（那会带着图标提取等重活）。
    /// 注意：这里只用 AccumulatedSeconds 聚合，不含运行中会话的补偿——
    /// 对历史天是精确值；"今天"参与的范围误差仅数秒，可接受。
    /// </summary>
    public async Task<(int AppCount, string? TopAppDisplay, List<(string Name, int Minutes)> Top5)> GetRangeStatsAsync(StatsRange range)
    {
        var (from, to) = range.ToDateRange();
        return await Task.Run(() =>
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();

                // Top5：范围内按前台累计排序的前五个应用（含显示名转换）
                var top5 = new List<(string Name, int Minutes)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        """
                        SELECT ProcessName, SUM(AccumulatedSeconds) AS Total
                        FROM ProcessSession
                        WHERE Date >= $from AND Date < $to
                        GROUP BY ProcessName
                        ORDER BY Total DESC
                        LIMIT 5
                        """;
                    cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var name = reader.GetString(0);
                        var minutes = (int)Math.Round(reader.GetDouble(1) / 60);
                        top5.Add((_appInfo.Resolve(name).DisplayName, minutes));
                    }
                }

                // 应用总数：单独一次 COUNT(DISTINCT)，与 Top5 解耦
                int appCount = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COUNT(DISTINCT ProcessName) FROM ProcessSession WHERE Date >= $from AND Date < $to";
                    cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
                    appCount = Convert.ToInt32(cmd.ExecuteScalar());
                }

                return (appCount, top5.Count > 0 ? top5[0].Name : null, top5);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "统计范围内应用数据失败");
                return (0, null, new List<(string Name, int Minutes)>());
            }
        });
    }

    private static string WeekdayName(DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日",
        };

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
            return;
        _isRefreshing = true;
        try
        {
            var list = await LoadAppUsageAsync(SelectedDate);

            // ── 派生指标 ──
            // 各应用时长之和（会因应用并行开启而重叠，只作参考）
            long appSumSeconds = list.Sum(x => x.TotalSeconds);

            // 占比以"各应用之和"为分母：占比要回答的是
            // "在所有这些应用的使用量里，这个应用占多少"，用重叠和有明确含义。
            foreach (var item in list)
                item.Percent = appSumSeconds > 0 ? item.TotalSeconds * 100.0 / appSumSeconds : 0;

            AppUsageList = new ObservableCollection<AppUsageItem>(list);

            // ── 分组：前台使用 vs 后台常驻 ──
            //
            // 用户的核心诉求是"区分开前台应用和后台应用"——光在每个条目上
            // 标注"后台 X"不够直观，常驻应用（微信、ChatGPT）会混在排行里。
            //
            // 判定用后台占比 ≥60%：微信这类常驻应用通常 >80%，
            // 正常使用的应用 <30%，60% 是中间的安全分界。
            // 注意：旧版前台判定有缺陷（置顶悬浮窗被误判为前台），
            // 历史数据可能失真；新版运行后占比会自然趋向真实值。
            const int BackgroundResidentShare = 60;

            ForegroundApps = new ObservableCollection<AppUsageItem>(
                list.Where(x => x.BackgroundShare < BackgroundResidentShare));
            BackgroundApps = new ObservableCollection<AppUsageItem>(
                list.Where(x => x.BackgroundShare >= BackgroundResidentShare)
                    .OrderByDescending(x => x.BackgroundSeconds));

            AppCount = list.Count;
            TopAppText = list.Count > 0 ? list[0].DisplayName : "—";
            AppSumText = FormatTotal(appSumSeconds);

            // 主指标：电脑使用时长（键鼠活动并集，不重叠）
            var active = await Task.Run(() =>
                _activityData.GetActiveDuration(
                    SelectedDate.Date,
                    SelectedDate.Date.AddDays(1)
                )
            );
            ComputerUsageText = active > TimeSpan.Zero ? ActivityCalculator.Format(active) : "—";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新软件使用数据失败");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static string FormatTotal(long seconds)
    {
        if (seconds <= 0)
            return "—";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分"
            : $"{(int)ts.TotalMinutes} 分钟";
    }

    private async Task<List<AppUsageItem>> LoadAppUsageAsync(DateTime date)
    {
        var result = new List<AppUsageItem>();
        var dateStr = date.ToString("yyyy-MM-dd");

        await Task.Run(() =>
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    @"
                SELECT ProcessName,
                    SUM(
                        CASE WHEN EndTime IS NULL THEN
                            -- 运行中的会话：已累计值 + 从最后一次刷新到现在的补偿。
                            --
                            -- 补偿值必须设上限：数据库里的 LastUpdateTime 若因异常
                            -- （进程被强杀、时钟回拨、旧版本留下的残留）而停在很久以前，
                            -- 无上限的补偿会把时长算成几十小时。
                            -- 上限取「今天已过去的时间」—— 单日使用时长不可能超过这个值。
                            MIN(
                                AccumulatedSeconds + (julianday('now','localtime') - julianday(COALESCE(LastUpdateTime, StartTime))) * 86400,
                                (julianday('now','localtime') - julianday($dayStart)) * 86400
                            )
                        ELSE AccumulatedSeconds
                        END
                    ) AS Seconds,
                    SUM(COALESCE(BackgroundSeconds, 0)) AS BgSeconds,
                    MIN(StartTime) AS StartTime,
                    MAX(COALESCE(EndTime, datetime('now','localtime'))) AS EndTime
                FROM ProcessSession
                WHERE Date = $date
                GROUP BY ProcessName
                ORDER BY Seconds DESC";
                cmd.Parameters.AddWithValue("$date", dateStr);
                // 当日零点，用于给补偿值封顶
                cmd.Parameters.AddWithValue("$dayStart", date.ToString("yyyy-MM-dd") + " 00:00:00");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var seconds = (long)Math.Round(reader.GetDouble(1));
                    var bgSeconds = (long)Math.Round(reader.GetDouble(2));
                    var startStr = reader.GetString(3);
                    var endStr = reader.GetString(4);

                    DateTime? startTime = DateTime.TryParse(startStr, out var st) ? st : null;
                    DateTime? endTime = DateTime.TryParse(endStr, out var et) ? et : null;

                    // 解析成用户认得出的名称与图标。
                    // 数据库里只有进程名（winword / msedge / snow_shot…），
                    // 原实现仅把首字母大写就直接显示，结果是一屏看不懂的小写英文。
                    // 解析涉及读 exe 版本信息与提取图标，本方法已在后台线程执行。
                    var info = _appInfo.Resolve(name);

                    result.Add(
                        new AppUsageItem
                        {
                            ProcessName = name,
                            DisplayName = info.DisplayName,
                            Icon = info.Icon,
                            Initial = info.Initial,
                            TotalSeconds = seconds,
                            BackgroundSeconds = bgSeconds,
                            StartTime = startTime,
                            EndTime = endTime,
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询软件使用数据失败");
            }
        });

        return result;
    }
}
