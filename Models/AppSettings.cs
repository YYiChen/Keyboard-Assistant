namespace XAssistant.Models;

public class AppSettings
{
    public RecordingSettings Recording { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

public class RecordingSettings
{
    // 个人自用默认：程序启动即自动开始记录，不需手动点「开始记录」。
    // 注意：这两个开关指的是「启动时自动开启录制」，与「开机自启注册表」是两件事，
    // 后者由 StartupService 管理（在 OnStartup 时自动注册）。
    public bool AutoStartRecording { get; set; } = true; // 鼠标点击录制自动启动
    public bool AutoStartKeyRecording { get; set; } = true; // 键盘按键录制自动启动
}

public class GeneralSettings
{
    // 启动时直接最小化到托盘，不弹主窗口（静默后台运行的前提）。
    // 托盘图标左键单击可恢复显示。
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// 是否已完成首次运行初始化。
    ///
    /// 用于让「开机自启」具备正确语义：首次运行默认开启（符合「开机即录」
    /// 的使用意图），此后不再由程序改写注册表，完全尊重用户在设置页的选择。
    ///
    /// 背景：此前每次启动都无条件 SetAutoStart(true)，用户在界面上取消勾选后
    /// 下次启动又被重新打开，设置项形同虚设。
    /// </summary>
    public bool FirstRunCompleted { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 720;
    public bool IsLogExpanded { get; set; } = false;
}
