using System.Data;
using System.Data.Common;
using System.Text.Json;
#if !NEXUS_NATIVE_AOT
using Dapper;
#endif
using GeminiNexus.Server.Application;
using GeminiNexus.Server.Domain;
using GeminiNexus.Server.Security;
using GeminiNexus.Shared;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace GeminiNexus.Server.Infrastructure;

public interface IWorkspaceStore
{
    Task<ConversationPage> Conversations(string owner, string? cursor, int take, string? search, CancellationToken ct);
    Task<MessagePage> Messages(string owner, string conversation, long? before, int take, CancellationToken ct, long? after = null);
    Task<Run> Enqueue(string owner, SubmitRun request, CancellationToken ct);
    Task<Run?> FindRun(string owner, string id, CancellationToken ct);
    Task<RunEvent[]> Events(string owner, string id, long after, int take, CancellationToken ct);
}

// Explicit ADO.NET mapping is reflection-free and shared by SQLite and PostgreSQL.
#if !NEXUS_NATIVE_AOT
[DapperAot]
#endif
public sealed class WorkspaceStore(ServerOptions options) : IWorkspaceStore
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    public async ValueTask<DbConnection> Open(CancellationToken ct = default)
    {
        DbConnection connection = options.DatabaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
            ? new NpgsqlConnection(options.ConnectionString) : new SqliteConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            if(connection is SqliteConnection)await Execute(connection,"PRAGMA foreign_keys=ON;",null,ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public static DbCommand Command(DbConnection db, string sql, DbTransaction? tx = null, params (string, object?)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = tx;
        foreach (var (name, value) in args) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; command.Parameters.Add(p); }
        return command;
    }
    private static async Task<int> Execute(DbConnection db, string sql, DbTransaction? tx, CancellationToken ct, params (string, object?)[] args)
    { await using var cmd = Command(db, sql, tx, args); return await cmd.ExecuteNonQueryAsync(ct); }
    public async Task Initialize(CancellationToken ct)
    {
        if (!options.DatabaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var path = new SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        }
        await using var db = await Open(ct);
        if (db is SqliteConnection) await Execute(db, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", null, ct);
        await DatabaseMigrations.Apply(db, options, ct);
        var minimumAdminPasswordLength = options.AllowInsecureLocal ? 6 : 16;
        if (options.AdminPassword.Length < minimumAdminPasswordLength)
            throw new InvalidOperationException($"Auth:AdminPassword must contain at least {minimumAdminPasswordLength} characters (or supply Auth:AdminPasswordFile).");
        // Revoke only when the configured administrator password actually changed.
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var lookup=Command(db,"SELECT PasswordHash FROM NexusUsers WHERE Name=@name",tx,("name",options.AdminName));
        var hash=await lookup.ExecuteScalarAsync(ct) as string;
        if(hash is null||!Passwords.Verify(options.AdminPassword,hash))
        {
            await Execute(db,"DELETE FROM AuthSessions WHERE UserId IN (SELECT Id FROM NexusUsers WHERE Name=@name)",tx,ct,("name",options.AdminName));
            await Execute(db, "INSERT INTO NexusUsers(Id,Name,PasswordHash) VALUES(@id,@name,@hash) ON CONFLICT(Name) DO UPDATE SET PasswordHash=excluded.PasswordHash", tx, ct,
                ("id", Guid.NewGuid().ToString("N")), ("name", options.AdminName), ("hash", Passwords.Hash(options.AdminPassword)));
        }
        await tx.CommitAsync(ct);
    }
    public async Task<UserInfo?> Login(string name, string password, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var cmd = Command(db, "SELECT Id,Name,PasswordHash FROM NexusUsers WHERE Name=@name", null, ("name", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) { Passwords.Verify(password, Passwords.Hash("unavailable-user")); return null; }
        return Passwords.Verify(password, r.GetString(2)) ? new(r.GetString(0), r.GetString(1), r.GetString(1)==options.AdminName) : null;
    }
    public async Task<UserInfo> AddUser(string name, string password,string email,CancellationToken ct)
    {
        email=email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || password.Length is < 16 or > 1024||email.Length>320||email.Length>0&&(!email.Contains('@')||email.StartsWith('@')||email.EndsWith('@'))) throw new WorkspaceException(400, "שם, כתובת דוא״ל או סיסמה אינם תקינים");
        var user = new UserInfo(Guid.NewGuid().ToString("N"), name);
        await using var db = await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
        var changed=await Execute(db, "INSERT INTO NexusUsers VALUES(@id,@name,@hash) ON CONFLICT(Name) DO NOTHING",tx,ct,("id",user.Id),("name",name),("hash",Passwords.Hash(password)));
        if(changed==0)throw new WorkspaceException(409,"שם המשתמש כבר קיים");
        if(email.Length>0)
        {
            try{await Execute(db,"INSERT INTO UserEmails(UserId,Email) VALUES(@id,@email)",tx,ct,("id",user.Id),("email",email));}
            catch(DbException){throw new WorkspaceException(409,"כתובת הדוא״ל כבר משויכת לחשבון");}
        }
        await tx.CommitAsync(ct);return user;
    }
    public async Task<string> CreateSession(string user,CancellationToken ct)
    {
        var id=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var db=await Open(ct);await Execute(db,"INSERT INTO AuthSessions VALUES(@id,@user,@expires)",null,ct,("id",id),("user",user),("expires",DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()));return id;
    }
    public async Task<bool> ValidSession(string? user,string? session,CancellationToken ct)
    {
        if(string.IsNullOrEmpty(user)||string.IsNullOrEmpty(session))return false;
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT 1 FROM AuthSessions WHERE Id=@id AND UserId=@user AND ExpiresAt>@now",null,("id",session),("user",user),("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));return await cmd.ExecuteScalarAsync(ct) is not null;
    }
    public async Task RevokeSessions(string user,string? session,CancellationToken ct)
    {await using var db=await Open(ct);await Execute(db,"DELETE FROM AuthSessions WHERE UserId=@user AND (@session='' OR Id=@session)",null,ct,("user",user),("session",session??""));}
    public async Task<UserAccount[]> Users(CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT u.Id,u.Name,(SELECT COUNT(*) FROM AuthSessions s WHERE s.UserId=u.Id AND s.ExpiresAt>@now),COALESCE(e.Email,'') FROM NexusUsers u LEFT JOIN UserEmails e ON e.UserId=u.Id ORDER BY u.Name",null,("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var users=new List<UserAccount>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))users.Add(new(r.GetString(0),r.GetString(1),Convert.ToInt32(r.GetValue(2)),r.GetString(3)));return users.ToArray();
    }
    public async Task<(string Email,string Token)?> CreatePasswordReset(string email,CancellationToken ct)
    {
        email=email.Trim().ToLowerInvariant();if(email.Length is <3 or >320)return null;
        await using var db=await Open(ct);await using var find=Command(db,"SELECT e.UserId,e.Email FROM UserEmails e JOIN NexusUsers u ON u.Id=e.UserId WHERE e.Email=@email AND u.Name<>@admin",null,("email",email),("admin",options.AdminName));
        string user,address;await using(var reader=await find.ExecuteReaderAsync(ct)){if(!await reader.ReadAsync(ct))return null;user=reader.GetString(0);address=reader.GetString(1);}
        var token=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));var now=DateTimeOffset.UtcNow;
        await Execute(db,"DELETE FROM PasswordResetTokens WHERE UserId=@user OR ExpiresAt<=@now",null,ct,("user",user),("now",now.ToUnixTimeSeconds()));
        await Execute(db,"INSERT INTO PasswordResetTokens(TokenHash,UserId,ExpiresAt,UsedAt,CreatedAt) VALUES(@hash,@user,@expires,NULL,@created)",null,ct,("hash",hash),("user",user),("expires",now.AddMinutes(30).ToUnixTimeSeconds()),("created",now.ToString("O")));
        return(address,token);
    }
    public async Task ResetPassword(string token,string password,CancellationToken ct)
    {
        if(token.Length is <32 or >256||password.Length is <16 or >1024)throw new WorkspaceException(400,"אסימון או סיסמה אינם תקינים");
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        await using var find=Command(db,"SELECT t.UserId FROM PasswordResetTokens t JOIN NexusUsers u ON u.Id=t.UserId WHERE t.TokenHash=@hash AND t.UsedAt IS NULL AND t.ExpiresAt>@now AND u.Name<>@admin",tx,("hash",hash),("now",now),("admin",options.AdminName));
        var user=await find.ExecuteScalarAsync(ct) as string;if(user is null)throw new WorkspaceException(400,"אסימון האיפוס אינו תקף או שפג תוקפו");
        await Execute(db,"UPDATE NexusUsers SET PasswordHash=@password WHERE Id=@user",tx,ct,("password",Passwords.Hash(password)),("user",user));
        await Execute(db,"DELETE FROM AuthSessions WHERE UserId=@user",tx,ct,("user",user));
        var changed=await Execute(db,"UPDATE PasswordResetTokens SET UsedAt=@now WHERE TokenHash=@hash AND UsedAt IS NULL",tx,ct,("now",now),("hash",hash));if(changed!=1)throw new WorkspaceException(400,"אסימון האיפוס כבר נוצל");
        await tx.CommitAsync(ct);
    }
    public async Task UpdatePassword(string owner,ChangePassword request,CancellationToken ct)
    {
        if(request.CurrentPassword.Length>1024||request.NewPassword.Length is <16 or >1024)throw new WorkspaceException(400,"הסיסמה החדשה חייבת להכיל 16 עד 1024 תווים");
        await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=Command(db,"SELECT Name,PasswordHash FROM NexusUsers WHERE Id=@id",tx,("id",owner));string name,hash;
        await using(var r=await cmd.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))throw new WorkspaceException(401,"נדרשת התחברות");name=r.GetString(0);hash=r.GetString(1);}
        if(name==options.AdminName)throw new WorkspaceException(409,"סיסמת המנהל מנוהלת בקובץ הסודות של השרת");
        if(!Passwords.Verify(request.CurrentPassword,hash))throw new WorkspaceException(403,"הסיסמה הנוכחית אינה נכונה");
        await Execute(db,"UPDATE NexusUsers SET PasswordHash=@hash WHERE Id=@id",tx,ct,("hash",Passwords.Hash(request.NewPassword)),("id",owner));
        await Execute(db,"DELETE FROM AuthSessions WHERE UserId=@id",tx,ct,("id",owner));await tx.CommitAsync(ct);
    }
    public async Task RequireOwner(DbConnection db, string owner, string id, CancellationToken ct, DbTransaction? tx = null)
    {
        await using var cmd = Command(db, "SELECT Id FROM Conversations WHERE Id=@id AND OwnerId=@owner AND Archived=0",tx,("id",id),("owner",owner));
        if (await cmd.ExecuteScalarAsync(ct) is null) throw new WorkspaceException(404,"השיחה לא נמצאה");
    }
    private static Conversation ConversationFrom(DbDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4)!=0,Convert.ToInt32(r.GetValue(5)));
    public async Task<ConversationPage> Conversations(string owner, string? cursor, int take, string? search, CancellationToken ct)
    {
        take=Math.Clamp(take,1,100); string time="", id=""; int pinned=0;
        if (!string.IsNullOrEmpty(cursor))
        {
            try { var parts=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|'); pinned=int.Parse(parts[0]);time=parts[1];id=parts[2]; }
            catch { throw new WorkspaceException(400,"סמן עמוד אינו תקין"); }
        }
        await using var db=await Open(ct); await using var cmd=Command(db,"""
        SELECT c.Id,c.Title,c.Model,c.UpdatedAt,c.Pinned,(SELECT COUNT(*) FROM Messages m WHERE m.ConversationId=c.Id)
        FROM Conversations c WHERE c.OwnerId=@owner AND c.Archived=0
        AND (@search='' OR LOWER(c.Title) LIKE @search)
        AND (@cursor='' OR c.Pinned<@pin OR (c.Pinned=@pin AND (c.UpdatedAt<@time OR (c.UpdatedAt=@time AND c.Id<@id))))
        ORDER BY c.Pinned DESC,c.UpdatedAt DESC,c.Id DESC LIMIT @take
        """,null,("owner",owner),("search",string.IsNullOrWhiteSpace(search)?"":"%"+search.ToLowerInvariant()+"%"),("cursor",cursor??""),("pin",pinned),("time",time),("id",id),("take",take+1));
        var items=new List<Conversation>(); await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add(ConversationFrom(r));
        var more=items.Count>take;if(more)items.RemoveAt(items.Count-1);
        var last=items.LastOrDefault();var next=more&&last is not null?Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{(last.Pinned?1:0)}|{last.UpdatedAt}|{last.Id}")):null;
        return new(items.ToArray(),next);
    }
    public async Task<Conversation> Create(string owner, CreateConversation request, CancellationToken ct)
    {
        var c=new Conversation(Guid.NewGuid().ToString("N"),string.IsNullOrWhiteSpace(request.Title)?"שיחה חדשה":request.Title[..Math.Min(160,request.Title.Length)],string.IsNullOrEmpty(request.Model)?options.DefaultModel:request.Model,Now,false,0);
        await using var db=await Open(ct);await Execute(db,"INSERT INTO Conversations(Id,OwnerId,Title,Model,UpdatedAt) VALUES(@id,@owner,@title,@model,@time)",null,ct,("id",c.Id),("owner",owner),("title",c.Title),("model",c.Model),("time",c.UpdatedAt));return c;
    }
    public async Task Rename(string owner,string id,RenameConversation request,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>160)throw new WorkspaceException(400,"כותרת אינה תקינה");
        await using var db=await Open(ct);await RequireOwner(db,owner,id,ct);await Execute(db,"UPDATE Conversations SET Title=@title,Pinned=@pin,UpdatedAt=@time WHERE Id=@id",null,ct,("title",request.Title),("pin",request.Pinned?1:0),("time",Now),("id",id));
    }
    public async Task Archive(string owner,string id,CancellationToken ct)
    {
        await using var db=await Open(ct);await RequireOwner(db,owner,id,ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await Execute(db,"UPDATE Conversations SET Archived=1 WHERE Id=@id",tx,ct,("id",id));
        await Execute(db,"UPDATE Runs SET CancelRequested=1 WHERE ConversationId=@id AND Status IN ('queued','running')",tx,ct,("id",id));await tx.CommitAsync(ct);
    }
    private static Message MessageFrom(DbDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8));
    public async Task<MessagePage> Messages(string owner,string conversation,long? before,int take,CancellationToken ct,long? after=null)
    {
        await using var db=await Open(ct);await RequireOwner(db,owner,conversation,ct);take=Math.Clamp(take,1,100);
        var list=await ReadMessages(db,conversation,after??before,take+1,ct,newer:after.HasValue);
        var more=list.Count>take||list.Count>1&&list.Sum(m=>(long)(m.Content.Length+m.PartsJson.Length)*2)>=options.MaxHistoryBytes;
        if(more)list.RemoveAt(list.Count-1);if(!after.HasValue)list.Reverse();
        return new(list.ToArray(),after.HasValue?list.Count>0&&list[0].Ordinal>1:more,list.Count>0?list[0].Ordinal:null,after.HasValue&&more);
    }
    private async Task<List<Message>> ReadMessages(DbConnection db,string id,long? before,int take,CancellationToken ct,DbTransaction? tx=null,bool newer=false)
    {
        var sql=newer?"SELECT Id,ConversationId,RunId,Ordinal,Role,Content,PartsJson,CreatedAt,Status FROM Messages WHERE ConversationId=@id AND Ordinal>@before ORDER BY Ordinal ASC LIMIT @take":"SELECT Id,ConversationId,RunId,Ordinal,Role,Content,PartsJson,CreatedAt,Status FROM Messages WHERE ConversationId=@id AND Ordinal<@before ORDER BY Ordinal DESC LIMIT @take";
        await using var cmd=Command(db,sql,tx,("id",id),("before",before??long.MaxValue),("take",take));
        var list=new List<Message>();long bytes=0;await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var message=MessageFrom(r);list.Add(message);bytes+=(long)(message.Content.Length+message.PartsJson.Length)*2;
            // Includes one look-ahead row so pagination can report additional history.
            if(bytes>=options.MaxHistoryBytes&&list.Count>1)break;
        }
        return list;
    }
    public async Task<Conversation> Fork(string owner,string id,string messageId,CancellationToken ct,bool before=false)
    {
        await using var db=await Open(ct);await RequireOwner(db,owner,id,ct);
        await using var cmd=Command(db,"SELECT Ordinal FROM Messages WHERE Id=@m AND ConversationId=@c",null,("m",messageId),("c",id));
        var ordinal=await cmd.ExecuteScalarAsync(ct);if(ordinal is null)throw new WorkspaceException(404,"ההודעה לא נמצאה");
        await using var modelCommand=Command(db,"SELECT Model FROM Conversations WHERE Id=@id",null,("id",id));
        var model=(string)(await modelCommand.ExecuteScalarAsync(ct))!;
        var c=await Create(owner,new("הסתעפות שיחה",model),ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var read=Command(db,"SELECT Id,ConversationId,RunId,Ordinal,Role,Content,PartsJson,CreatedAt,Status FROM Messages WHERE ConversationId=@id AND Ordinal<=@n ORDER BY Ordinal",tx,("id",id),("n",Convert.ToInt64(ordinal)-(before?1:0)));
        var copy=new List<Message>();await using(var r=await read.ExecuteReaderAsync(ct)){while(await r.ReadAsync(ct))copy.Add(MessageFrom(r));}
        foreach(var m in copy)await InsertMessage(db,tx,m with{Id=Guid.NewGuid().ToString("N"),ConversationId=c.Id,RunId=""},ct);
        await tx.CommitAsync(ct);return c with{MessageCount=copy.Count};
    }
    private static Task<int> InsertMessage(DbConnection db,DbTransaction tx,Message m,CancellationToken ct)=>Execute(db,"INSERT INTO Messages VALUES(@id,@c,@r,@n,@role,@content,@parts,@time,@status)",tx,ct,("id",m.Id),("c",m.ConversationId),("r",m.RunId),("n",m.Ordinal),("role",m.Role),("content",m.Content),("parts",m.PartsJson),("time",m.CreatedAt),("status",m.Status));
    public async Task SaveFile(string owner,Attachment file,string providerName,string providerUri,CancellationToken ct)
    {
        await using var db=await Open(ct);
        await Execute(db,"INSERT INTO UserFiles(Id,OwnerId,Name,MimeType,SizeBytes,ProviderName,ProviderUri,State,ExpiresAt,CreatedAt) VALUES(@id,@o,@n,@m,@z,@p,@u,@s,@e,@t)",null,ct,
            ("id",file.FileId),("o",owner),("n",file.Name),("m",file.MimeType),("z",file.SizeBytes),("p",providerName),("u",providerUri),("s",file.State),("e",file.ExpiresAt),("t",Now));
    }
    public async Task<Attachment[]> Files(string owner,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Id,Name,MimeType,SizeBytes,ProviderUri,State,ExpiresAt FROM UserFiles WHERE OwnerId=@o ORDER BY CreatedAt DESC LIMIT 100",null,("o",owner));
        var files=new List<Attachment>();await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))files.Add(new(r.GetString(1),r.GetString(2),FileId:r.GetString(0),FileUri:r.GetString(4),SizeBytes:r.GetInt64(3),State:r.GetString(5),ExpiresAt:r.IsDBNull(6)?null:r.GetString(6)));
        return files.ToArray();
    }
    public async Task<string> DeleteFile(string owner,string id,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);await using var lookup=Command(db,"SELECT ProviderName FROM UserFiles WHERE Id=@id AND OwnerId=@o",tx,("id",id),("o",owner));
        if(await lookup.ExecuteScalarAsync(ct) is not string providerName)throw new WorkspaceException(404,"הקובץ לא נמצא");await Execute(db,"DELETE FROM UserFiles WHERE Id=@id AND OwnerId=@o",tx,ct,("id",id),("o",owner));await tx.CommitAsync(ct);return providerName;
    }
    public async Task<string> FileProviderName(string owner,string id,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT ProviderName FROM UserFiles WHERE Id=@id AND OwnerId=@o",null,("id",id),("o",owner));
        return await cmd.ExecuteScalarAsync(ct) as string??throw new WorkspaceException(404,"הקובץ לא נמצא");
    }
    private static async Task<Attachment> RequireFile(DbConnection db,DbTransaction tx,string owner,string id,CancellationToken ct)
    {
        await using var cmd=Command(db,"SELECT Name,MimeType,SizeBytes,ProviderUri,State,ExpiresAt FROM UserFiles WHERE Id=@id AND OwnerId=@o",tx,("id",id),("o",owner));await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))throw new WorkspaceException(404,"הקובץ המצורף לא נמצא");
        var expires=r.IsDBNull(5)?null:r.GetString(5);if(expires is not null&&DateTimeOffset.TryParse(expires,out var expiry)&&expiry<=DateTimeOffset.UtcNow)throw new WorkspaceException(410,"תוקף הקובץ המצורף פג; יש להעלות אותו מחדש");
        if(!r.GetString(4).Equals("active",StringComparison.OrdinalIgnoreCase))throw new WorkspaceException(409,"הקובץ עדיין אינו מוכן לעיבוד");
        return new(r.GetString(0),r.GetString(1),FileId:id,FileUri:r.GetString(3),SizeBytes:r.GetInt64(2),State:r.GetString(4),ExpiresAt:expires);
    }
    public async Task<Run> Enqueue(string owner,SubmitRun request,CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            if(options.DatabaseProvider.Equals("Postgres",StringComparison.OrdinalIgnoreCase))
                await Execute(db,"SELECT pg_advisory_xact_lock(hashtext(@owner),hashtext(@conversation))",tx,ct,("owner",owner),("conversation",request.ConversationId));
            await RequireOwner(db,owner,request.ConversationId,ct,tx);
            var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,NexusJson.Default.SubmitRun)));
            Run? previous=null;
            await using(var existing=Command(db,"SELECT Id,ConversationId,Model,Status,CreatedAt,UpdatedAt,LastSequence,Error,CancelRequested FROM Runs WHERE OwnerId=@o AND IdempotencyKey=@k",tx,("o",owner),("k",request.IdempotencyKey)))
            {await using var r=await existing.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct))previous=RunFrom(r);}
            if(previous is not null)
            {
                await using var hash=Command(db,"SELECT Hash FROM RunFingerprints WHERE RunId=@id",tx,("id",previous.Id));
                if(previous.ConversationId!=request.ConversationId||await hash.ExecuteScalarAsync(ct) is not string oldHash||oldHash!=fingerprint)
                    throw new WorkspaceException(409,"מזהה הבקשה כבר שימש לשליחה אחרת");
                return previous;
            }
            await using(var active=Command(db,"SELECT COUNT(*) FROM Runs WHERE ConversationId=@id AND Status IN ('queued','running')",tx,("id",request.ConversationId)))
                if(Convert.ToInt64(await active.ExecuteScalarAsync(ct))>0)throw new WorkspaceException(409,"כבר יש ריצה בשיחה. אפשר ליצור הסתעפות כדי לעבוד במקביל.");
            await using(var count=Command(db,"SELECT COUNT(*) FROM Runs WHERE OwnerId=@o AND Status IN ('queued','running')",tx,("o",owner)))
                if(Convert.ToInt64(await count.ExecuteScalarAsync(ct))>=options.MaxQueuedPerUser)throw new WorkspaceException(429,"תור העיבוד מלא");
            var history=await ReadMessages(db,request.ConversationId,null,request.Settings.ContextMaxTurns*2,ct,tx);history.Reverse();
            // Preserve all provider parts, including thought signatures and tool responses.
            var contents=new System.Text.Json.Nodes.JsonArray();
            foreach(var m in history.Where(m=>m.Status=="completed"))
                contents.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject{["role"]=m.Role,["parts"]=System.Text.Json.Nodes.JsonNode.Parse(m.PartsJson)});
            var parts=new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject{["text"]=request.Prompt});
            foreach(var attachment in request.Attachments??[])
            {
                if(attachment.FileId.Length>0)
                {
                    var file=await RequireFile(db,tx,owner,attachment.FileId,ct);
                    parts.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject{["fileData"]=new System.Text.Json.Nodes.JsonObject{["mimeType"]=file.MimeType,["fileUri"]=file.FileUri}});
                }
                else parts.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject{["inlineData"]=new System.Text.Json.Nodes.JsonObject{["mimeType"]=attachment.MimeType,["data"]=attachment.Data}});
            }
            contents.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject{["role"]="user",["parts"]=parts.DeepClone()});
            var run=new Run(Guid.NewGuid().ToString("N"),request.ConversationId,request.Model,"queued",Now,Now,0,null,false);
            var stored=JsonSerializer.Serialize(new StoredRunRequest(request,contents.ToJsonString()),NexusJson.Default.StoredRunRequest);
            await Execute(db,"INSERT INTO Runs(Id,OwnerId,ConversationId,Model,Status,CreatedAt,UpdatedAt,IdempotencyKey,RequestJson) VALUES(@id,@o,@c,@m,'queued',@t,@t,@k,@j)",tx,ct,("id",run.Id),("o",owner),("c",run.ConversationId),("m",run.Model),("t",run.CreatedAt),("k",request.IdempotencyKey),("j",stored));
            await using var next=Command(db,"SELECT COALESCE(MAX(Ordinal),0)+1 FROM Messages WHERE ConversationId=@c",tx,("c",run.ConversationId));var ordinal=Convert.ToInt64(await next.ExecuteScalarAsync(ct));
            await InsertMessage(db,tx,new(Guid.NewGuid().ToString("N"),run.ConversationId,run.Id,ordinal,"user",request.Prompt,parts.ToJsonString(),Now,"completed"),ct);
            await Execute(db,"UPDATE Conversations SET UpdatedAt=@t,Model=@m,Title=CASE WHEN Title='שיחה חדשה' THEN @title ELSE Title END WHERE Id=@id",tx,ct,("t",Now),("m",request.Model),("title",request.Prompt[..Math.Min(70,request.Prompt.Length)]),("id",run.ConversationId));
            await Execute(db,"INSERT INTO RunFingerprints VALUES(@id,@hash)",tx,ct,("id",run.Id),("hash",fingerprint));
            await Execute(db,"INSERT INTO Outbox VALUES(@id,@t,0)",tx,ct,("id",run.Id),("t",Now));await tx.CommitAsync(ct);return run;
        }
        finally{writeGate.Release();}
    }
    private static Run RunFrom(DbDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6),r.IsDBNull(7)?null:r.GetString(7),r.GetInt32(8)!=0);
    public async Task<Run?> FindRun(string owner,string id,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Id,ConversationId,Model,Status,CreatedAt,UpdatedAt,LastSequence,Error,CancelRequested FROM Runs WHERE Id=@id AND OwnerId=@o",null,("id",id),("o",owner));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?RunFrom(r):null;
    }
    public async Task<RunMetrics> Metrics(string owner,string id,CancellationToken ct)
    {
        if(await FindRun(owner,id,ct) is null)throw new WorkspaceException(404,"הריצה לא נמצאה");
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Json FROM RunMetrics WHERE RunId=@id",null,("id",id));
        return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize(json,NexusJson.Default.RunMetrics)!:new("{}",null,null,0,null,0);
    }
    public async Task<Run[]> ListRuns(string owner,string? conversation,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Id,ConversationId,Model,Status,CreatedAt,UpdatedAt,LastSequence,Error,CancelRequested FROM Runs WHERE OwnerId=@o AND (@c='' OR ConversationId=@c) ORDER BY CASE WHEN Status IN ('queued','running') THEN 0 ELSE 1 END,CreatedAt DESC LIMIT 100",null,("o",owner),("c",conversation??""));
        var list=new List<Run>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(RunFrom(r));return list.ToArray();
    }
    public async Task Cancel(string owner,string id,CancellationToken ct)
    {
        await using var db=await Open(ct);var n=await Execute(db,"UPDATE Runs SET CancelRequested=1 WHERE Id=@id AND OwnerId=@o AND Status IN ('queued','running')",null,ct,("id",id),("o",owner));
        if(n==0&&await FindRun(owner,id,ct) is null)throw new WorkspaceException(404,"הריצה לא נמצאה");
    }
    public async Task<(Run Run,string Owner,StoredRunRequest Request)?> Claim(string worker,CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(options.DatabaseProvider.Equals("Postgres",StringComparison.OrdinalIgnoreCase)?IsolationLevel.ReadCommitted:IsolationLevel.Serializable,ct);
            var claimSql=options.DatabaseProvider.Equals("Postgres",StringComparison.OrdinalIgnoreCase)?"""
            SELECT r.Id,r.ConversationId,r.Model,r.Status,r.CreatedAt,r.UpdatedAt,r.LastSequence,r.Error,r.CancelRequested,r.OwnerId,r.RequestJson
            FROM Runs r JOIN Outbox o ON o.RunId=r.Id WHERE r.Status='queued' AND o.Delivered=0
            AND (SELECT COUNT(*) FROM Runs active WHERE active.OwnerId=r.OwnerId AND active.Status='running')<@max
            AND pg_try_advisory_xact_lock(hashtext(r.OwnerId))
            ORDER BY r.CreatedAt LIMIT 1 FOR UPDATE OF r SKIP LOCKED
            """:"""
            SELECT r.Id,r.ConversationId,r.Model,r.Status,r.CreatedAt,r.UpdatedAt,r.LastSequence,r.Error,r.CancelRequested,r.OwnerId,r.RequestJson
            FROM Runs r JOIN Outbox o ON o.RunId=r.Id WHERE r.Status='queued' AND o.Delivered=0
            AND (SELECT COUNT(*) FROM Runs active WHERE active.OwnerId=r.OwnerId AND active.Status='running')<@max
            ORDER BY r.CreatedAt LIMIT 1
            """;
            await using var cmd=Command(db,claimSql,tx,("max",options.PerUserConcurrency));
            Run run;string owner;StoredRunRequest request;
            await using(var r=await cmd.ExecuteReaderAsync(ct))
            {if(!await r.ReadAsync(ct))return null;run=RunFrom(r);owner=r.GetString(9);request=JsonSerializer.Deserialize(r.GetString(10),NexusJson.Default.StoredRunRequest)!;}
            var changed=await Execute(db,"UPDATE Runs SET Status='running',LeaseOwner=@w,LeaseUntil=@lease,UpdatedAt=@t WHERE Id=@id AND Status='queued'",tx,ct,("w",worker),("lease",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+30),("t",Now),("id",run.Id));
            if(changed!=1)return null;
            await Execute(db,"UPDATE Outbox SET Delivered=1 WHERE RunId=@id",tx,ct,("id",run.Id));await tx.CommitAsync(ct);return(run with{Status="running"},owner,request);
        }
        finally{writeGate.Release();}
    }
    public async Task<bool> Heartbeat(string id,string worker,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"UPDATE Runs SET LeaseUntil=@lease WHERE Id=@id AND LeaseOwner=@w AND Status='running' RETURNING CancelRequested",null,("lease",DateTimeOffset.UtcNow.ToUnixTimeSeconds()+30),("id",id),("w",worker));
        var value=await cmd.ExecuteScalarAsync(ct);return value is null||Convert.ToInt32(value)!=0;
    }
    public async Task Append(string id,string worker,string kind,string json,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=Command(db,"UPDATE Runs SET LastSequence=LastSequence+1,UpdatedAt=@t WHERE Id=@id AND LeaseOwner=@w AND Status='running' RETURNING LastSequence",tx,("t",Now),("id",id),("w",worker));
        var sequence=await cmd.ExecuteScalarAsync(ct);if(sequence is null)throw new OperationCanceledException("Run lease lost");
        await Execute(db,"INSERT INTO RunEvents VALUES(@id,@s,@k,@j,@t)",tx,ct,("id",id),("s",Convert.ToInt64(sequence)),("k",kind),("j",json),("t",Now));await tx.CommitAsync(ct);
    }
    public async Task Finish(string id,string worker,string content,string parts,string status,string? error,CancellationToken ct,bool expiredOnly=false,RunMetrics? metrics=null)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
            await using var cmd=Command(db,"UPDATE Runs SET Status=@s,Error=@e,UpdatedAt=@t,LeaseUntil=0,RequestJson='',LastSequence=LastSequence+1 WHERE Id=@id AND LeaseOwner=@w AND Status='running' AND (@expired=0 OR LeaseUntil<@now) RETURNING ConversationId",tx,("s",status),("e",error),("t",Now),("id",id),("w",worker),("expired",expiredOnly?1:0),("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var c=await cmd.ExecuteScalarAsync(ct) as string;if(c is null)return;
            if(metrics is not null)await Execute(db,"INSERT INTO RunMetrics VALUES(@id,@json) ON CONFLICT(RunId) DO UPDATE SET Json=excluded.Json",tx,ct,("id",id),("json",JsonSerializer.Serialize(metrics,NexusJson.Default.RunMetrics)));
            await Execute(db,"INSERT INTO RunEvents SELECT Id,LastSequence,'complete',@json,@time FROM Runs WHERE Id=@id",tx,ct,
                ("json",JsonSerializer.Serialize(new RunCompletion(status,error),NexusJson.Default.RunCompletion)),("time",Now),("id",id));
            await using var next=Command(db,"SELECT COALESCE(MAX(Ordinal),0)+1 FROM Messages WHERE ConversationId=@c",tx,("c",c));var ordinal=Convert.ToInt64(await next.ExecuteScalarAsync(ct));
            await InsertMessage(db,tx,new(Guid.NewGuid().ToString("N"),c,id,ordinal,"model",content,parts,Now,status),ct);
            await Execute(db,"UPDATE Conversations SET UpdatedAt=@t WHERE Id=@id",tx,ct,("t",Now),("id",c));await tx.CommitAsync(ct);
        }
        finally{writeGate.Release();}
    }
    public async Task Recover(CancellationToken ct)
    {
        await using var db=await Open(ct);
        await Execute(db,"DELETE FROM AuthSessions WHERE ExpiresAt<=@now",null,ct,("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await Execute(db,"DELETE FROM PasswordResetTokens WHERE ExpiresAt<=@now OR UsedAt IS NOT NULL",null,ct,("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        // Recover visible partial responses without repeating potentially billed generation.
        var expired=new List<(string Id,string Owner,string Worker)>();
        await using(var cmd=Command(db,"SELECT Id,OwnerId,LeaseOwner FROM Runs WHERE Status='running' AND LeaseUntil<@now",null,("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
        {await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))expired.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));}
        foreach(var run in expired)
        {
            var text=new System.Text.StringBuilder();var parts=new System.Text.Json.Nodes.JsonArray();long after=0;
            while(true)
            {
                var events=await Events(run.Owner,run.Id,after,100,ct);if(events.Length==0)break;
                foreach(var item in events)
                {
                    after=item.Sequence;if(item.Kind!="provider")continue;
                    var chunk=Providers.GeminiProvider.Parse(item.Json);text.Append(chunk.Text);
                    foreach(var part in System.Text.Json.Nodes.JsonNode.Parse(chunk.PartsJson)!.AsArray())parts.Add(part?.DeepClone());
                }
            }
            await Finish(run.Id,run.Worker,text.ToString(),parts.ToJsonString(),"interrupted","העיבוד נקטע עקב הפסקת שרת; אפשר לנסות שוב במפורש",ct,expiredOnly:true);
        }
        await Execute(db,"DELETE FROM RunEvents WHERE CreatedAt<@cutoff AND RunId IN (SELECT Id FROM Runs WHERE Status NOT IN ('queued','running'))",null,ct,("cutoff",DateTimeOffset.UtcNow.AddDays(-options.TraceRetentionDays).ToString("O")));
        await ApplyRetention(ct);
    }
    public async Task<RunEvent[]> Events(string owner,string id,long after,int take,CancellationToken ct)
    {
        if(await FindRun(owner,id,ct) is null)throw new WorkspaceException(404,"הריצה לא נמצאה");
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT RunId,Sequence,Kind,Json,CreatedAt FROM RunEvents WHERE RunId=@id AND Sequence>@s ORDER BY Sequence LIMIT @take",null,("id",id),("s",after),("take",Math.Clamp(take,1,200)));
        var list=new List<RunEvent>();long bytes=0;await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var json=r.GetString(3);var size=json.Length*2L;
            if(list.Count>0&&bytes+size>options.MaxHistoryBytes)break;
            list.Add(new(r.GetString(0),r.GetInt64(1),r.GetString(2),json,r.GetString(4)));bytes+=size;
        }
        return list.ToArray();
    }
    public async Task<WorkspaceSettings> Settings(string owner,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Json FROM Preferences WHERE OwnerId=@o",null,("o",owner));return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize(json,NexusJson.Default.WorkspaceSettings)!:new();
    }
    public static void ValidateGeneration(GenerationSettings s)
    {
        if(s.ContextMaxTurns is <0 or >500 || s.ContextTokenBudget is <1 or >2000000 || s.MaxOutputTokens is <1 or >1000000 || !double.IsFinite(s.Temperature) || s.Temperature is <0 or >2 || s.AdvancedJson.Length>131072 || s.SystemInstruction.Length>100000)
            throw new WorkspaceException(400,"הגדרות יצירה אינן תקינות");
        using var json=JsonDocument.Parse(s.AdvancedJson);
        if(json.RootElement.ValueKind!=JsonValueKind.Object || json.RootElement.TryGetProperty("contents",out _))
            throw new WorkspaceException(400,"הגדרות מתקדמות חייבות להיות אובייקט ללא היסטוריית שיחה");
    }
    public async Task SaveSettings(string owner,WorkspaceSettings settings,CancellationToken ct)
    {
        ValidateGeneration(settings.Generation);
        if(settings.MessagePageSize is <1 or >100||settings.ConversationPageSize is <1 or >100||settings.MaxBufferedTurns is <1 or >1000||settings.MaxBufferedBytes is <65536 or >67108864||settings.OverscanCount is <0 or >30||settings.PrefetchPages is <0 or >3||settings.RenderIntervalMs is <16 or >1000||settings.Theme is not ("dark" or "light"))throw new WorkspaceException(400,"הגדרות באפר אינן תקינות");
        await using var db=await Open(ct);await Execute(db,"INSERT INTO Preferences VALUES(@o,@j) ON CONFLICT(OwnerId) DO UPDATE SET Json=excluded.Json",null,ct,("o",owner),("j",JsonSerializer.Serialize(settings,NexusJson.Default.WorkspaceSettings)));
    }
    public async Task<PluginList> Plugins(string owner,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Json FROM Plugins WHERE OwnerId=@o ORDER BY Id",null,("o",owner));var list=new List<PluginDefinition>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(JsonSerializer.Deserialize(r.GetString(0),NexusJson.Default.PluginDefinition)!);return new(list.ToArray());
    }
    public async Task SavePlugin(string owner,PluginDefinition plugin,CancellationToken ct)
    {
        if(plugin.Id.Length is <1 or >80||plugin.Name.Length is <1 or >120||!PluginEngine.Kinds.Contains(plugin.Kind)||plugin.Execution is not ("local" or "server" or "auto" or "browser" or "maui")||plugin.ConfigurationJson.Length>16384||(plugin.Kind!="wasm"&&plugin.Version!="1"))throw new WorkspaceException(400,"הגדרת התוסף אינה נתמכת");
        using var json=JsonDocument.Parse(plugin.ConfigurationJson);if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new WorkspaceException(400,"נדרש אובייקט הגדרות");
        foreach(var field in new[]{"text","find"})if(json.RootElement.TryGetProperty(field,out var value)&&value.ValueKind!=JsonValueKind.String)throw new WorkspaceException(400,"ערכי התוסף חייבים להיות טקסט");
        await using var db=await Open(ct);await Execute(db,"INSERT INTO Plugins VALUES(@o,@id,@j) ON CONFLICT(OwnerId,Id) DO UPDATE SET Json=excluded.Json",null,ct,("o",owner),("id",plugin.Id),("j",JsonSerializer.Serialize(plugin,NexusJson.Default.PluginDefinition)));
    }
    public async Task DeletePlugin(string owner,string id,CancellationToken ct)
    {await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);await Execute(db,"DELETE FROM Plugins WHERE OwnerId=@o AND Id=@id",tx,ct,("o",owner),("id",id));await Execute(db,"DELETE FROM PluginPackages WHERE OwnerId=@o AND Id=@id",tx,ct,("o",owner),("id",id));await tx.CommitAsync(ct);}

    public async Task SavePluginPackage(string owner,PluginDefinition plugin,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
        await Execute(db,"INSERT INTO PluginPackages(OwnerId,Id,Version,ManifestJson,PackageReference,PackageHash,InstalledAt) VALUES(@o,@id,@v,@m,@p,@h,@t) ON CONFLICT(OwnerId,Id) DO UPDATE SET Version=excluded.Version,ManifestJson=excluded.ManifestJson,PackageReference=excluded.PackageReference,PackageHash=excluded.PackageHash,InstalledAt=excluded.InstalledAt",tx,ct,
            ("o",owner),("id",plugin.Id),("v",plugin.Version),("m",JsonSerializer.Serialize(plugin.Manifest,NexusJson.Default.PluginManifest)),("p",plugin.PackageReference),("h",plugin.PackageHash),("t",Now));
        await Execute(db,"INSERT INTO Plugins VALUES(@o,@id,@j) ON CONFLICT(OwnerId,Id) DO UPDATE SET Json=excluded.Json",tx,ct,("o",owner),("id",plugin.Id),("j",JsonSerializer.Serialize(plugin,NexusJson.Default.PluginDefinition)));await tx.CommitAsync(ct);
    }

    public async Task<PluginDefinition?> FindPluginForTool(string runId,string tool,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT p.Json FROM Plugins p JOIN Runs r ON r.OwnerId=p.OwnerId WHERE r.Id=@r",null,("r",runId));await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)){var plugin=JsonSerializer.Deserialize(reader.GetString(0),NexusJson.Default.PluginDefinition);if(plugin is {Enabled:true,Kind:"wasm",Manifest:not null}&&plugin.Execution is "server" or "auto"&&plugin.Manifest.Tools.Any(x=>x.Name==tool))return plugin;}
        return null;
    }

    public async Task SaveProviderOperation(string owner,ProviderOperation operation,CancellationToken ct)
    {
        await using var db=await Open(ct);
        await Execute(db,"INSERT INTO ProviderOperations(Id,OwnerId,Capability,ProviderName,Status,RequestJson,ResponseJson,CreatedAt,UpdatedAt) VALUES(@id,@o,@c,@n,@s,@q,@r,@t,@u)",null,ct,
            ("id",operation.Id),("o",owner),("c",operation.Capability),("n",operation.ProviderName),("s",operation.Status),("q",operation.RequestJson),("r",operation.ResponseJson),("t",operation.CreatedAt),("u",operation.UpdatedAt));
    }

    public async Task<ProviderOperationList> ProviderOperations(string owner,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Id,Capability,ProviderName,Status,RequestJson,ResponseJson,CreatedAt,UpdatedAt FROM ProviderOperations WHERE OwnerId=@o ORDER BY UpdatedAt DESC,Id DESC LIMIT 100",null,("o",owner));
        var list=new List<ProviderOperation>();await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))list.Add(new(reader.GetString(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7)));
        return new(list.ToArray());
    }

    public async Task SaveToolExecution(string id,string runId,string tool,string arguments,string result,string status,CancellationToken ct)
    {
        await using var db=await Open(ct);await Execute(db,"INSERT INTO ToolExecutions(Id,RunId,ToolName,ArgumentsJson,ResultJson,Status,CreatedAt) VALUES(@id,@r,@n,@a,@o,@s,@t)",null,ct,
            ("id",id),("r",runId),("n",tool),("a",arguments),("o",result),("s",status),("t",Now));
    }

    public async Task<RetentionPolicy> Retention(string owner,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT ConversationDays,FileDays,DeleteArchived FROM RetentionPolicies WHERE OwnerId=@o",null,("o",owner));await using var reader=await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2)!=0):new();
    }

    public async Task SaveRetention(string owner,RetentionPolicy policy,CancellationToken ct)
    {
        if(policy.ConversationDays is <0 or >36500||policy.FileDays is <0 or >36500)throw new WorkspaceException(400,"מדיניות השימור אינה תקינה");
        await using var db=await Open(ct);await Execute(db,"INSERT INTO RetentionPolicies(OwnerId,ConversationDays,FileDays,DeleteArchived,UpdatedAt) VALUES(@o,@c,@f,@d,@t) ON CONFLICT(OwnerId) DO UPDATE SET ConversationDays=excluded.ConversationDays,FileDays=excluded.FileDays,DeleteArchived=excluded.DeleteArchived,UpdatedAt=excluded.UpdatedAt",null,ct,("o",owner),("c",policy.ConversationDays),("f",policy.FileDays),("d",policy.DeleteArchived?1:0),("t",Now));
    }

    public async Task ApplyRetention(CancellationToken ct)
    {
        await using var db=await Open(ct);var expired=new List<string>();
        await using(var cmd=Command(db,"SELECT c.Id,c.UpdatedAt,p.ConversationDays FROM Conversations c JOIN RetentionPolicies p ON p.OwnerId=c.OwnerId WHERE p.DeleteArchived=1 AND p.ConversationDays>0 AND c.Archived=1",null))
        await using(var reader=await cmd.ExecuteReaderAsync(ct)){while(await reader.ReadAsync(ct))if(DateTimeOffset.Parse(reader.GetString(1))<DateTimeOffset.UtcNow.AddDays(-reader.GetInt32(2)))expired.Add(reader.GetString(0));}
        foreach(var id in expired)
        {
            await using var tx=await db.BeginTransactionAsync(ct);
            await Execute(db,"DELETE FROM RunEvents WHERE RunId IN (SELECT Id FROM Runs WHERE ConversationId=@id)",tx,ct,("id",id));await Execute(db,"DELETE FROM RunMetrics WHERE RunId IN (SELECT Id FROM Runs WHERE ConversationId=@id)",tx,ct,("id",id));await Execute(db,"DELETE FROM RunFingerprints WHERE RunId IN (SELECT Id FROM Runs WHERE ConversationId=@id)",tx,ct,("id",id));await Execute(db,"DELETE FROM ToolExecutions WHERE RunId IN (SELECT Id FROM Runs WHERE ConversationId=@id)",tx,ct,("id",id));await Execute(db,"DELETE FROM Outbox WHERE RunId IN (SELECT Id FROM Runs WHERE ConversationId=@id)",tx,ct,("id",id));await Execute(db,"DELETE FROM Messages WHERE ConversationId=@id",tx,ct,("id",id));await Execute(db,"DELETE FROM Runs WHERE ConversationId=@id",tx,ct,("id",id));await Execute(db,"DELETE FROM Conversations WHERE Id=@id",tx,ct,("id",id));await tx.CommitAsync(ct);
        }
    }

    public async Task<(int Queued,int Running)> QueueDepth(CancellationToken ct)
    {
#if NEXUS_NATIVE_AOT
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT COALESCE(SUM(CASE WHEN Status='queued' THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN Status='running' THEN 1 ELSE 0 END),0) FROM Runs",null);await using var reader=await cmd.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);return(Convert.ToInt32(reader.GetValue(0)),Convert.ToInt32(reader.GetValue(1)));
#else
        await using var db=await Open(ct);ct.ThrowIfCancellationRequested();var row=await db.QuerySingleAsync<QueueDepthRow>("SELECT COALESCE(SUM(CASE WHEN Status='queued' THEN 1 ELSE 0 END),0) AS Queued,COALESCE(SUM(CASE WHEN Status='running' THEN 1 ELSE 0 END),0) AS Running FROM Runs");return(checked((int)row.Queued),checked((int)row.Running));
#endif
    }
#if !NEXUS_NATIVE_AOT
    private sealed record QueueDepthRow(long Queued,long Running);
#endif
}
