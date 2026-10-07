using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using XAssistant.Models;

namespace XAssistant.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly ClickCounterViewModel _clickCounter;
    private readonly KeyCounterViewModel _keyCounter;
    private readonly UsageViewModel _usage;
    private readonly AppUsageViewModel _appUsage;

    public HomeViewModel(
        ClickCounterViewModel clickCounter,
        KeyCounterViewModel keyCounter,
        UsageViewModel usage,
        AppUsageViewModel appUsage
    )
    {
        _clickCounter = clickCounter;
        _keyCounter = keyCounter;
        _usage = usage;
        _appUsage = appUsage;

        // 监听子 ViewModel 的属性变化，及时转发到自身同名的属性
        _clickCounter.PropertyChanged += OnClickCounterPropertyChanged;
        _keyCounter.PropertyChanged += OnKeyCounterPropertyChanged;
        _usage.PropertyChanged += OnUsagePropertyChanged;
        _appUsage.PropertyChanged += OnAppUsagePropertyChanged;
    }

    // ========== 鼠标点击模块 ==========
    public string MouseRecordingStatus => _clickCounter.MouseRecordingStatus;
    public System.Windows.Media.Brush MouseRecordingColor => _clickCounter.MouseRecordingColor;
    public int MouseTodayClicks => _clickCounter.MouseTodayClicks;
    public int MouseTotalClicks => _clickCounter.MouseTotalClicks;

    // ========== 键盘记录模块 ==========
    public string KeyRecordingStatus => _keyCounter.KeyRecordingStatus;
    public System.Windows.Media.Brush KeyRecordingColor => _keyCounter.KeyRecordingColor;
    public int KeyTodayPresses => _keyCounter.KeyTodayPresses;
    public int KeyTotalPresses => _keyCounter.KeyTotalPresses;

    // ========== 电脑使用模块 ==========
    // 假设 UsageViewModel 里有 TodayUsageText 属性（带格式化时长）
    public string TodayUsageText => _usage.TodayUsageText;

    // ========== 软件使用模块（修正点）==========
    // 注意：AppUsageViewModel 中的列表属性叫 AppUsageList，不是 TopApps
    public ObservableCollection<AppUsageItem> TopApps => _appUsage.AppUsageList;

    // ══════════════ 总览（跨模块综合） ══════════════
    //
    // 用户反馈：首页原来只是四个模块各报各的数字，看不出"整体今天怎么样"。
    // 这里把各模块的数据汇总成几个跨模块指标。

    /// <summary>今日输入总量 = 键盘按键 + 鼠标点击。</summary>
    public int TodayInputTotal => KeyTodayPresses + MouseTodayClicks;

    /// <summary>今日用过的应用数。</summary>
    public int TodayAppCount => _appUsage.AppCount;

    /// <summary>今日用时最多的应用。</summary>
    public string TopAppName => _appUsage.TopAppText;

    /// <summary>近 7 日活跃趋势（键鼠合并后的每日活跃时长，直接复用电脑使用页的数据源）。</summary>
    public ObservableCollection<DayBar> WeeklyActivity => _usage.WeeklyDurations;

    /// <summary>今日输入量的构成说明（如 "按键 1,350 · 点击 1,227"）。</summary>
    public string TodayInputBreakdown =>
        $"按键 {KeyTodayPresses:N0} · 点击 {MouseTodayClicks:N0}";

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
            case nameof(ClickCounterViewModel.MouseTodayClicks):
                OnPropertyChanged(nameof(MouseTodayClicks));
                // 总览指标依赖"按键 + 点击"，两个模块任一变化都要刷新
                OnPropertyChanged(nameof(TodayInputTotal));
                OnPropertyChanged(nameof(TodayInputBreakdown));
                break;
            case nameof(ClickCounterViewModel.MouseTotalClicks):
                OnPropertyChanged(nameof(MouseTotalClicks));
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
            case nameof(KeyCounterViewModel.KeyTodayPresses):
                OnPropertyChanged(nameof(KeyTodayPresses));
                OnPropertyChanged(nameof(TodayInputTotal));
                OnPropertyChanged(nameof(TodayInputBreakdown));
                break;
            case nameof(KeyCounterViewModel.KeyTotalPresses):
                OnPropertyChanged(nameof(KeyTotalPresses));
                break;
        }
    }

    private void OnUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsageViewModel.TodayUsageText))
            OnPropertyChanged(nameof(TodayUsageText));
    }

    private void OnAppUsagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 当 AppUsageViewModel 的 AppUsageList 重新赋值时，通知 TopApps 已变化
        if (e.PropertyName == nameof(AppUsageViewModel.AppUsageList))
        {
            OnPropertyChanged(nameof(TopApps));
            OnPropertyChanged(nameof(TodayAppCount));
            OnPropertyChanged(nameof(TopAppName));
        }
    }
}
