namespace GeminiNexus.Server.Infrastructure;

using Microsoft.Data.Sqlite;
using GeminiNexus.Shared.Contracts;

public sealed class DatabaseService
{
    private const string ConnectionString = "Data Source=nexus_chat.db;Mode=ReadWriteCreate;Cache=Shared";

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        const string sql = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                PromptTokens INTEGER,
                CandidateTokens INTEGER,
                CachedTokens INTEGER,
                TokensPerSecond REAL,
                ElapsedMs INTEGER,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Messages_Session ON Messages(SessionId);
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask SaveMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO Messages (SessionId, Role, Content, PromptTokens, CandidateTokens, CachedTokens, TokensPerSecond, ElapsedMs, CreatedAtUtc)
            VALUES ($sessionId, $role, $content, $pt, $ct, $cached, $tps, $elapsed, $created);
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$sessionId", message.SessionId);
        cmd.Parameters.AddWithValue("$role", message.Role);
        cmd.Parameters.AddWithValue("$content", message.Content);
        cmd.Parameters.AddWithValue("$pt", message.PromptTokens);
        cmd.Parameters.AddWithValue("$ct", message.CandidateTokens);
        cmd.Parameters.AddWithValue("$cached", message.CachedTokens);
        cmd.Parameters.AddWithValue("$tps", message.TokensPerSecond);
        cmd.Parameters.AddWithValue("$elapsed", message.ElapsedMilliseconds);
        cmd.Parameters.AddWithValue("$created", message.CreatedAtUtc.ToString("o"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async IAsyncEnumerable<ChatMessage> GetMessagesAsync(string sessionId)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        const string sql = "SELECT Id, SessionId, Role, Content, PromptTokens, CandidateTokens, CachedTokens, TokensPerSecond, ElapsedMs, CreatedAtUtc FROM Messages WHERE SessionId = $sessionId ORDER BY Id ASC;";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            yield return new ChatMessage(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetDouble(7),
                reader.GetInt64(8),
                DateTime.Parse(reader.GetString(9))
            );
        }
    }
}