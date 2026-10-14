using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services;

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
    private readonly ILogger<HomeViewModel> _logger;

    public HomeViewModel(
        ClickCounterViewModel clickCounter,
        KeyCounterViewModel keyCounter,
        UsageViewModel usage,
        AppUsageViewModel appUsage,
        ActivityDataService activityData,
        ILogger<HomeViewModel> logger
    )
    {
        _clickCounter = clickCounter;
        _keyCounter = keyCounter;
        _usage = usage;
        _appUsage = appUsage;
        _activityData = activityData;
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

    /// <summary>范围切换后的联动刷新：电脑使用时长 + 应用统计（均为异步数据源）。</summary>
    private async Task RefreshRangeAsync()
    {
        try
        {
            var (from, to) = SelectedRange.ToDateRange();

            // 电脑使用时长（键鼠活动并集，不重叠）——与"电脑使用"页同源同口径
            var usage = await Task.Run(() => _activityData.GetActiveDuration(from, to));
            RangeUsageText = usage > TimeSpan.Zero ? ActivityCalculator.Format(usage) : "—";

            // 应用统计（轻量查询：应用数 + 用时最多的应用）
            var (count, top) = await _appUsage.GetRangeStatsAsync(SelectedRange);
            RangeAppCount = count;
            RangeTopAppName = top ?? "—";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新首页范围数据失败");
        }
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
