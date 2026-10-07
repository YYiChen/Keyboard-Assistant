using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services.Interfaces;
using WpfApplication = System.Windows.Application;

namespace XAssistant.ViewModels;

public partial class ClickCounterViewModel : ViewModelBase
{
    private readonly IMouseClickHookService _hookService;
    private readonly IClickDatabaseService _dbService;
    private readonly IConfigurationService _configService;
    private readonly IMouseClickBuffer _buffer;

    [ObservableProperty]
    private int _leftClickCount;

    [ObservableProperty]
    private int _middleClickCount;

    [ObservableProperty]
    private int _rightClickCount;

    [ObservableProperty]
    private bool _isRecording;

    // 今天
    [ObservableProperty]
    private int _leftClickToday;

    [ObservableProperty]
    private int _middleClickToday;

    [ObservableProperty]
    private int _rightClickToday;

    // 日历选中日期及对应点击量
    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private int _selectedDateLeftCount;

    [ObservableProperty]
    private int _selectedDateMiddleCount;

    [ObservableProperty]
    private int _selectedDateRightCount;

    // ===== 新增：首页用聚合属性 =====
    public int MouseTodayClicks => LeftClickToday + MiddleClickToday + RightClickToday;
    public int MouseTotalClicks => LeftClickCount + MiddleClickCount + RightClickCount;

    public string MouseRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush MouseRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public ClickCounterViewModel(
        IMouseClickHookService hookService,
        IClickDatabaseService dbService,
        IConfigurationService configService,
        IMouseClickBuffer buffer
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;
        _buffer = buffer;

        // 先建好节流定时器：StartRecording() 之后随时可能有点击回调进来
        _uiTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            WpfApplication.Current.Dispatcher
        );
        _uiTimer.Tick += (_, _) =>
        {
            _uiTimer.Stop(); // 单次触发
            FlushUiCounters();
        };

        // 加载历史总计
        var counts = _dbService.GetClickCounts();
        LeftClickCount = counts["Left"];
        MiddleClickCount = counts["Middle"];
        RightClickCount = counts["Right"];

        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);

        _hookService.MouseClicked += OnMouseClicked;

        // 跨天自动刷新。
        // 本程序是常驻后台的（开机自启、静默运行），会跨越多个自然日。
        // 而「今日点击」的刷新原先只挂在 OnMouseClicked 的跨天分支上 ——
        // 意味着跨天时若用户只打开界面查看、不做任何点击，界面会一直显示
        // 昨天的数字当作「今日」。对每天都会发生的事，这是必然出现的显示错误。
        _dayRolloverTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _dayRolloverTimer.Tick += (_, _) => CheckDayRollover();
        _dayRolloverTimer.Start();

        RefreshRangeStatistics();

        if (_configService.GetRecordingAutoStart())
        {
            StartRecording();
        }
    }

    /// <summary>
    /// 检测是否跨天；跨天则重载「今日」计数并清零待刷计数。
    ///
    /// 清零是必要的：<see cref="RefreshDailyCounts"/> 是直接赋值，
    /// 但待刷计数会在下一次 <see cref="FlushUiCounters"/> 时以 <c>+=</c> 累加，
    /// 若不清零，昨天的点击会被算进今天。
    /// </summary>
    private void CheckDayRollover()
    {
        if (DateTime.Today == _lastRefreshDate)
            return;

        _lastRefreshDate = DateTime.Today;
        lock (_uiGate)
        {
            _pendingLeft = 0;
            _pendingMiddle = 0;
            _pendingRight = 0;
        }
        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);
    }

    // 当 IsRecording 变化时，自动通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(MouseRecordingStatus));
        OnPropertyChanged(nameof(MouseRecordingColor));
    }

    // ────────────── 时间范围 ──────────────

    /// <summary>当前统计范围（分段控件选择）。</summary>
    [ObservableProperty]
    private StatsRange _selectedRange = StatsRange.Today;

    partial void OnSelectedRangeChanged(StatsRange value)
    {
        OnPropertyChanged(nameof(IsRangeToday));
        OnPropertyChanged(nameof(IsRangeYesterday));
        OnPropertyChanged(nameof(IsRangeLast7Days));
        OnPropertyChanged(nameof(IsRangeLast30Days));
        OnPropertyChanged(nameof(RangeLabel));

        // 选快捷范围时把查看日期同步过去，避免"范围显示昨天、日期还停在三天前"
        switch (value)
        {
            case StatsRange.Today:
                SelectedDate = DateTime.Today;
                break;
            case StatsRange.Yesterday:
                SelectedDate = DateTime.Today.AddDays(-1);
                break;
        }

        RefreshRangeStatistics();
    }

    public bool IsRangeToday
    {
        get => SelectedRange == StatsRange.Today;
        set
        {
            if (value)
                SelectedRange = StatsRange.Today;
        }
    }

    public bool IsRangeYesterday
    {
        get => SelectedRange == StatsRange.Yesterday;
        set
        {
            if (value)
                SelectedRange = StatsRange.Yesterday;
        }
    }

    public bool IsRangeLast7Days
    {
        get => SelectedRange == StatsRange.Last7Days;
        set
        {
            if (value)
                SelectedRange = StatsRange.Last7Days;
        }
    }

    public bool IsRangeLast30Days
    {
        get => SelectedRange == StatsRange.Last30Days;
        set
        {
            if (value)
                SelectedRange = StatsRange.Last30Days;
        }
    }

    public string RangeLabel => SelectedRange.Label();

    /// <summary>范围内的左/中/右键点击次数。</summary>
    [ObservableProperty]
    private int _rangeLeft;

    [ObservableProperty]
    private int _rangeMiddle;

    [ObservableProperty]
    private int _rangeRight;

    public int RangeTotal => RangeLeft + RangeMiddle + RangeRight;

    /// <summary>三种按键的构成比例（占比条数据）。</summary>
    public ObservableCollection<ClickCompositionItem> ClickComposition { get; } = new();

    /// <summary>
    /// 按当前范围重算点击统计。
    /// 自定义单日（Custom）时以 <see cref="SelectedDate"/> 那一天为范围。
    /// </summary>
    private void RefreshRangeStatistics()
    {
        var (from, to) =
            SelectedRange == StatsRange.Custom
                ? (SelectedDate.Date, SelectedDate.Date.AddDays(1))
                : SelectedRange.ToDateRange();

        var counts = _dbService.GetClickCountsInRange(from, to);

        RangeLeft = counts.GetValueOrDefault("Left");
        RangeMiddle = counts.GetValueOrDefault("Middle");
        RangeRight = counts.GetValueOrDefault("Right");

        OnPropertyChanged(nameof(RangeTotal));

        // 构成比例（三项合计为分母）
        int total = RangeTotal;
        ClickComposition.Clear();
        foreach (var (name, count) in new[]
        {
            ("左键", RangeLeft),
            ("中键", RangeMiddle),
            ("右键", RangeRight),
        })
        {
            ClickComposition.Add(
                new ClickCompositionItem
                {
                    Name = name,
                    Count = count,
                    Percent = total > 0 ? count * 100.0 / total : 0,
                }
            );
        }
    }

    // SelectedDate 变更时自动加载对应日期的点击量
    partial void OnSelectedDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(DateDisplayText));
        OnPropertyChanged(nameof(CanGoNext));
        // 日期变化会改变"今天/昨天"的匹配结果，分段控件要实现刷新选中态
        OnPropertyChanged(nameof(IsRangeToday));
        OnPropertyChanged(nameof(IsRangeYesterday));
        LoadCountsForDate(value);

        // 自定义单日模式下，范围统计跟随所选日期
        if (SelectedRange == StatsRange.Custom)
            RefreshRangeStatistics();
    }

    // ────────────── 日期切换 ──────────────
    // 替代原先的原生 Calendar 控件（外观与设计系统冲突，且 Calendar 是
    // WPF 里样式化成本最高的控件之一）。三键式覆盖"看昨天/前天"这类高频操作。

    /// <summary>形如 "10-08 周三"；当天额外标注。</summary>
    public string DateDisplayText =>
        SelectedDate.Date == DateTime.Today
            ? $"今天 · {SelectedDate:MM-dd}"
            : $"{SelectedDate:MM-dd} {WeekdayName(SelectedDate.DayOfWeek)}";

    /// <summary>不允许翻到未来（未来的数据必然为空）。</summary>
    public bool CanGoNext => SelectedDate.Date < DateTime.Today;

    [RelayCommand]
    private void PreviousDay()
    {
        // 用箭头即进入"自定义单日"，分段控件随之取消高亮
        SelectedRange = StatsRange.Custom;
        SelectedDate = SelectedDate.AddDays(-1);
    }

    [RelayCommand]
    private void NextDay()
    {
        if (!CanGoNext)
            return;
        SelectedRange = StatsRange.Custom;
        SelectedDate = SelectedDate.AddDays(1);
    }

    [RelayCommand]
    private void GoToToday()
    {
        SelectedRange = StatsRange.Today;
        SelectedDate = DateTime.Today;
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

    private void LoadCountsForDate(DateTime date)
    {
        var dayCounts = _dbService.GetClickCountsByDate(date);
        SelectedDateLeftCount = dayCounts["Left"];
        SelectedDateMiddleCount = dayCounts["Middle"];
        SelectedDateRightCount = dayCounts["Right"];
    }

    public void RefreshDailyCounts()
    {
        var today = _dbService.GetClickCountsByDate(DateTime.Today);
        LeftClickToday = today["Left"];
        MiddleClickToday = today["Middle"];
        RightClickToday = today["Right"];
    }

    private DateTime _lastRefreshDate = DateTime.Today;

    /// <summary>跨天检测定时器（每 30 秒查一次日期是否变化）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _dayRolloverTimer;

    /// <summary>节流派发定时器（复用单实例，见 ScheduleUiFlush）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;

    // UI 更新节流：高频点击时不必每次都切到 UI 线程派发，
    // 合并为最多每 50ms 一次，避免 Dispatcher 队列积压导致界面卡顿。
    private static readonly TimeSpan UiThrottleInterval = TimeSpan.FromMilliseconds(50);
    private readonly object _uiGate = new();
    private int _pendingLeft;
    private int _pendingMiddle;
    private int _pendingRight;
    private bool _uiFlushScheduled;
    private DateTime _lastUiFlush = DateTime.MinValue;

    private void OnMouseClicked(string button)
    {
        if (!IsRecording)
            return;

        // 热路径：只做入队，不碰数据库、不切 UI 线程
        _buffer.Enqueue(new MouseClickRecord { Button = button, ClickTime = DateTime.Now });

        if (DateTime.Today != _lastRefreshDate)
        {
            CheckDayRollover();
            return;
        }

        // 累计到待刷新计数，按节流合并派发
        lock (_uiGate)
        {
            switch (button)
            {
                case "Left":
                    _pendingLeft++;
                    break;
                case "Middle":
                    _pendingMiddle++;
                    break;
                case "Right":
                    _pendingRight++;
                    break;
            }

            var now = DateTime.UtcNow;
            if (_uiFlushScheduled || (now - _lastUiFlush) < UiThrottleInterval)
                return;

            _uiFlushScheduled = true;
            _lastUiFlush = now;
        }

        ScheduleUiFlush();
    }

    /// <summary>
    /// 节流派发用的单次定时器（复用同一个实例，避免热路径反复 new 造成 GC 压力）。
    /// </summary>
    private void ScheduleUiFlush()
    {
        var delay = UiThrottleInterval - (DateTime.UtcNow - _lastUiFlush);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        _uiTimer.Stop(); // 重置计时，避免上一次的残留
        _uiTimer.Interval = delay;
        _uiTimer.Start();
    }

    private void FlushUiCounters()
    {
        int left;
        int middle;
        int right;
        lock (_uiGate)
        {
            left = _pendingLeft;
            middle = _pendingMiddle;
            right = _pendingRight;
            _pendingLeft = 0;
            _pendingMiddle = 0;
            _pendingRight = 0;
            _uiFlushScheduled = false;
            _lastUiFlush = DateTime.UtcNow;
        }

        if (left == 0 && middle == 0 && right == 0)
            return;

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            LeftClickCount += left;
            MiddleClickCount += middle;
            RightClickCount += right;
            LeftClickToday += left;
            MiddleClickToday += middle;
            RightClickToday += right;

            OnPropertyChanged(nameof(MouseTodayClicks));
            OnPropertyChanged(nameof(MouseTotalClicks));
        });

        // 节流窗口内可能又攒了新计数，继续排一次
        lock (_uiGate)
        {
            if (_pendingLeft == 0 && _pendingMiddle == 0 && _pendingRight == 0)
                return;
            _uiFlushScheduled = true;
        }
        ScheduleUiFlush();
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetRecordingAutoStart(false);
    }
}
