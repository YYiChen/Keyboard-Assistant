using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using XAssistant.Models;

// 本工程同时引用 WinForms 与 WPF，两者都有 Color / Colors。
// 这里显式指定用 WPF 的一套，避免 CS0104 二义性。
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace XAssistant.Services;

/// <summary>
/// 键盘热力图：把按键计数映射成一张真实排布的键盘。
///
/// 布局取 ANSI 主键区（数字行 → ZXCV 行 → 修饰键行），不含 F 区与数字小键盘 ——
/// 这两块在实际输入中占比极低，加进来会让整图变得很宽、每块都很小。
///
/// 着色用「浅蓝 → 深蓝」的连续色阶，强度按当前范围的最大值归一化。
/// 未使用的键保持中性底色而不是留空，否则键盘轮廓会散掉、看不出形状。
/// </summary>
public static class KeyboardHeatmap
{
    // ── 布局常量（单位：标准键宽）──
    private const double StandardKey = 1.0;

    // ── 绘制尺寸（像素）──
    /// <summary>一个标准键的宽度（像素）。</summary>
    public const double UnitPx = 42;

    /// <summary>键帽高度（像素）。</summary>
    public const double KeyHeightPx = 40;

    /// <summary>键与键之间的间隙（像素）。</summary>
    public const double GapPx = 4;

    /// <summary>
    /// 主键区键位表。每项：(标签, 行, 起始X, 宽度, 匹配别名…)
    /// 别名用于兼容数据库里可能出现的不同写法（如 Enter / Return）。
    /// </summary>
    private static readonly (string Label, int Row, double X, double Width, string[] Aliases)[] Layout =
    {
        // ── 行 0：数字行 ──
        ("`", 0, 0, 1, new[] { "`", "OemTilde", "~" }),
        ("1", 0, 1, 1, new[] { "1", "D1" }),
        ("2", 0, 2, 1, new[] { "2", "D2" }),
        ("3", 0, 3, 1, new[] { "3", "D3" }),
        ("4", 0, 4, 1, new[] { "4", "D4" }),
        ("5", 0, 5, 1, new[] { "5", "D5" }),
        ("6", 0, 6, 1, new[] { "6", "D6" }),
        ("7", 0, 7, 1, new[] { "7", "D7" }),
        ("8", 0, 8, 1, new[] { "8", "D8" }),
        ("9", 0, 9, 1, new[] { "9", "D9" }),
        ("0", 0, 10, 1, new[] { "0", "D0" }),
        ("-", 0, 11, 1, new[] { "-", "OemMinus" }),
        ("=", 0, 12, 1, new[] { "=", "OemPlus" }),
        ("⌫", 0, 13, 2, new[] { "Backspace", "⌫" }),

        // ── 行 1：QWERTY ──
        ("Tab", 1, 0, 1.5, new[] { "Tab" }),
        ("Q", 1, 1.5, 1, new[] { "Q" }),
        ("W", 1, 2.5, 1, new[] { "W" }),
        ("E", 1, 3.5, 1, new[] { "E" }),
        ("R", 1, 4.5, 1, new[] { "R" }),
        ("T", 1, 5.5, 1, new[] { "T" }),
        ("Y", 1, 6.5, 1, new[] { "Y" }),
        ("U", 1, 7.5, 1, new[] { "U" }),
        ("I", 1, 8.5, 1, new[] { "I" }),
        ("O", 1, 9.5, 1, new[] { "O" }),
        ("P", 1, 10.5, 1, new[] { "P" }),
        ("[", 1, 11.5, 1, new[] { "[", "OemOpenBrackets" }),
        ("]", 1, 12.5, 1, new[] { "]", "OemCloseBrackets" }),
        ("\\", 1, 13.5, 1.5, new[] { "\\", "OemPipe" }),

        // ── 行 2：ASDF ──
        ("Caps", 2, 0, 1.75, new[] { "Caps Lock", "CapsLock", "Caps" }),
        ("A", 2, 1.75, 1, new[] { "A" }),
        ("S", 2, 2.75, 1, new[] { "S" }),
        ("D", 2, 3.75, 1, new[] { "D" }),
        ("F", 2, 4.75, 1, new[] { "F" }),
        ("G", 2, 5.75, 1, new[] { "G" }),
        ("H", 2, 6.75, 1, new[] { "H" }),
        ("J", 2, 7.75, 1, new[] { "J" }),
        ("K", 2, 8.75, 1, new[] { "K" }),
        ("L", 2, 9.75, 1, new[] { "L" }),
        (";", 2, 10.75, 1, new[] { ";", "OemSemicolon" }),
        ("'", 2, 11.75, 1, new[] { "'", "OemQuotes" }),
        ("Enter", 2, 12.75, 2.25, new[] { "Enter", "Return", "⏎" }),

        // ── 行 3：ZXCV ──
        ("Shift", 3, 0, 2.25, new[] { "Left Shift", "Shift", "LShift" }),
        ("Z", 3, 2.25, 1, new[] { "Z" }),
        ("X", 3, 3.25, 1, new[] { "X" }),
        ("C", 3, 4.25, 1, new[] { "C" }),
        ("V", 3, 5.25, 1, new[] { "V" }),
        ("B", 3, 6.25, 1, new[] { "B" }),
        ("N", 3, 7.25, 1, new[] { "N" }),
        ("M", 3, 8.25, 1, new[] { "M" }),
        (",", 3, 9.25, 1, new[] { ",", "OemComma" }),
        (".", 3, 10.25, 1, new[] { ".", "OemPeriod" }),
        ("/", 3, 11.25, 1, new[] { "/", "OemQuestion" }),
        ("Shift", 3, 12.25, 2.75, new[] { "Right Shift", "RShift" }),

        // ── 行 4：修饰键 ──
        ("Ctrl", 4, 0, 1.25, new[] { "Left Ctrl", "Ctrl", "LCtrl" }),
        ("Win", 4, 1.25, 1.25, new[] { "Left Windows", "Win", "LWin" }),
        ("Alt", 4, 2.5, 1.25, new[] { "Left Alt", "Alt", "LAlt" }),
        ("Space", 4, 3.75, 6.25, new[] { "Space", " " }),
        ("Alt", 4, 10, 1.25, new[] { "Right Alt", "RAlt" }),
        ("Win", 4, 11.25, 1.25, new[] { "Right Windows", "RWin" }),
        ("Menu", 4, 12.5, 1.25, new[] { "Menu", "Apps" }),
        ("Ctrl", 4, 13.75, 1.25, new[] { "Right Ctrl", "RCtrl" }),
    };

    /// <summary>键色的浅端与深端。</summary>
    private static readonly Color LowColor = Color.FromRgb(0xEF, 0xF6, 0xFF);   // 极浅蓝
    private static readonly Color HighColor = Color.FromRgb(0x1D, 0x4E, 0xD8);  // 深蓝

    /// <summary>键色的中性底色（未使用的键 —— 不能留空，否则键盘轮廓会散掉）。</summary>
    private static readonly Color NeutralColor = Color.FromRgb(0xF3, 0xF5, 0xF8);

    /// <summary>深色底时文字转白，保证可读。</summary>
    private const double DarkTextThreshold = 0.55;

    /// <summary>
    /// 由按键计数生成键盘热力图。
    /// </summary>
    /// <param name="counts">键名 → 次数。</param>
    /// <returns>键位列表；顺序与布局表一致，便于稳定渲染。</returns>
    public static List<KeyboardKey> Build(IReadOnlyDictionary<string, int> counts)
    {
        // 建别名 → 次数 的查找表（大小写不敏感，兼容不同来源的键名写法）
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in counts)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            lookup[key.Trim()] = lookup.TryGetValue(key.Trim(), out int existing)
                ? existing + value
                : value;
        }

        int max = 0;
        var result = new List<KeyboardKey>(Layout.Length);

        foreach (var (label, row, x, width, aliases) in Layout)
        {
            int count = 0;
            foreach (var alias in aliases)
            {
                if (lookup.TryGetValue(alias, out int v))
                    count += v;
            }

            if (count > max)
                max = count;

            result.Add(
                new KeyboardKey
                {
                    Label = label,
                    Row = row,
                    X = x,
                    Width = width,
                    Count = count,
                    Aliases = aliases,
                }
            );
        }

        // 归一化并着色
        foreach (var key in result)
        {
            key.Intensity = max > 0 ? (double)key.Count / max : 0;
            key.Fill = new SolidColorBrush(Mix(key.Intensity));
            key.Foreground = new SolidColorBrush(
                key.Intensity >= DarkTextThreshold ? Colors.White : Color.FromRgb(0x0F, 0x17, 0x2A)
            );

            // 像素坐标：单位宽 → 像素，并留出键间间隙
            key.PixelX = key.X * (UnitPx + GapPx);
            key.PixelY = key.Row * (KeyHeightPx + GapPx);
            key.PixelWidth = key.Width * UnitPx + (key.Width - 1) * GapPx;
            key.PixelHeight = KeyHeightPx;
        }

        return result;
    }

    /// <summary>主键区总宽度（像素），供视图设定 Canvas 尺寸。</summary>
    public static double TotalWidthPx => TotalUnits * (UnitPx + GapPx) - GapPx;

    /// <summary>主键区总高度（像素）。</summary>
    public static double TotalHeightPx => TotalRows * (KeyHeightPx + GapPx) - GapPx;

    /// <summary>
    /// 按强度取色：0 → 中性底色，中间过渡到浅蓝，最大 → 深蓝。
    ///
    /// 没有直接在中性色与深蓝之间插值，是因为那样"用了一两次"的键会立刻显得很深，
    /// 与"用了很多次"区分不开。先过一遍浅蓝可以拉开低段的层次。
    /// </summary>
    private static Color Mix(double intensity)
    {
        if (intensity <= 0)
            return NeutralColor;

        // 低段（0–0.15）：中性 → 浅蓝
        if (intensity < 0.15)
        {
            double t = intensity / 0.15;
            return Lerp(NeutralColor, LowColor, t);
        }

        // 其余：浅蓝 → 深蓝
        double t2 = (intensity - 0.15) / 0.85;
        return Lerp(LowColor, HighColor, t2);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t)
        );
    }

    /// <summary>主键区总宽度（单位：标准键宽），供视图计算缩放。</summary>
    public static double TotalUnits => Layout.Max(k => k.X + k.Width);

    /// <summary>主键区总行数。</summary>
    public static int TotalRows => Layout.Max(k => k.Row) + 1;

    /// <summary>统计有多少按键没有被布局覆盖 —— 用于提示"未识别键未上色"。</summary>
    public static int CountUnmapped(IReadOnlyDictionary<string, int> counts)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, _, _, aliases) in Layout)
        foreach (var alias in aliases)
            known.Add(alias);

        return counts.Keys.Count(k => !known.Contains(k.Trim()));
    }
}
