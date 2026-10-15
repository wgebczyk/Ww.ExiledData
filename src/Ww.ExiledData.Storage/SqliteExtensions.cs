using Microsoft.Data.Sqlite;

namespace Ww.ExiledData.Storage;

internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    public static int Execute(this SqliteConnection connection, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = connection.Command(sql, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static object? ExecuteScalar(this SqliteConnection connection, string sql, params (string Name, object? Value)[] args) =>
        connection.ExecuteScalar(sql, null, args);

    public static object? ExecuteScalar(this SqliteConnection connection, string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var cmd = connection.Command(sql, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Sets a parameter, adding it on first use so commands can be reused in loops.</summary>
    public static void Set(this SqliteCommand cmd, string name, object? value)
    {
        if (cmd.Parameters.Contains(name))
            cmd.Parameters[name].Value = value ?? DBNull.Value;
        else
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    // Timestamps are stored as UTC ticks: exact round-trip and naturally sortable.
    public static long ToDb(this DateTimeOffset value) => value.UtcTicks;
    public static long? ToDb(this DateTimeOffset? value) => value?.UtcTicks;
    public static string ToDb(this Guid value) => value.ToString("D");

    public static DateTimeOffset GetTime(this SqliteDataReader r, int i) => new(r.GetInt64(i), TimeSpan.Zero);
    public static DateTimeOffset? GetTimeOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetTime(i);
    public static Guid GetGuidText(this SqliteDataReader r, int i) => Guid.Parse(r.GetString(i));
    public static Guid? GetGuidOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuidText(i);
    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
