using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyboardHookService : IKeyboardHookService, IDisposable
{
    public event Action<string>? KeyPressed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly ILogger<KeyboardHookService> _logger;
    private LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // 记录当前正处于按下状态的键（名称）
    private readonly HashSet<string> _pressedKeys = new(StringComparer.Ordinal);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    public KeyboardHookService(ILogger<KeyboardHookService> logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    /// <summary>
    /// 安装低级键盘钩子。
    ///
    /// 与 <see cref="MouseClickHookService.Start"/> 对齐（上游此处两版实现不一致）：
    /// 1. 防重复：已安装时直接返回。否则重复调用会覆盖 <c>_hookId</c>，
    ///    旧句柄永久泄漏——它仍挂在钩子链上持续被调用，且 Stop() 只能卸载最新那个，
    ///    表现为「点了停止记录却仍在记录」。
    /// 2. 失败检测：SetWindowsHookEx 失败时抛异常。
    ///    上游实现静默忽略返回值，钩子没装上而界面仍显示「记录中」，
    ///    用户会以为在记录、实际零数据。
    /// </summary>
    public void Start()
    {
        if (_hookId != IntPtr.Zero)
            return; // 已有一个钩子在运行

        string? moduleName;
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            moduleName = curProcess.MainModule?.ModuleName;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法获取主模块名称，键盘钩子安装失败。", ex);
        }

        if (string.IsNullOrEmpty(moduleName))
            throw new InvalidOperationException("无法获取主模块名称，键盘钩子安装失败。");

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(moduleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.LogError("键盘钩子安装失败，Win32 错误码 {Error}", error);
            throw new InvalidOperationException($"SetWindowsHookEx 失败，错误代码：{error}");
        }

        _logger.LogInformation("键盘钩子已安装");
        // 钩子重建后清空按下状态，避免残留状态导致漏记（例如停止期间某个键未抬起）
        _pressedKeys.Clear();
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                string keyName = GetKeyNameFromScanCode(kb.vkCode, kb.scanCode, kb.flags);

                if (isKeyDown)
                {
                    // 如果该键已经处于按下状态，则为长按重复，直接忽略
                    if (_pressedKeys.Contains(keyName))
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);

                    _pressedKeys.Add(keyName);
                    KeyPressed?.Invoke(keyName);
                }
                else // isKeyUp
                {
                    _pressedKeys.Remove(keyName);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // 左右修饰键固定映射：GetKeyNameText 对左/右 Shift、Ctrl、Alt、Win 的命名依赖键盘布局
    // （部分布局自带 “Left/Right” 方位词，多数不区分），统一走这里保证左右稳定可区分。
    // Win 用 “Left Windows/Right Windows” 与正式版历史数据保持一致
    private static readonly Dictionary<int, string> ModifierKeyNames = new()
    {
        [0x5B] = "Left Windows", // VK_LWIN
        [0x5C] = "Right Windows", // VK_RWIN
        [0xA0] = "Shift", // VK_LSHIFT
        [0xA1] = "Right Shift", // VK_RSHIFT
        [0xA2] = "Ctrl", // VK_LCONTROL
        [0xA3] = "Right Ctrl", // VK_RCONTROL
        [0xA4] = "Alt", // VK_LMENU
        [0xA5] = "Right Alt", // VK_RMENU
    };

    // 把扫描码 + 扩展标志合成 GetKeyNameText 所需的参数
    private string GetKeyNameFromScanCode(int vkCode, int scanCode, int flags)
    {
        // 左右修饰键优先走固定映射，避免布局差异
        if (ModifierKeyNames.TryGetValue(vkCode, out var modifierName))
            return modifierName;

        // 如果设置了扩展位（flags & 1），需要给扫描码加上 0x100
        bool isExtended = (flags & 1) != 0;
        int lParamValue = (scanCode << 16) | (isExtended ? 0x1000000 : 0);

        var sb = new StringBuilder(256);
        int result = GetKeyNameText(lParamValue, sb, sb.Capacity);
        if (result > 0)
            return sb.ToString();

        // ── 回退 1：按虚拟键码查表 ──
        //
        // GetKeyNameText 只认扫描码。部分键盘（笔记本内置键盘、远程桌面会话、
        // 虚拟化环境）会给出 scanCode = 0x00，此时 GetKeyNameText 必然失败，
        // 而 vkCode 其实是完好的 —— 例如 "vk 0x25" 就是左方向键。
        //
        // 原先没有这一层回退，这些键全被记成 "Unknown (scan 0x00, vk 0x25)"
        // 这类技术串，既污染统计（"最高频按键"栏里显示一串十六进制），
        // 也无法归入任何有意义的类型。
        if (VirtualKeyNames.TryGetValue(vkCode, out var byVk))
            return byVk;

        // 扫描码 0x63 是多数笔记本 Fn 的扫描码
        if (scanCode == 0x63)
            return "Fn";

        _logger.LogWarning(
            "按键无法识别，扫描码 0x{Scan:X2}，虚拟键码 0x{Vk:X2}，标志 0x{Flags:X2}",
            scanCode,
            vkCode,
            flags
        );

        // 面向用户的名称，不暴露十六进制码值
        return "未识别键";
    }

    /// <summary>
    /// 虚拟键码 → 键名。用于扫描码不可用时的回退。
    /// 覆盖常用键；未列出的仍会走"未识别键"。
    /// </summary>
    private static readonly Dictionary<int, string> VirtualKeyNames = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Caps Lock",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "Page Up",
        [0x22] = "Page Down",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0x2C] = "Print Screen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x5B] = "Left Windows",
        [0x5C] = "Right Windows",
        [0x5D] = "Menu",
        [0x90] = "Num Lock",
        [0x91] = "Scroll Lock",
        // 小键盘
        [0x60] = "Num 0",
        [0x61] = "Num 1",
        [0x62] = "Num 2",
        [0x63] = "Num 3",
        [0x64] = "Num 4",
        [0x65] = "Num 5",
        [0x66] = "Num 6",
        [0x67] = "Num 7",
        [0x68] = "Num 8",
        [0x69] = "Num 9",
        [0x6A] = "Num *",
        [0x6B] = "Num +",
        [0x6D] = "Num -",
        [0x6E] = "Num .",
        [0x6F] = "Num /",
        // 功能键
        [0x70] = "F1",
        [0x71] = "F2",
        [0x72] = "F3",
        [0x73] = "F4",
        [0x74] = "F5",
        [0x75] = "F6",
        [0x76] = "F7",
        [0x77] = "F8",
        [0x78] = "F9",
        [0x79] = "F10",
        [0x7A] = "F11",
        [0x7B] = "F12",
        // 左右修饰键（未在 ModifierKeyNames 中命中时的兜底）
        [0xA0] = "Left Shift",
        [0xA1] = "Right Shift",
        [0xA2] = "Left Ctrl",
        [0xA3] = "Right Ctrl",
        [0xA4] = "Left Alt",
        [0xA5] = "Right Alt",
        // OEM 符号键
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    // 结构体定义
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    // P/Invoke
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose() => Stop();
}
