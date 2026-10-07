using System;
using System.Collections.Generic;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 负责点击记录的持久化存储（SQLite 等）。
/// </summary>
public interface IClickDatabaseService
{
    /// <summary>
    /// 单条写入（仅用于非热路径）。热路径请使用 <see cref="IMouseClickBuffer"/>。
    /// </summary>
    void SaveClick(MouseClickRecord record);

    /// <summary>
    /// 批量写入：单连接 + 单事务。
    /// </summary>
    void SaveClickBatch(IReadOnlyList<MouseClickRecord> records);

    /// <summary>
    /// 获取各鼠标按键的累计点击次数。
    /// </summary>
    /// <returns>键为按键名称（"Left","Middle","Right"），值为累计次数</returns>
    Dictionary<string, int> GetClickCounts();

    Dictionary<string, int> GetClickCountsByDate(DateTime date);

    /// <summary>
    /// 指定区间（[from, to)）内各鼠标按键的点击次数。
    /// 用于"近 7 天 / 近 30 天"这类时间范围统计。
    /// </summary>
    Dictionary<string, int> GetClickCountsInRange(DateTime from, DateTime to);
}
