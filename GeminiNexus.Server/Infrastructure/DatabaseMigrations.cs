using System.Data.Common;
using GeminiNexus.Server.Application;

namespace GeminiNexus.Server.Infrastructure;

/// <summary>
/// Versioned, forward-only schema migrations. PostgreSQL uses a session advisory lock so
/// exactly one server migrates a shared database; SQLite is intentionally single-server.
/// </summary>
public static class DatabaseMigrations
{
    private const long AdvisoryLock = 0x4E45585553; // "NEXUS"

    private static readonly (int Version, string Name, string Sql)[] All =
    [
        (1, "workspace_core", """
        CREATE TABLE IF NOT EXISTS NexusUsers(Id TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE, PasswordHash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS AuthSessions(Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, ExpiresAt BIGINT NOT NULL, FOREIGN KEY(UserId) REFERENCES NexusUsers(Id) ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS IX_AuthSessions_User ON AuthSessions(UserId);
        CREATE TABLE IF NOT EXISTS Conversations(Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL, Title TEXT NOT NULL, Model TEXT NOT NULL, UpdatedAt TEXT NOT NULL, Pinned INTEGER NOT NULL DEFAULT 0, Archived INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS IX_Conversations_Owner ON Conversations(OwnerId, Archived, Pinned, UpdatedAt, Id);
        CREATE TABLE IF NOT EXISTS Messages(Id TEXT PRIMARY KEY, ConversationId TEXT NOT NULL, RunId TEXT NOT NULL, Ordinal BIGINT NOT NULL, Role TEXT NOT NULL, Content TEXT NOT NULL, PartsJson TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status TEXT NOT NULL, UNIQUE(ConversationId, Ordinal));
        CREATE TABLE IF NOT EXISTS Runs(Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL, ConversationId TEXT NOT NULL, Model TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, LastSequence BIGINT NOT NULL DEFAULT 0, Error TEXT, CancelRequested INTEGER NOT NULL DEFAULT 0, IdempotencyKey TEXT NOT NULL, RequestJson TEXT NOT NULL, LeaseOwner TEXT, LeaseUntil BIGINT NOT NULL DEFAULT 0, UNIQUE(OwnerId, IdempotencyKey));
        CREATE INDEX IF NOT EXISTS IX_Runs_Queue ON Runs(Status, CreatedAt);
        CREATE INDEX IF NOT EXISTS IX_Runs_Owner ON Runs(OwnerId, ConversationId, Status);
        CREATE TABLE IF NOT EXISTS RunEvents(RunId TEXT NOT NULL, Sequence BIGINT NOT NULL, Kind TEXT NOT NULL, Json TEXT NOT NULL, CreatedAt TEXT NOT NULL, PRIMARY KEY(RunId, Sequence));
        CREATE TABLE IF NOT EXISTS RunMetrics(RunId TEXT PRIMARY KEY, Json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS RunFingerprints(RunId TEXT PRIMARY KEY, Hash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Outbox(RunId TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Delivered INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS Preferences(OwnerId TEXT PRIMARY KEY, Json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Plugins(OwnerId TEXT NOT NULL, Id TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(OwnerId, Id));
        """),
        (2, "production_operations", """
        CREATE TABLE IF NOT EXISTS ProviderOperations(Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL, Capability TEXT NOT NULL, ProviderName TEXT, Status TEXT NOT NULL, RequestJson TEXT NOT NULL, ResponseJson TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_ProviderOperations_Owner ON ProviderOperations(OwnerId, Capability, UpdatedAt, Id);
        CREATE TABLE IF NOT EXISTS PluginPackages(OwnerId TEXT NOT NULL, Id TEXT NOT NULL, Version TEXT NOT NULL, ManifestJson TEXT NOT NULL, PackageReference TEXT NOT NULL, PackageHash TEXT NOT NULL, InstalledAt TEXT NOT NULL, PRIMARY KEY(OwnerId, Id));
        CREATE TABLE IF NOT EXISTS ToolExecutions(Id TEXT PRIMARY KEY, RunId TEXT NOT NULL, ToolName TEXT NOT NULL, ArgumentsJson TEXT NOT NULL, ResultJson TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_ToolExecutions_Run ON ToolExecutions(RunId, CreatedAt);
        CREATE TABLE IF NOT EXISTS RetentionPolicies(OwnerId TEXT PRIMARY KEY, ConversationDays INTEGER NOT NULL, FileDays INTEGER NOT NULL, DeleteArchived INTEGER NOT NULL, UpdatedAt TEXT NOT NULL);
        """),
        (3, "cluster_invariants", """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Runs_ActiveConversation ON Runs(ConversationId) WHERE Status IN ('queued','running');
        """),
        (4, "password_recovery", """
        CREATE TABLE IF NOT EXISTS UserEmails(UserId TEXT PRIMARY KEY, Email TEXT NOT NULL UNIQUE, FOREIGN KEY(UserId) REFERENCES NexusUsers(Id) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS PasswordResetTokens(TokenHash TEXT PRIMARY KEY, UserId TEXT NOT NULL, ExpiresAt BIGINT NOT NULL, UsedAt BIGINT, CreatedAt TEXT NOT NULL, FOREIGN KEY(UserId) REFERENCES NexusUsers(Id) ON DELETE CASCADE);
        CREATE INDEX IF NOT EXISTS IX_PasswordResetTokens_User ON PasswordResetTokens(UserId, ExpiresAt);
        """)
    ];

    public static int LatestVersion => All[^1].Version;

    public static async Task Apply(DbConnection db, ServerOptions options, CancellationToken ct)
    {
        var postgres = options.DatabaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
        if (postgres) await Execute(db, "SELECT pg_advisory_lock(@key)", ct, null, ("key", AdvisoryLock));
        try
        {
            await Execute(db, "CREATE TABLE IF NOT EXISTS SchemaMigrations(Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAt TEXT NOT NULL)", ct);
            foreach (var migration in All)
            {
                await using var exists = WorkspaceStore.Command(db, "SELECT 1 FROM SchemaMigrations WHERE Version=@version", null, ("version", migration.Version));
                if (await exists.ExecuteScalarAsync(ct) is not null) continue;
                await using var tx = await db.BeginTransactionAsync(ct);
                try
                {
                    await Execute(db, migration.Sql, ct, tx: tx);
                    await Execute(db, "INSERT INTO SchemaMigrations(Version,Name,AppliedAt) VALUES(@version,@name,@time)", ct, tx,
                        ("version", migration.Version), ("name", migration.Name), ("time", DateTimeOffset.UtcNow.ToString("O")));
                    await tx.CommitAsync(ct);
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }
        }
        finally
        {
            if (postgres) await Execute(db, "SELECT pg_advisory_unlock(@key)", ct, null, ("key", AdvisoryLock));
        }
    }

    private static async Task Execute(DbConnection db, string sql, CancellationToken ct, DbTransaction? tx = null, params (string, object?)[] args)
    {
        await using var command = WorkspaceStore.Command(db, sql, tx, args);
        await command.ExecuteNonQueryAsync(ct);
    }
}
