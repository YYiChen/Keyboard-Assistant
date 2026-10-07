using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace XAssistant.Services;

/// <summary>
/// 活动数据读取：把键盘与鼠标两个库的"有活动的分钟"合并起来。
///
/// 只取分钟级（<c>substr(PressTime, 1, 16)</c>）而非每一条原始记录：
/// 一天可能有数千条按键，全部取出再解析成 DateTime 开销不小，
/// 而活跃时长的判定阈值是 5 分钟 —— 分钟级精度完全够用，
/// 去重后通常只剩几百行。
/// </summary>
public class ActivityDataService
{
    private readonly string _keyDbPath;
    private readonly string _clickDbPath;

    public ActivityDataService()
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        _keyDbPath = Path.Combine(folder, "key_data.db");
        _clickDbPath = Path.Combine(folder, "click_data.db");
    }

    /// <summary>某日所有有输入活动的分钟（键与鼠合并、已去重、已排序）。</summary>
    public List<DateTime> GetActiveMinutes(DateTime date) =>
        GetActiveMinutes(date, date.AddDays(1));

    /// <summary>指定区间（[from, to)）内所有有输入活动的分钟。</summary>
    public List<DateTime> GetActiveMinutes(DateTime from, DateTime to)
    {
        var set = new HashSet<DateTime>();

        // 存储格式为 "o"（如 "2026-10-08T00:10:48.07+08:00"），
        // 前 16 个字符即 "yyyy-MM-ddTHH:mm"，字典序等于时间序，
        // 因此范围比较可直接走索引。
        string fromKey = from.Date.ToString("yyyy-MM-dd") + "T00:00";
        string toKey = to.Date.ToString("yyyy-MM-dd") + "T00:00";

        CollectMinutes(_keyDbPath, "KeyPressRecords", "PressTime", fromKey, toKey, set);
        CollectMinutes(_clickDbPath, "ClickRecords", "ClickTime", fromKey, toKey, set);

        var list = new List<DateTime>(set);
        list.Sort();
        return list;
    }

    /// <summary>某日的活跃时长（相邻活动分钟间隔 ≤ 5 分钟视为同一段）。</summary>
    public TimeSpan GetActiveDuration(DateTime date) =>
        ActivityCalculator.CalculateActiveDuration(GetActiveMinutes(date));

    /// <summary>指定区间的活跃时长。</summary>
    public TimeSpan GetActiveDuration(DateTime from, DateTime to) =>
        ActivityCalculator.CalculateActiveDuration(GetActiveMinutes(from, to));

    /// <summary>某日的活跃段（用于统计段数、最长连续时长）。</summary>
    public List<ActivityCalculator.ActivitySegment> GetSegments(DateTime date) =>
        ActivityCalculator.SplitSegments(GetActiveMinutes(date));

    /// <summary>指定区间的活跃段。</summary>
    public List<ActivityCalculator.ActivitySegment> GetSegments(DateTime from, DateTime to) =>
        ActivityCalculator.SplitSegments(GetActiveMinutes(from, to));

    /// <summary>
    /// 近 N 天（含今日）每日活跃时长，按日期升序。缺数据的日子返回 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    public List<(DateTime Date, TimeSpan Duration)> GetDailyDurations(int days)
    {
        var list = new List<(DateTime, TimeSpan)>();
        if (days <= 0)
            return list;

        var start = DateTime.Today.AddDays(-(days - 1));
        for (int i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            list.Add((d, GetActiveDuration(d)));
        }
        return list;
    }

    private static void CollectMinutes(
        string dbPath,
        string table,
        string column,
        string from,
        string to,
        HashSet<DateTime> into
    )
    {
        if (!File.Exists(dbPath))
            return; // 尚未产生该库，属正常（例如只用了键盘没点鼠标）

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $@"
                SELECT DISTINCT substr({column}, 1, 16)
                FROM {table}
                WHERE {column} >= @from AND {column} < @to";
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (DateTime.TryParse(reader.GetString(0), out var dt))
                    into.Add(dt);
            }
        }
        catch
        {
            // 单个库读取失败不应让整个统计崩掉 —— 另一个库的数据仍有价值
        }
    }
}
