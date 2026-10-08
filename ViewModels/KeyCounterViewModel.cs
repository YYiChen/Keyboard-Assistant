using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using WpfApplication = System.Windows.Application;

namespace XAssistant.ViewModels;

public partial class KeyCounterViewModel : ViewModelBase
{
    private readonly IKeyboardHookService _hookService;
    private readonly IKeyDatabaseService _dbService;
    private readonly IConfigurationService _configService;
    private readonly IKeyPressBuffer _buffer;
    private DateTime _currentDate = DateTime.Today;

    /// <summary>跨天检测定时器（每 30 秒查一次日期是否变化）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _dayRolloverTimer;

    /// <summary>节流派发定时器（复用单实例，见 ScheduleUiFlush）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;

    // UI 更新节流：合并为最多每 50ms 一次，避免高频按键把 Dispatcher 队列打满。
    private static readonly TimeSpan UiThrottleInterval = TimeSpan.FromMilliseconds(50);
    private readonly object _uiGate = new();
    private readonly Dictionary<string, int> _pendingKeys = new();
    private bool _uiFlushScheduled;
    private DateTime _lastUiFlush = DateTime.MinValue;

    // 总计
    public ObservableCollection<KeyCountItem> KeyCounts { get; } = new();

    // 今天
    public ObservableCollection<KeyCountItem> TodayKeyCounts { get; } = new();

    // 昨天
    public ObservableCollection<KeyCountItem> YesterdayKeyCounts { get; } = new();

    // 前天
    public ObservableCollection<KeyCountItem> DayBeforeYesterdayKeyCounts { get; } = new();

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private int _selectedTabIndex;

    // ===== 新增：首页用聚合属性 =====
    public int KeyTodayPresses => TodayKeyCounts.Sum(item => item.Count);
    public int KeyTotalPresses => KeyCounts.Sum(item => item.Count);

    public string KeyRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush KeyRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public KeyCounterViewModel(
        IKeyboardHookService hookService,
        IKeyDatabaseService dbService,
        IConfigurationService configService,
        IKeyPressBuffer buffer
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;
        _buffer = buffer;

        // 先建好节流定时器：StartRecording() 之后随时可能有按键回调进来
        _uiTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            WpfApplication.Current.Dispatcher
        );
        _uiTimer.Tick += (_, _) =>
        {
            _uiTimer.Stop(); // 单次触发
            FlushUiCounters();
        };

        _hookService.KeyPressed += OnKeyPressed;

        LoadAllCounts();

        // 跨天自动刷新。
        // 本程序常驻后台（开机自启、静默运行），必然跨越自然日。
        // 而「今日按键」原先只在 OnKeyPressed 的跨天分支刷新 ——
        // 跨天后若用户只打开界面查看而不按键，会一直显示昨天的数字当作「今日」。
        _dayRolloverTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _dayRolloverTimer.Tick += (_, _) => CheckDayRollover();
        _dayRolloverTimer.Start();

        if (_configService.GetKeyRecordingAutoStart())
        {
            StartRecording();
        }
    }

    /// <summary>
    /// 检测是否跨天；跨天则重建各时段统计并把待刷计数清零。
    ///
    /// 清空 <c>_pendingKeys</c> 是必要的：这些计数对应的记录已入队、会正常落库，
    /// 但 <see cref="ReloadAllCountsCore"/> 会用数据库值重建「今日」集合。
    /// 若不清空，残留的旧计数会在下一次 flush 时被 <c>+=</c> 到新的一天，造成
    /// 「今天的数字里混进昨天的按键」。
    /// </summary>
    private void CheckDayRollover()
    {
        if (DateTime.Today <= _currentDate)
            return;

        _currentDate = DateTime.Today;
        lock (_uiGate)
        {
            _pendingKeys.Clear();
        }
        LoadAllCounts();
    }

    // IsRecording 变化时通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyRecordingStatus));
        OnPropertyChanged(nameof(KeyRecordingColor));
    }

    private void OnKeyPressed(string key)
    {
        // 热路径：只入队。数据库写入由后台批量刷盘完成，
        // 绝不在低级钩子回调里做 I/O（原实现会导致输入延迟）。
        _buffer.Enqueue(new Models.KeyPressRecord { Key = key, PressTime = DateTime.Now });

        // 检测是否跨天（与定时器共用同一逻辑）
        if (DateTime.Today > _currentDate)
        {
            CheckDayRollover();
            return;
        }

        // 累计到待刷新字典，按节流合并派发
        lock (_uiGate)
        {
            _pendingKeys.TryGetValue(key, out var n);
            _pendingKeys[key] = n + 1;

            var now = DateTime.UtcNow;
            if (_uiFlushScheduled || (now - _lastUiFlush) < UiThrottleInterval)
                return;

            _uiFlushScheduled = true;
            _lastUiFlush = now;
        }

        ScheduleUiFlush();
    }

    /// <summary>
    /// 节流派发用的单次定时器（复用同一个实例）。
    ///
    /// 原实现在每次调度时 <c>new DispatcherTimer</c>：高频输入下每 50ms 就新建一个，
    /// 一天可产生上百万个短命对象，纯属 GC 压力。复用单实例没有副作用 ——
    /// <c>_uiFlushScheduled</c> 已保证同一时刻至多只有一个待刷新任务。
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
        Dictionary<string, int> pending;
        lock (_uiGate)
        {
            if (_pendingKeys.Count == 0)
            {
                _uiFlushScheduled = false;
                return;
            }
            pending = new Dictionary<string, int>(_pendingKeys);
            _pendingKeys.Clear();
            _uiFlushScheduled = false;
            _lastUiFlush = DateTime.UtcNow;
        }

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            foreach (var kv in pending)
            {
                UpdateCollection(KeyCounts, kv.Key, kv.Value);
                UpdateCollection(TodayKeyCounts, kv.Key, kv.Value);
            }

            OnPropertyChanged(nameof(KeyTodayPresses));
            OnPropertyChanged(nameof(KeyTotalPresses));
        });

        // 节流窗口内可能又攒了新计数，继续排一次
        lock (_uiGate)
        {
            if (_pendingKeys.Count == 0)
                return;
            _uiFlushScheduled = true;
        }
        ScheduleUiFlush();
    }

    private void UpdateCollection(
        ObservableCollection<KeyCountItem> collection,
        string key,
        int delta
    )
    {
        var item = collection.FirstOrDefault(x => x.Key == key);
        if (item != null)
            item.Count += delta;
        else
            collection.Add(new KeyCountItem { Key = key, Count = delta });
    }

    /// <summary>
    /// 重新加载各时间段统计。
    ///
    /// 注意：这里用 <c>Dispatcher.InvokeAsync</c> 而非 <c>Invoke</c>。
    /// <c>Invoke</c> 是同步阻塞的，而本方法由 OnKeyPressed 触发 —— 后者运行在
    /// 低级键盘钩子回调线程上。若在这里同步等 UI 线程执行 4 次数据库聚合查询，
    /// 跨天那一刻会把 UI 线程卡住，直接体现为「键盘突然卡一下」。
    /// 数据量越大（当前已有数千行）越明显。
    /// </summary>
    private void LoadAllCounts()
    {
        WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                ReloadAllCountsCore();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"重新加载按键统计失败: {ex.Message}");
            }
        });
    }

    private void ReloadAllCountsCore()
    {
        // 总计
        var totalDict = _dbService.GetKeyCounts();
        KeyCounts.Clear();
        foreach (var kv in totalDict.OrderByDescending(x => x.Value))
            KeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 今天
        var todayDict = _dbService.GetKeyCounts(DateTime.Today, DateTime.Today.AddDays(1));
        TodayKeyCounts.Clear();
        foreach (var kv in todayDict.OrderByDescending(x => x.Value))
            TodayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 昨天
        var yesterdayDict = _dbService.GetKeyCounts(DateTime.Today.AddDays(-1), DateTime.Today);
        YesterdayKeyCounts.Clear();
        foreach (var kv in yesterdayDict.OrderByDescending(x => x.Value))
            YesterdayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 前天
        var dayBeforeDict = _dbService.GetKeyCounts(
            DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-1)
        );
        DayBeforeYesterdayKeyCounts.Clear();
        foreach (var kv in dayBeforeDict.OrderByDescending(x => x.Value))
            DayBeforeYesterdayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 通知聚合属性更新
        OnPropertyChanged(nameof(KeyTodayPresses));
        OnPropertyChanged(nameof(KeyTotalPresses));

        RefreshStatistics();
    }

    // ══════════════ 时间范围 ══════════════

    /// <summary>
    /// 当前统计范围。切换后指标卡、类型分布、高频排行与明细表都会按新范围重取数据。
    /// （24 小时时段图除外 —— 它表达的是"一天之内的时间分布"，跨天没有意义。）
    /// </summary>
    [ObservableProperty]
    private StatsRange _selectedRange = StatsRange.Today;

    partial void OnSelectedRangeChanged(StatsRange value)
    {
        // 四个互斥单选按钮需要跟着刷新选中态
        OnPropertyChanged(nameof(IsRangeToday));
        OnPropertyChanged(nameof(IsRangeYesterday));
        OnPropertyChanged(nameof(IsRangeLast7Days));
        OnPropertyChanged(nameof(IsRangeLast30Days));
        OnPropertyChanged(nameof(RangeLabel));
        LoadAllCounts();
    }

    // 供 RadioButton 绑定（同一 GroupName 保证互斥，这里只负责把选中态映射到枚举）
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

    /// <summary>当前范围名称，供卡片与图表引用。</summary>
    public string RangeLabel => SelectedRange.Label();

    /// <summary>范围内的按键总数。</summary>
    [ObservableProperty]
    private int _rangeTotal;

    /// <summary>范围内有按键记录的天数。</summary>
    [ObservableProperty]
    private int _rangeActiveDays;

    /// <summary>范围内日均按键文本（单日范围时显示"—"，因为日均没有意义）。</summary>
    [ObservableProperty]
    private string _rangeDailyAverageText = "—";

    // ══════════════ 统计（"数据分析"部分） ══════════════

    /// <summary>今日有按键的不同分钟数。</summary>
    [ObservableProperty]
    private int _todayActiveMinutes;

    /// <summary>
    /// 今日平均按键频率（键/分钟）。
    ///
    /// 定义为「今日按键数 ÷ 活跃分钟数」，其中活跃分钟 = 有按键记录的不同分钟。
    /// 这不是峰值打字速度，而是把"在用的那些分钟"平均下来的节奏 ——
    /// 比"全天 1440 分钟"作分母更有意义（后者会被睡眠、开会等空闲时间稀释到一个无用的数字）。
    /// </summary>
    [ObservableProperty]
    private double _todayKpm;

    /// <summary>活跃时长的可读文本。</summary>
    [ObservableProperty]
    private string _todayActiveText = "—";

    /// <summary>今日最高频的键及其次数文本。</summary>
    [ObservableProperty]
    private string _topKeyText = "—";

    /// <summary>今日相对昨日的变化文本（如 "+12%" / "−8%" / "持平"）。</summary>
    [ObservableProperty]
    private string _todayDeltaText = string.Empty;

    /// <summary>今日比昨天多还是少（决定变化文本的着色）。</summary>
    [ObservableProperty]
    private bool _todayIsUp;

    /// <summary>近 7 日趋势（含今日），用于迷你柱状图。</summary>
    public ObservableCollection<DailyKeyCount> WeeklyTrend { get; } = new();

    /// <summary>24 小时分布。</summary>
    public ObservableCollection<HourBar> HourlyBars { get; } = new();

    /// <summary>
    /// 键盘热力图：按真实排布绘制键位，颜色随使用频次加深（v1.3.0 新增）。
    ///
    /// 与「高频按键 Top10」互补 —— 排行只能列出前几名，
    /// 热力图能一眼看出整块键盘的"重心"落在哪里（例如左手区 vs 右手区）。
    /// </summary>
    public ObservableCollection<KeyboardKey> KeyboardKeys { get; } = new();

    /// <summary>当前热力图上有按键记录的键位数（用于说明数据完整度）。</summary>
    [ObservableProperty]
    private int _heatmapUsedKeyCount;

    /// <summary>按键类型分布。</summary>
    public ObservableCollection<KeyCategoryItem> CategoryBreakdown { get; } = new();

    /// <summary>高频按键 Top 10。</summary>
    public ObservableCollection<KeyRankItem> TopKeys { get; } = new();

    /// <summary>
    /// 计算今日的各项统计。
    /// 只在刷新时调用（不是每次按键），因此这里的数据库查询频率可接受。
    /// </summary>
    private void RefreshStatistics()
    {
        var (rangeFrom, rangeTo) = SelectedRange.ToDateRange();
        int rangeDays = SelectedRange.DayCount();

        // 归一化历史遗留的未识别键名（旧版本解析失败时写入的技术串）。
        // 变量名沿用 todayDict，但取数范围已随 SelectedRange 变化。
        var todayDict = NormalizeCounts(_dbService.GetKeyCounts(rangeFrom, rangeTo));
        int todayTotal = todayDict.Values.Sum();

        // ── 范围指标 ──
        RangeTotal = todayTotal;
        RangeActiveDays = _dbService.GetActiveDayCount(rangeFrom, rangeTo);
        // 单日范围下"日均"没有意义，改为说明平均频率的口径
        RangeDailyAverageText =
            rangeDays > 1 ? $"日均 {todayTotal / rangeDays:N0} 次" : "按有输入的分钟数平均";

        // ── 活跃时长与频率 ──
        TodayActiveMinutes = _dbService.GetActiveMinuteCount(rangeFrom, rangeTo);
        TodayKpm = TodayActiveMinutes > 0 ? Math.Round((double)todayTotal / TodayActiveMinutes, 1) : 0;
        TodayActiveText = FormatActiveDuration(TodayActiveMinutes);

        // ── 最高频键 ──
        var topOne = todayDict.OrderByDescending(x => x.Value).FirstOrDefault();
        TopKeyText = topOne.Key is null ? "—" : $"{topOne.Key}（{topOne.Value:N0}）";

        // ── 较昨日变化（仅"今天"范围下有对比意义） ──
        int yesterdayTotal = YesterdayKeyCounts.Sum(x => x.Count);
        if (SelectedRange != StatsRange.Today)
        {
            TodayDeltaText = string.Empty;
        }
        else if (yesterdayTotal == 0 && todayTotal == 0)
        {
            TodayDeltaText = string.Empty;
        }
        else if (yesterdayTotal == 0)
        {
            TodayDeltaText = "昨日无记录";
            TodayIsUp = true;
        }
        else
        {
            double pct = (todayTotal - yesterdayTotal) * 100.0 / yesterdayTotal;
            TodayIsUp = pct >= 0;
            TodayDeltaText = Math.Abs(pct) < 0.5
                ? "与昨日持平"
                : $"较昨日 {(pct > 0 ? "+" : "−")}{Math.Abs(pct):F0}%";
        }

        // ── 24 小时分布 ──
        var hourly = _dbService.GetHourlyCounts(DateTime.Today);
        int peak = hourly.Length > 0 ? hourly.Max() : 0;
        int currentHour = DateTime.Now.Hour;

        HourlyBars.Clear();
        for (int h = 0; h < 24; h++)
        {
            HourlyBars.Add(new HourBar
            {
                Hour = h,
                Count = hourly[h],
                // 归一化到像素高度；有数据时给一个最小可见高度，避免小数值看不出来
                Height = peak > 0 && hourly[h] > 0
                    ? Math.Max(3, hourly[h] * HourBar.MaxHeight / peak)
                    : 0,
                Label = h % 3 == 0 ? h.ToString("D2") : string.Empty,
                IsCurrent = h == currentHour,
            });
        }

        // ── 键盘热力图 ──
        // 用同一份 todayDict（已按范围取数）构建，保证与上方指标卡口径一致。
        var heatmap = KeyboardHeatmap.Build(todayDict);

        KeyboardKeys.Clear();
        foreach (var key in heatmap)
            KeyboardKeys.Add(key);

        HeatmapUsedKeyCount = heatmap.Count(k => k.Count > 0);

        // ── 按键类型分布 ──
        var byCategory = todayDict
            .GroupBy(kv => CategorizeKey(kv.Key))
            .Select(g => new { Name = g.Key, Count = g.Sum(x => x.Value) })
            .OrderByDescending(x => x.Count)
            .ToList();

        CategoryBreakdown.Clear();
        foreach (var c in byCategory)
        {
            CategoryBreakdown.Add(new KeyCategoryItem
            {
                Name = c.Name,
                Count = c.Count,
                Percent = todayTotal > 0 ? c.Count * 100.0 / todayTotal : 0,
            });
        }

        // ── Top 10 排行 ──
        var ranked = todayDict.OrderByDescending(x => x.Value).Take(10).ToList();
        int topCount = ranked.Count > 0 ? ranked[0].Value : 0;

        TopKeys.Clear();
        foreach (var kv in ranked)
        {
            TopKeys.Add(new KeyRankItem
            {
                Key = kv.Key,
                Count = kv.Value,
                // 相对榜首的长度（不是占比）：这样榜首满格，其余按比例缩短，排行感更强
                Percent = topCount > 0 ? kv.Value * 100.0 / topCount : 0,
            });
        }

        // ── 近 7 日趋势 ──
        var weekly = _dbService.GetDailyTotals(7);
        int weekPeak = weekly.Count > 0 ? weekly.Max(d => d.Count) : 0;
        WeeklyTrend.Clear();
        foreach (var d in weekly)
        {
            WeeklyTrend.Add(new DailyKeyCount(d.Date, d.Count));
        }
        WeeklyTrendPeak = weekPeak == 0 ? 1 : weekPeak;
    }

    /// <summary>近 7 日中的峰值，供趋势图归一化（避免除零，最小取 1）。</summary>
    [ObservableProperty]
    private int _weeklyTrendPeak = 1;

    private static string FormatActiveDuration(int minutes)
    {
        if (minutes <= 0)
            return "—";
        return minutes < 60
            ? $"{minutes} 分钟"
            : $"{minutes / 60} 小时 {minutes % 60} 分";
    }

    /// <summary>
    /// 把历史遗留的未识别键名归并成一个统一名称。
    ///
    /// 旧版本的按键解析只依赖扫描码，遇到 scanCode=0x00（笔记本内置键盘、
    /// 远程桌面等）就写入形如 <c>Unknown (scan 0x00, vk 0x25)</c> 的技术串。
    /// 解析逻辑已修复（新增虚拟键码回退），但库里已有的记录不会自动改变 ——
    /// 若不在展示层归并，统计里仍会混着十六进制串，甚至占据"最高频按键"首位。
    /// </summary>
    private static string NormalizeKeyName(string key) =>
        key.StartsWith("Unknown (", StringComparison.Ordinal) ? "未识别键" : key;

    /// <summary>按归一化后的键名重新聚合计数。</summary>
    private static Dictionary<string, int> NormalizeCounts(Dictionary<string, int> source)
    {
        // 绝大多数情况下没有需要归并的键，直接复用避免多余分配
        if (!source.Keys.Any(k => k.StartsWith("Unknown (", StringComparison.Ordinal)))
            return source;

        var merged = new Dictionary<string, int>();
        foreach (var (key, count) in source)
        {
            string name = NormalizeKeyName(key);
            merged[name] = merged.TryGetValue(name, out int existing) ? existing + count : count;
        }
        return merged;
    }

    /// <summary>
    /// 把单个按键归入类别。
    ///
    /// 输入是钩子产出的键名（如 "A"、"2"、", "、"Ctrl"、"Left Windows"、"Num 1"、"F5"），
    /// 未识别的键形如 "Unknown (scan 0x00, vk 0xFC)"，归入"其他"。
    /// </summary>
    private static string CategorizeKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "其他";

        if (key.Length == 1)
        {
            if (char.IsLetter(key[0]))
                return "字母";
            return char.IsDigit(key[0]) ? "数字" : "符号";
        }

        // 修饰键（含 "Left Windows" / "Right Ctrl" 这类带方位前缀的写法）
        if (
            key.Contains("Ctrl", StringComparison.Ordinal)
            || key.Contains("Alt", StringComparison.Ordinal)
            || key.Contains("Shift", StringComparison.Ordinal)
            || key.Contains("Windows", StringComparison.Ordinal)
        )
            return "修饰键";

        if (key.StartsWith("Num ", StringComparison.Ordinal))
            return "小键盘";

        if (key is "Up" or "Down" or "Left" or "Right")
            return "方向键";

        if (
            key.Length >= 2
            && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out int fn)
            && fn is >= 1 and <= 24
        )
            return "功能键";

        if (
            key
                is "Space"
                    or "Enter"
                    or "Tab"
                    or "Backspace"
                    or "Delete"
                    or "Insert"
                    or "Home"
                    or "End"
                    or "Page Up"
                    or "Page Down"
                    or "Esc"
        )
            return "编辑键";

        return "其他";
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetKeyRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetKeyRecordingAutoStart(false);
    }

    [RelayCommand]
    private void RefreshData()
    {
        _currentDate = DateTime.Today;
        LoadAllCounts();
    }
}

// 辅助类，用于绑定
public partial class KeyCountItem : ObservableObject
{
    private int _count;
    public string Key { get; set; } = string.Empty;

    public int Length => Key?.Length ?? 0; // 用于排序

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}
