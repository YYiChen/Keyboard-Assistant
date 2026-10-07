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

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private ObservableCollection<AppUsageItem> _appUsageList = new();

    /// <summary>当天所有应用的总时长文本（用于"这几个应用加起来多久"）。</summary>
    [ObservableProperty]
    private string _totalUsageText = "—";

    /// <summary>当天出现过的应用数量。</summary>
    [ObservableProperty]
    private int _appCount;

    /// <summary>占用时间最多的应用名（一眼看出今天主要在用哪个软件）。</summary>
    [ObservableProperty]
    private string _topAppText = "—";

    private volatile bool _isRefreshing;

    private const int RefreshIntervalSeconds = 2;

    public AppUsageViewModel(ILogger<AppUsageViewModel> logger, AppInfoResolver appInfo)
    {
        _logger = logger;
        _appInfo = appInfo;
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

            // ── 派生指标：总时长 / 应用数 / 占比 ──
            long totalSeconds = list.Sum(x => x.TotalSeconds);

            foreach (var item in list)
                item.Percent = totalSeconds > 0 ? item.TotalSeconds * 100.0 / totalSeconds : 0;

            AppUsageList = new ObservableCollection<AppUsageItem>(list);

            TotalUsageText = FormatTotal(totalSeconds);
            AppCount = list.Count;
            TopAppText = list.Count > 0 ? list[0].DisplayName : "—";
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
                        CASE WHEN EndTime IS NULL 
                            THEN AccumulatedSeconds + (julianday('now','localtime') - julianday(COALESCE(LastUpdateTime, StartTime))) * 86400
                            ELSE AccumulatedSeconds
                        END
                    ) AS Seconds,
                    MIN(StartTime) AS StartTime,
                    MAX(COALESCE(EndTime, datetime('now','localtime'))) AS EndTime
                FROM ProcessSession
                WHERE Date = $date
                GROUP BY ProcessName
                ORDER BY Seconds DESC";
                cmd.Parameters.AddWithValue("$date", dateStr);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var seconds = (long)Math.Round(reader.GetDouble(1));
                    var startStr = reader.GetString(2);
                    var endStr = reader.GetString(3);

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
