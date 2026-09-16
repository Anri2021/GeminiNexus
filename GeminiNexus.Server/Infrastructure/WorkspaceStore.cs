using System.Data;
using System.Data.Common;
using System.Text.Json;
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
    Task<MessagePage> Messages(string owner, string conversation, long? before, int take, CancellationToken ct);
    Task<Run> Enqueue(string owner, SubmitRun request, CancellationToken ct);
    Task<Run?> FindRun(string owner, string id, CancellationToken ct);
    Task<RunEvent[]> Events(string owner, string id, long after, int take, CancellationToken ct);
}

// Explicit ADO.NET mapping is reflection-free and shared by SQLite and PostgreSQL.
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
        await Execute(db, """
        CREATE TABLE IF NOT EXISTS NexusUsers(Id TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE, PasswordHash TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Conversations(Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL, Title TEXT NOT NULL, Model TEXT NOT NULL, UpdatedAt TEXT NOT NULL, Pinned INTEGER NOT NULL DEFAULT 0, Archived INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS IX_Conversations_Owner ON Conversations(OwnerId, Archived, Pinned, UpdatedAt, Id);
        CREATE TABLE IF NOT EXISTS Messages(Id TEXT PRIMARY KEY, ConversationId TEXT NOT NULL, RunId TEXT NOT NULL, Ordinal BIGINT NOT NULL, Role TEXT NOT NULL, Content TEXT NOT NULL, PartsJson TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status TEXT NOT NULL, UNIQUE(ConversationId, Ordinal));
        CREATE TABLE IF NOT EXISTS Runs(Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL, ConversationId TEXT NOT NULL, Model TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, LastSequence BIGINT NOT NULL DEFAULT 0, Error TEXT, CancelRequested INTEGER NOT NULL DEFAULT 0, IdempotencyKey TEXT NOT NULL, RequestJson TEXT NOT NULL, LeaseOwner TEXT, LeaseUntil BIGINT NOT NULL DEFAULT 0, UNIQUE(OwnerId, IdempotencyKey));
        CREATE INDEX IF NOT EXISTS IX_Runs_Queue ON Runs(Status, CreatedAt);
        CREATE INDEX IF NOT EXISTS IX_Runs_Owner ON Runs(OwnerId, ConversationId, Status);
        CREATE TABLE IF NOT EXISTS RunEvents(RunId TEXT NOT NULL, Sequence BIGINT NOT NULL, Kind TEXT NOT NULL, Json TEXT NOT NULL, CreatedAt TEXT NOT NULL, PRIMARY KEY(RunId, Sequence));
        CREATE TABLE IF NOT EXISTS Outbox(RunId TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Delivered INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS Preferences(OwnerId TEXT PRIMARY KEY, Json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS Plugins(OwnerId TEXT NOT NULL, Id TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(OwnerId, Id));
        """, null, ct);
        if (options.AdminPassword.Length < 16) throw new InvalidOperationException("Auth:AdminPassword must contain at least 16 characters (or supply Auth:AdminPasswordFile).");
        // Password rotation takes effect at startup; persisted sessions can be revoked by deleting the key ring.
        await Execute(db, "INSERT INTO NexusUsers(Id,Name,PasswordHash) VALUES(@id,@name,@hash) ON CONFLICT(Name) DO UPDATE SET PasswordHash=excluded.PasswordHash", null, ct,
            ("id", Guid.NewGuid().ToString("N")), ("name", options.AdminName), ("hash", Passwords.Hash(options.AdminPassword)));
    }
    public async Task<UserInfo?> Login(string name, string password, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var cmd = Command(db, "SELECT Id,Name,PasswordHash FROM NexusUsers WHERE Name=@name", null, ("name", name));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) { Passwords.Verify(password, Passwords.Hash("unavailable-user")); return null; }
        return Passwords.Verify(password, r.GetString(2)) ? new(r.GetString(0), r.GetString(1)) : null;
    }
    public async Task<UserInfo> AddUser(string name, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || password.Length is < 16 or > 1024) throw new WorkspaceException(400, "שם או סיסמה אינם תקינים");
        var user = new UserInfo(Guid.NewGuid().ToString("N"), name);
        await using var db = await Open(ct); await Execute(db, "INSERT INTO NexusUsers VALUES(@id,@name,@hash)", null, ct, ("id",user.Id),("name",name),("hash",Passwords.Hash(password))); return user;
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
    public async Task<MessagePage> Messages(string owner,string conversation,long? before,int take,CancellationToken ct)
    {
        await using var db=await Open(ct);await RequireOwner(db,owner,conversation,ct);take=Math.Clamp(take,1,100);
        var list=await ReadMessages(db,conversation,before,take+1,ct);
        var more=list.Count>take||list.Count>1&&list.Sum(m=>(long)(m.Content.Length+m.PartsJson.Length)*2)>=options.MaxHistoryBytes;
        if(more)list.RemoveAt(list.Count-1);list.Reverse();
        return new(list.ToArray(),more,list.Count>0?list[0].Ordinal:null);
    }
    private async Task<List<Message>> ReadMessages(DbConnection db,string id,long? before,int take,CancellationToken ct,DbTransaction? tx=null)
    {
        await using var cmd=Command(db,"SELECT Id,ConversationId,RunId,Ordinal,Role,Content,PartsJson,CreatedAt,Status FROM Messages WHERE ConversationId=@id AND Ordinal<@before ORDER BY Ordinal DESC LIMIT @take",tx,("id",id),("before",before??long.MaxValue),("take",take));
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
    public async Task<Run> Enqueue(string owner,SubmitRun request,CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            await RequireOwner(db,owner,request.ConversationId,ct,tx);
            await using(var existing=Command(db,"SELECT Id,ConversationId,Model,Status,CreatedAt,UpdatedAt,LastSequence,Error,CancelRequested FROM Runs WHERE OwnerId=@o AND IdempotencyKey=@k",tx,("o",owner),("k",request.IdempotencyKey)))
            {await using var r=await existing.ExecuteReaderAsync(ct);if(await r.ReadAsync(ct))return RunFrom(r);}
            await using(var active=Command(db,"SELECT COUNT(*) FROM Runs WHERE ConversationId=@id AND Status IN ('queued','running')",tx,("id",request.ConversationId)))
                if(Convert.ToInt64(await active.ExecuteScalarAsync(ct))>0)throw new WorkspaceException(409,"כבר יש ריצה בשיחה. אפשר ליצור הסתעפות כדי לעבוד במקביל.");
            await using(var count=Command(db,"SELECT COUNT(*) FROM Runs WHERE OwnerId=@o AND Status IN ('queued','running')",tx,("o",owner)))
                if(Convert.ToInt64(await count.ExecuteScalarAsync(ct))>=options.MaxQueuedPerUser)throw new WorkspaceException(429,"תור העיבוד מלא");
            var history=await ReadMessages(db,request.ConversationId,null,request.Settings.ContextMaxTurns*2,ct,tx);history.Reverse();
            // Preserve all provider parts, including thought signatures and tool responses.
            var contents=new System.Text.Json.Nodes.JsonArray();
            foreach(var m in history.Where(m=>m.Status=="completed"))
                contents.Add(new System.Text.Json.Nodes.JsonObject{["role"]=m.Role,["parts"]=System.Text.Json.Nodes.JsonNode.Parse(m.PartsJson)});
            var parts=new System.Text.Json.Nodes.JsonArray{new System.Text.Json.Nodes.JsonObject{["text"]=request.Prompt}};
            foreach(var a in request.Attachments??[])parts.Add(new System.Text.Json.Nodes.JsonObject{["inlineData"]=new System.Text.Json.Nodes.JsonObject{["mimeType"]=a.MimeType,["data"]=a.Data}});
            contents.Add(new System.Text.Json.Nodes.JsonObject{["role"]="user",["parts"]=parts.DeepClone()});
            var run=new Run(Guid.NewGuid().ToString("N"),request.ConversationId,request.Model,"queued",Now,Now,0,null,false);
            var stored=JsonSerializer.Serialize(new StoredRunRequest(request,contents.ToJsonString()),NexusJson.Default.StoredRunRequest);
            await Execute(db,"INSERT INTO Runs(Id,OwnerId,ConversationId,Model,Status,CreatedAt,UpdatedAt,IdempotencyKey,RequestJson) VALUES(@id,@o,@c,@m,'queued',@t,@t,@k,@j)",tx,ct,("id",run.Id),("o",owner),("c",run.ConversationId),("m",run.Model),("t",run.CreatedAt),("k",request.IdempotencyKey),("j",stored));
            await using var next=Command(db,"SELECT COALESCE(MAX(Ordinal),0)+1 FROM Messages WHERE ConversationId=@c",tx,("c",run.ConversationId));var ordinal=Convert.ToInt64(await next.ExecuteScalarAsync(ct));
            await InsertMessage(db,tx,new(Guid.NewGuid().ToString("N"),run.ConversationId,run.Id,ordinal,"user",request.Prompt,parts.ToJsonString(),Now,"completed"),ct);
            await Execute(db,"UPDATE Conversations SET UpdatedAt=@t,Model=@m,Title=CASE WHEN Title='שיחה חדשה' THEN @title ELSE Title END WHERE Id=@id",tx,ct,("t",Now),("m",request.Model),("title",request.Prompt[..Math.Min(70,request.Prompt.Length)]),("id",run.ConversationId));
            await Execute(db,"INSERT INTO Outbox VALUES(@id,@t,0)",tx,ct,("id",run.Id),("t",Now));await tx.CommitAsync(ct);return run;
        }
        finally{writeGate.Release();}
    }
    private static Run RunFrom(DbDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6),r.IsDBNull(7)?null:r.GetString(7),r.GetInt32(8)!=0);
    public async Task<Run?> FindRun(string owner,string id,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Id,ConversationId,Model,Status,CreatedAt,UpdatedAt,LastSequence,Error,CancelRequested FROM Runs WHERE Id=@id AND OwnerId=@o",null,("id",id),("o",owner));await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?RunFrom(r):null;
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
            await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            await using var cmd=Command(db,"""
            SELECT r.Id,r.ConversationId,r.Model,r.Status,r.CreatedAt,r.UpdatedAt,r.LastSequence,r.Error,r.CancelRequested,r.OwnerId,r.RequestJson
            FROM Runs r JOIN Outbox o ON o.RunId=r.Id WHERE r.Status='queued' AND o.Delivered=0
            AND (SELECT COUNT(*) FROM Runs active WHERE active.OwnerId=r.OwnerId AND active.Status='running')<@max
            ORDER BY r.CreatedAt LIMIT 1
            """,tx,("max",options.PerUserConcurrency));
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
    public async Task Finish(string id,string worker,string content,string parts,string status,string? error,CancellationToken ct,bool expiredOnly=false)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            await using var db=await Open(ct);await using var tx=await db.BeginTransactionAsync(ct);
            await using var cmd=Command(db,"UPDATE Runs SET Status=@s,Error=@e,UpdatedAt=@t,LeaseUntil=0,RequestJson='',LastSequence=LastSequence+1 WHERE Id=@id AND LeaseOwner=@w AND Status='running' AND (@expired=0 OR LeaseUntil<@now) RETURNING ConversationId",tx,("s",status),("e",error),("t",Now),("id",id),("w",worker),("expired",expiredOnly?1:0),("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var c=await cmd.ExecuteScalarAsync(ct) as string;if(c is null)return;
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
    }
    public async Task<RunEvent[]> Events(string owner,string id,long after,int take,CancellationToken ct)
    {
        if(await FindRun(owner,id,ct) is null)throw new WorkspaceException(404,"הריצה לא נמצאה");
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT RunId,Sequence,Kind,Json,CreatedAt FROM RunEvents WHERE RunId=@id AND Sequence>@s ORDER BY Sequence LIMIT @take",null,("id",id),("s",after),("take",Math.Clamp(take,1,200)));
        var list=new List<RunEvent>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(new(r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4)));return list.ToArray();
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
        if(settings.MessagePageSize is <1 or >100||settings.ConversationPageSize is <1 or >100||settings.MaxBufferedTurns is <1 or >1000||settings.MaxBufferedBytes is <65536 or >67108864||settings.OverscanCount is <0 or >30||settings.RenderIntervalMs is <16 or >1000||settings.Theme is not ("dark" or "light"))throw new WorkspaceException(400,"הגדרות באפר אינן תקינות");
        await using var db=await Open(ct);await Execute(db,"INSERT INTO Preferences VALUES(@o,@j) ON CONFLICT(OwnerId) DO UPDATE SET Json=excluded.Json",null,ct,("o",owner),("j",JsonSerializer.Serialize(settings,NexusJson.Default.WorkspaceSettings)));
    }
    public async Task<PluginList> Plugins(string owner,CancellationToken ct)
    {
        await using var db=await Open(ct);await using var cmd=Command(db,"SELECT Json FROM Plugins WHERE OwnerId=@o ORDER BY Id",null,("o",owner));var list=new List<PluginDefinition>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(JsonSerializer.Deserialize(r.GetString(0),NexusJson.Default.PluginDefinition)!);return new(list.ToArray());
    }
    public async Task SavePlugin(string owner,PluginDefinition plugin,CancellationToken ct)
    {
        if(plugin.Id.Length is <1 or >80||plugin.Name.Length is <1 or >120||!PluginEngine.Kinds.Contains(plugin.Kind)||plugin.Execution is not ("local" or "server")||plugin.ConfigurationJson.Length>16384||plugin.Version!="1")throw new WorkspaceException(400,"הגדרת התוסף אינה נתמכת");
        using var json=JsonDocument.Parse(plugin.ConfigurationJson);if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new WorkspaceException(400,"נדרש אובייקט הגדרות");
        foreach(var field in new[]{"text","find"})if(json.RootElement.TryGetProperty(field,out var value)&&value.ValueKind!=JsonValueKind.String)throw new WorkspaceException(400,"ערכי התוסף חייבים להיות טקסט");
        await using var db=await Open(ct);await Execute(db,"INSERT INTO Plugins VALUES(@o,@id,@j) ON CONFLICT(OwnerId,Id) DO UPDATE SET Json=excluded.Json",null,ct,("o",owner),("id",plugin.Id),("j",JsonSerializer.Serialize(plugin,NexusJson.Default.PluginDefinition)));
    }
    public async Task DeletePlugin(string owner,string id,CancellationToken ct)
    {await using var db=await Open(ct);await Execute(db,"DELETE FROM Plugins WHERE OwnerId=@o AND Id=@id",null,ct,("o",owner),("id",id));}
}
