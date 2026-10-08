using System;
using System.Collections.Generic;
using System.Windows.Media;

// 工程同时引用 WinForms（托盘图标用）与 WPF，Brushes / Color 两边都有定义。
// 显式指定用 WPF 的一套，避免 CS0104 二义性。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace XAssistant.Models;

/// <summary>
/// 键盘热力图上的一个键位。
///
/// 布局用「单位宽」表达：1 单位 = 一个标准字母键的宽度。
/// 这样 Tab(1.5)、Caps(1.75)、Shift(2.25/2.75)、Space(6.25) 这些
/// 非标准宽度的键可以精确还原真实键盘的排布 —— 用 UniformGrid 做不到这点，
/// 它只能均分，画出来的"键盘"会失真。
/// </summary>
public sealed class KeyboardKey
{
    /// <summary>键帽文字。功能键用短标签（Shift / Ctrl / Space…）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>匹配用的键名（数据库里记录的键名），可为多个别名。</summary>
    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>行号（0 起）。</summary>
    public int Row { get; init; }

    /// <summary>该行内的起始位置，单位是"标准键宽"。</summary>
    public double X { get; init; }

    /// <summary>宽度，单位是"标准键宽"。</summary>
    public double Width { get; init; } = 1;

    /// <summary>该键在当前统计范围内的按键次数。</summary>
    public int Count { get; set; }

    /// <summary>按次数归一化后的强度 0–1（0 = 未使用，1 = 使用最多）。</summary>
    public double Intensity { get; set; }

    /// <summary>键帽填充色（由强度映射得到）。</summary>
    public Brush Fill { get; set; } = Brushes.Transparent;

    /// <summary>键帽文字颜色（深色底时转白，保证可读）。</summary>
    public Brush Foreground { get; set; } = Brushes.Black;

    /// <summary>悬停提示：显示具体次数。</summary>
    public string Tooltip =>
        Count > 0 ? $"{Label}　{Count:N0} 次" : $"{Label}　未使用";

    /// <summary>是否属于字母/数字等"主键区"字符键（用于区分着色策略）。</summary>
    public bool IsCharacterKey =>
        Label.Length == 1
        || (Label.Length == 2 && Label[0] == 'F' && char.IsDigit(Label[1]));

    // ── 绘制用坐标（由 KeyboardHeatmap 在生成时算好）──
    // 直接算成像素而非在 XAML 里做转换：Canvas.Left/Top 需要 double，
    // 用转换器会让绑定链变复杂，而这些值在构建时就能确定。

    /// <summary>键帽左边缘（像素）。</summary>
    public double PixelX { get; set; }

    /// <summary>键帽上边缘（像素）。</summary>
    public double PixelY { get; set; }

    /// <summary>键帽宽度（像素）。</summary>
    public double PixelWidth { get; set; }

    /// <summary>键帽高度（像素）。</summary>
    public double PixelHeight { get; set; }
}
