using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TeamsChannelScraper.WPF.Utilities;

/// <summary>
/// SQLite store that tracks every scraped message ID per channel so
/// re-runs never produce duplicates, even when Teams DOM omits data-message-id.
/// </summary>
public sealed class ScrapingStateDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public ScrapingStateDb()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.AppDataFolder,
            "scraping_state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scraped_messages (
                message_id   TEXT NOT NULL,
                channel_name TEXT NOT NULL,
                scraped_at   TEXT NOT NULL,
                PRIMARY KEY (message_id, channel_name)
            );
            CREATE INDEX IF NOT EXISTS idx_channel ON scraped_messages(channel_name);
            """;
        cmd.ExecuteNonQuery();
    }

    public bool IsAlreadyScraped(string messageId, string channelName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM scraped_messages WHERE message_id=$id AND channel_name=$ch LIMIT 1";
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$ch", channelName);
        return cmd.ExecuteScalar() is not null;
    }

    public void MarkScraped(string messageId, string channelName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO scraped_messages (message_id, channel_name, scraped_at)
            VALUES ($id, $ch, $at)
            """;
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$ch", channelName);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Stable deterministic ID for messages that lack a DOM data-message-id.
    /// Uses author + trimmed content + minute-precision timestamp so the same
    /// message gets the same hash across scroll passes.
    /// </summary>
    public static string ComputeContentId(string author, string content, DateTime timestamp)
    {
        var raw = $"{author}|{content.Trim()}|{timestamp:yyyyMMddHHmm}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "hash-" + Convert.ToHexString(hash)[..16];
    }

    public void Dispose() => _conn.Dispose();
}
