using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>
/// 首页：跨模块总览。
///
/// v1.3.3 起支持时间范围切换（今天/昨天/近 7 天/近 30 天）——
/// 用户反馈首页只有"今日"太单薄。
///
/// 范围状态是<b>共享</b>的：切首页的范围会同步到键盘/鼠标/软件使用页
/// （它们本就各持一份 <see cref="StatsRange"/>，且语义相同——"全局统一的时间范围"
/// 比各页各管各的更符合直觉，也避免"首页近 7 天、键盘页今天"的困惑）。
///
/// 「电脑使用」由本 VM 直接用 <see cref="ActivityDataService"/> 按范围计算，
/// 不代理 <see cref="UsageViewModel.TodayUsageText"/> —— 那个属性绑定着
/// 使用追踪服务的实时管道逻辑（含回退链），语义是"今天"，不适合按范围改写。
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    private readonly ClickCounterViewModel _clickCounter;
    private readonly KeyCounterViewModel _keyCounter;
    private readonly UsageViewModel _usage;
    private readonly AppUsageViewModel _appUsage;
    private readonly ActivityDataService _activityData;
    private readonly IKeyDatabaseService _keyDb;
    private readonly IClickDatabaseService _clickDb;
    private readonly ILogger<HomeViewModel> _logger;

    public HomeViewModel(
        ClickCounterViewModel clickCounter,
        KeyCounterViewModel keyCounter,
        UsageViewModel usage,
        AppUsageViewModel appUsage,
        ActivityDataService activityData,
        IKeyDatabaseService keyDb,
        IClickDatabaseService clickDb,
        ILogger<HomeViewModel> logger
    )
    {
        _clickCounter = clickCounter;
        _keyCounter = keyCounter;
        _usage = usage;
        _appUsage = appUsage;
        _activityData = activityData;
        _keyDb = keyDb;
        _clickDb = clickDb;
        _logger = logger;

        _clickCounter.PropertyChanged += OnClickCounterPropertyChanged;
        _keyCounter.PropertyChanged += OnKeyCounterPropertyChanged;
        _usage.PropertyChanged += OnUsagePropertyChanged;
        _appUsage.PropertyChanged += OnAppUsagePropertyChanged;

        _ = RefreshRangeAsync();
    }

    // ========== 时间范围 ==========

    [ObservableProperty]
    private StatsRange _selectedRange = StatsRange.Today;

    partial void OnSelectedRangeChanged(StatsRange value)
    {
        // 同步到各子 VM：键盘/鼠标会自行刷新其范围数据；
        // 软件使用页保持自己的按日浏览，这里只取它的轻量范围统计。
        _keyCounter.SelectedRange = value;
        _clickCounter.SelectedRange = value;
        _ = RefreshRangeAsync();
    }

    /// <summary>范围切换后的联动刷新：电脑使用时长 + 应用统计 + 环比 + 洞察。</summary>
    private async Task RefreshRangeAsync()
    {
        try
        {
            var (from, to) = SelectedRange.ToDateRange();
            var days = Math.Max(SelectedRange.DayCount(), 1);

            // ── 电脑使用时长（键鼠活动并集，不重叠）──
            var usage = await Task.Run(() => _activityData.GetActiveDuration(from, to));
            RangeUsageText = usage > TimeSpan.Zero ? ActivityCalculator.Format(usage) : "—";

            // ── 应用统计（Top5 + 总数 + 用时最多）──
            var (count, top, top5) = await _appUsage.GetRangeStatsAsync(SelectedRange);
            RangeAppCount = count;
            RangeTopAppName = top ?? "—";

            TopApps.Clear();
            var max = top5.Count > 0 ? top5[0].Minutes : 0;
            int rank = 1;
            foreach (var (name, minutes) in top5)
            {
                TopApps.Add(new TopAppItem
                {
                    Name = name,
                    Minutes = minutes,
                    Rank = rank++,
                    BarWidth = max > 0 ? minutes * 100.0 / max : 0,
                });
            }

            // ── 键盘/鼠标/使用：今日给环比，其他范围给日均 ──
            await RefreshDeltasAsync(days);

            // ── 洞察结论 ──
            await BuildInsightsAsync(days);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新首页范围数据失败");
        }
    }

    /// <summary>
    /// 指标卡副标题：
    /// 「今天」范围下与昨日对比（用户最关心"我今天比昨天怎么样"）；
    /// 其他范围没有天然的"上一期"直觉，改给日均。
    /// </summary>
    private async Task RefreshDeltasAsync(int days)
    {
        int keyNow = _keyCounter.RangeTotal;
        int mouseNow = _clickCounter.RangeTotal;

        if (SelectedRange == StatsRange.Today)
        {
            var yesterday = DateTime.Today.AddDays(-1);
            int keyPrev = await Task.Run(() => _keyDb.GetHourlyCounts(yesterday).Sum());
            var clickPrev = await Task.Run(() => _clickDb.GetClickCountsByDate(yesterday));
            int mousePrev = clickPrev.Values.Sum();
            var usagePrev = await Task.Run(() => _activityData.GetActiveDuration(yesterday));
            var usageNow = await Task.Run(() => _activityData.GetActiveDuration(DateTime.Today));

            KeyDeltaText = FormatDelta(keyNow, keyPrev, "次");
            MouseDeltaText = FormatDelta(mouseNow, mousePrev, "次");
            UsageDeltaText = FormatDeltaMinutes((int)usageNow.TotalMinutes, (int)usagePrev.TotalMinutes);
        }
        else
        {
            KeyDeltaText = $"日均 {keyNow / days:N0} 次";
            MouseDeltaText = $"日均 {mouseNow / days:N0} 次";
            var (from, to) = SelectedRange.ToDateRange();
            var usage = await Task.Run(() => _activityData.GetActiveDuration(from, to));
            UsageDeltaText = $"日均 {ActivityCalculator.Format(TimeSpan.FromMinutes(usage.TotalMinutes / days))}";
        }
    }

    /// <summary>
    /// 自动洞察：从现有数据源拼出 2–4 条"人话"结论。
    /// 每条都必须有明确的数字支撑——宁缺毋滥，不输出"数据表现平稳"这类废话。
    /// </summary>
    private async Task BuildInsightsAsync(int days)
    {
        var lines = new List<string>();

        try
        {
            // ① 使用时长：本周期日均 vs 上一周期日均
            var daily = await Task.Run(() => _activityData.GetDailyDurations(days * 2));
            if (daily.Count >= days)
            {
                var cur = daily.Skip(daily.Count - days).Average(d => d.Duration.TotalMinutes);
                var prevList = daily.Take(daily.Count - days).ToList();
                if (prevList.Count > 0)
                {
                    var prev = prevList.Average(d => d.Duration.TotalMinutes);
                    if (prev > 1)
                    {
                        var diff = cur - prev;
                        lines.Add(diff >= 0
                            ? $"{SelectedRange.Label()}日均 {ActivityCalculator.Format(TimeSpan.FromMinutes(cur))}，比上一周期多 {diff:F0} 分钟"
                            : $"{SelectedRange.Label()}日均 {ActivityCalculator.Format(TimeSpan.FromMinutes(cur))}，比上一周期少 {-diff:F0} 分钟");
                    }
                }
            }

            // ② 今日峰值时段（仅"今天"范围——其他范围的峰值是平均出来的，没有信息量）
            if (SelectedRange == StatsRange.Today)
            {
                var hourly = await Task.Run(() => _keyDb.GetHourlyCounts(DateTime.Today));
                int peak = 0;
                for (int h = 1; h < 24; h++)
                    if (hourly[h] > hourly[peak]) peak = h;
                if (hourly[peak] > 0)
                    lines.Add($"今天 {peak:00} 点前后输入最密集，该小时共 {hourly[peak]:N0} 次按键");
            }

            // ③ 应用集中度：第一名占前五名总用时的比例
            if (TopApps.Count > 0)
            {
                var sum = TopApps.Sum(t => t.Minutes);
                if (sum > 0)
                {
                    var t = TopApps[0];
                    lines.Add($"「{t.Name}」占前五名应用用时总和的 {t.Minutes * 100 / sum}%");
                }
            }

            // ④ 输入构成：键盘/鼠标比值
            int key = _keyCounter.RangeTotal, mouse = _clickCounter.RangeTotal;
            if (key > 0 && mouse > 0)
            {
                double ratio = (double)key / mouse;
                lines.Add($"键盘与鼠标输入比约 {ratio:F1} : 1");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成首页洞察失败（不影响其他指标）");
        }

        Insights.Clear();
        foreach (var line in lines.Take(4))
            Insights.Add(line);
        HasInsights = Insights.Count > 0;
    }

    private static string FormatDelta(int now, int prev, string unit)
    {
        if (prev <= 0)
            return now > 0 ? $"较昨日 +100%（{now:N0} {unit}）" : "昨日无记录";
        var pct = (now - prev) * 100.0 / prev;
        var sign = pct >= 0 ? "+" : "";
        return $"较昨日 {sign}{pct:F0}%（{now:N0} {unit}）";
    }

    private static string FormatDeltaMinutes(int nowMin, int prevMin)
    {
        if (prevMin <= 0)
            return nowMin > 0 ? "较昨日 +100%" : "昨日无记录";
        var pct = (nowMin - prevMin) * 100.0 / prevMin;
        var sign = pct >= 0 ? "+" : "";
        return $"较昨日 {sign}{pct:F0}%";
    }

    /// <summary>当前范围的标签（今天 / 昨天 / 近 7 天 / 近 30 天）。</summary>
    public string RangeLabel => SelectedRange.Label();

    public bool IsRangeToday
    {
        get => SelectedRange == StatsRange.Today;
        set { if (value) SelectedRange = StatsRange.Today; }
    }

    public bool IsRangeYesterday
    {
        get => SelectedRange == StatsRange.Yesterday;
        set { if (value) SelectedRange = StatsRange.Yesterday; }
    }

    public bool IsRangeLast7Days
    {
        get => SelectedRange == StatsRange.Last7Days;
        set { if (value) SelectedRange = StatsRange.Last7Days; }
    }

    public bool IsRangeLast30Days
    {
        get => SelectedRange == StatsRange.Last30Days;
        set { if (value) SelectedRange = StatsRange.Last30Days; }
    }

    // ========== 鼠标点击模块 ==========
    public string MouseRecordingStatus => _clickCounter.MouseRecordingStatus;
    public System.Windows.Media.Brush MouseRecordingColor => _clickCounter.MouseRecordingColor;
    public int MouseTotalClicks => _clickCounter.MouseTotalClicks;

    /// <summary>当前范围内的鼠标点击数（代理鼠标页，随全局范围联动）。</summary>
    public int MouseRangeClicks => _clickCounter.RangeTotal;

    // ========== 键盘记录模块 ==========
    public string KeyRecordingStatus => _keyCounter.KeyRecordingStatus;
    public System.Windows.Media.Brush KeyRecordingColor => _keyCounter.KeyRecordingColor;
    public int KeyTotalPresses => _keyCounter.KeyTotalPresses;

    /// <summary>当前范围内的键盘按键数（代理键盘页，随全局范围联动）。</summary>
    public int KeyRangePresses => _keyCounter.RangeTotal;

    // ========== 电脑使用模块 ==========

    /// <summary>当前范围内的电脑使用时长（键鼠活动并集）。</summary>
    [ObservableProperty]
    private string _rangeUsageText = "—";

    // ========== 软件使用模块 ==========

    /// <summary>近 7 日活跃趋势（复用电脑使用页的数据源）。</summary>
    public ObservableCollection<DayBar> WeeklyActivity => _usage.WeeklyDurations;

    /// <summary>当前范围内用过的应用数。</summary>
    [ObservableProperty]
    private int _rangeAppCount;

    /// <summary>当前范围内用时最多的应用。</summary>
    [ObservableProperty]
    private string _rangeTopAppName = "—";

    // ══════════════ 深度分析区 ══════════════
    //
    // 用户反馈首页"信息丰富程度太少，完全不够看"。新增三块：

    /// <summary>今日 24 小时输入分布（直接代理键盘页的同源数据，口径一致）。</summary>
    public ObservableCollection<HourBar> HourlyBars => _keyCounter.HourlyBars;

    /// <summary>当前范围内应用用时 Top 5（横条图，宽度已归一化到第一名）。</summary>
    public ObservableCollection<TopAppItem> TopApps { get; } = new();

    /// <summary>自动生成的洞察结论（对比、峰值、集中度等，最多 4 条）。</summary>
    public ObservableCollection<string> Insights { get; } = new();

    /// <summary>是否有可展示的洞察（决定空状态文案）。</summary>
    [ObservableProperty]
    private bool _hasInsights;

    /// <summary>键盘卡副标题：今日显示「较昨日 ±x%」，其他范围显示「日均 N 次」。</summary>
    [ObservableProperty]
    private string _keyDeltaText = "";

    /// <summary>鼠标卡副标题（规则同上）。</summary>
    [ObservableProperty]
    private string _mouseDeltaText = "";

    /// <summary>电脑使用卡副标题（规则同上）。</summary>
    [ObservableProperty]
    private string _usageDeltaText = "";

    // ========== 属性变化转发 ==========
    private void OnClickCounterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClickCounterViewModel.MouseRecordingStatus):
                OnPropertyChanged(nameof(MouseRecordingStatus));
                break;
            case nameof(ClickCounterViewModel.MouseRecordingColor):
                OnPropertyChanged(nameof(MouseRecordingColor));
                break;
            // 鼠标页的范围数据刷新完成后，RangeTotal 会变 —— 转发给首页卡片
            case nameof(ClickCounterViewModel.RangeTotal):
                OnPropertyChanged(nameof(MouseRangeClicks));
                break;
        }
    }

    private void OnKeyCounterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(KeyCounterViewModel.KeyRecordingStatus):
                OnPropertyChanged(nameof(KeyRecordingStatus));
                break;
            case nameof(KeyCounterViewModel.KeyRecordingColor):
                OnPropertyChanged(nameof(KeyRecordingColor));
                break;
            case nameof(KeyCounterViewModel.RangeTotal):
                OnPropertyChanged(nameof(KeyRangePresses));
                break;
        }
    }

    private void OnUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsageViewModel.WeeklyDurations))
            OnPropertyChanged(nameof(WeeklyActivity));
    }

    private void OnAppUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 应用页自己的范围统计由首页主动拉取（GetRangeStatsAsync），
        // 这里只转发列表类变化（首页不再直接消费它的按日列表）。
    }
}
