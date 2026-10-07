using System;
using System.Collections.Generic;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

public interface IKeyDatabaseService
{
    /// <summary>
    /// 单条写入（仅用于非热路径，如测试或补录）。热路径请使用 <see cref="IKeyPressBuffer"/>。
    /// </summary>
    void SaveKeyPress(KeyPressRecord record);

    /// <summary>
    /// 批量写入：单连接 + 单事务，供后台刷盘使用。表结构不变，无需迁移历史数据。
    /// </summary>
    void SaveKeyPressBatch(IReadOnlyList<KeyPressRecord> records);

    Dictionary<string, int> GetKeyCounts();
    Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to);

    // ────────────── 统计查询 ──────────────
    // 时间条件一律用「字符串范围比较」而非 substr()/date() 函数：
    // 对列施加函数会让 idx_keypress_presstime 失效退化为全表扫描
    // （该问题在鼠标库上曾导致 78% 的归属误差 + 2.6 倍性能损失，已修复，此处沿用正确写法）。

    /// <summary>某日各小时的按键数，索引 0–23 对应 0–23 时。</summary>
    int[] GetHourlyCounts(DateTime date);

    /// <summary>指定区间内（[from, to)）有按键记录的不同分钟数。</summary>
    int GetActiveMinuteCount(DateTime from, DateTime to);

    /// <summary>近 N 天（含今日）每日按键总数，按日期升序返回。</summary>
    List<DailyKeyCount> GetDailyTotals(int days);

    /// <summary>指定区间内有按键记录的天数（用于"活跃天数"这类指标）。</summary>
    int GetActiveDayCount(DateTime from, DateTime to);
}
