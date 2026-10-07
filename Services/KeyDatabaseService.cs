using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyDatabaseService : IKeyDatabaseService
{
    private static readonly string ConnectionString = InitializeConnectionString();

    private static string InitializeConnectionString()
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(folder);
        string dbPath = Path.Combine(folder, "key_data.db");
        return $"Data Source={dbPath}";
    }

    public KeyDatabaseService()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            @"
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=256;
            CREATE TABLE IF NOT EXISTS KeyPressRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL,
                PressTime TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_keypress_presstime ON KeyPressRecords(PressTime);
            ";
        cmd.ExecuteNonQuery();
    }

    public void SaveKeyPress(KeyPressRecord record)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";
        cmd.Parameters.AddWithValue("@k", record.Key);
        cmd.Parameters.AddWithValue("@t", record.PressTime.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 批量写入：一次连接、一次事务、参数复用。
    /// 表结构与单条写入完全一致，历史数据零迁移。
    /// </summary>
    public void SaveKeyPressBatch(IReadOnlyList<KeyPressRecord> records)
    {
        if (records.Count == 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";

        var keyParam = cmd.Parameters.Add("@k", Microsoft.Data.Sqlite.SqliteType.Text);
        var timeParam = cmd.Parameters.Add("@t", Microsoft.Data.Sqlite.SqliteType.Text);

        // 先预编译语句，避免每条记录重新解析 SQL
        cmd.Prepare();

        foreach (var r in records)
        {
            keyParam.Value = r.Key;
            timeParam.Value = r.PressTime.ToString("o");
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Dictionary<string, int> GetKeyCounts()
    {
        return GetKeyCounts(null, null);
    }

    public Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to)
    {
        var counts = new Dictionary<string, int>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var cmd = connection.CreateCommand();

        if (from.HasValue && to.HasValue)
        {
            cmd.CommandText =
                @"
                SELECT Key, COUNT(*) 
                FROM KeyPressRecords 
                WHERE PressTime >= @from AND PressTime < @to 
                GROUP BY Key";
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("o"));
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("o"));
        }
        else
        {
            cmd.CommandText = "SELECT Key, COUNT(*) FROM KeyPressRecords GROUP BY Key";
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    // ══════════════ 统计查询 ══════════════

    /// <summary>
    /// 某日的字符串范围。PressTime 以 "o" 格式存储
    /// （如 "2026-10-08T00:10:48.0733784+08:00"），其字典序等于时间序，
    /// 因此用定宽前缀的范围比较即可，且能走 idx_keypress_presstime 索引。
    /// </summary>
    private static (string From, string To) DayRange(DateTime date)
    {
        string day = date.Date.ToString("yyyy-MM-dd");
        string next = date.Date.AddDays(1).ToString("yyyy-MM-dd");
        return (day + "T00:00:00", next + "T00:00:00");
    }

    public int[] GetHourlyCounts(DateTime date)
    {
        var result = new int[24];
        var (from, to) = DayRange(date);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        // substr(...,12,2) 取小时：存储串第 12–13 个字符正好是 "HH"
        cmd.CommandText =
            @"
            SELECT substr(PressTime, 12, 2) AS h, COUNT(*)
            FROM KeyPressRecords
            WHERE PressTime >= @from AND PressTime < @to
            GROUP BY h";
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (int.TryParse(reader.GetString(0), out int hour) && hour is >= 0 and < 24)
                result[hour] = reader.GetInt32(1);
        }
        return result;
    }

    public int GetActiveMinuteCount(DateTime from, DateTime to)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        // 不同的 "HH:mm" 数量 = 有按键的分钟数
        cmd.CommandText =
            @"
            SELECT COUNT(DISTINCT substr(PressTime, 12, 5))
            FROM KeyPressRecords
            WHERE PressTime >= @from AND PressTime < @to";
        cmd.Parameters.AddWithValue("@from", from.Date.ToString("yyyy-MM-dd") + "T00:00:00");
        cmd.Parameters.AddWithValue("@to", to.Date.ToString("yyyy-MM-dd") + "T00:00:00");

        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public int GetActiveDayCount(DateTime from, DateTime to)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            @"
            SELECT COUNT(DISTINCT substr(PressTime, 1, 10))
            FROM KeyPressRecords
            WHERE PressTime >= @from AND PressTime < @to";
        cmd.Parameters.AddWithValue("@from", from.Date.ToString("yyyy-MM-dd") + "T00:00:00");
        cmd.Parameters.AddWithValue("@to", to.Date.ToString("yyyy-MM-dd") + "T00:00:00");

        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public List<DailyKeyCount> GetDailyTotals(int days)
    {
        var list = new List<DailyKeyCount>();
        if (days <= 0)
            return list;

        var start = DateTime.Today.AddDays(-(days - 1));
        var (from, _) = DayRange(start);
        var (_, to) = DayRange(DateTime.Today);

        var byDate = new Dictionary<string, int>();
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"
                SELECT substr(PressTime, 1, 10) AS d, COUNT(*)
                FROM KeyPressRecords
                WHERE PressTime >= @from AND PressTime < @to
                GROUP BY d";
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                byDate[reader.GetString(0)] = reader.GetInt32(1);
        }

        // 补齐没有记录的日子：否则趋势图会缺柱、日期轴与实际日期错位
        for (int i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            byDate.TryGetValue(d.ToString("yyyy-MM-dd"), out int count);
            list.Add(new DailyKeyCount(d, count));
        }

        return list;
    }
}
