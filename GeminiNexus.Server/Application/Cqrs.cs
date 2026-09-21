using System.Text.Json;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Server.Providers;
using GeminiNexus.Shared;

namespace GeminiNexus.Server.Application;

public interface ICommand<TResult>{}
public interface IQuery<TResult>{}
public interface ICommandHandler<in TCommand,TResult> where TCommand:ICommand<TResult>{ValueTask<TResult> Handle(TCommand command,CancellationToken ct);}
public interface IQueryHandler<in TQuery,TResult> where TQuery:IQuery<TResult>{ValueTask<TResult> Handle(TQuery query,CancellationToken ct);}

public sealed record ExecuteProviderOperationCommand(string Owner,ProviderOperationRequest Request):ICommand<ProviderOperation>;
public sealed class ExecuteProviderOperationHandler(IProviderOperations provider,WorkspaceStore store):ICommandHandler<ExecuteProviderOperationCommand,ProviderOperation>
{
    public async ValueTask<ProviderOperation> Handle(ExecuteProviderOperationCommand command,CancellationToken ct)
    {
        var response=await provider.Execute(command.Request,ct);var now=DateTimeOffset.UtcNow.ToString("O");
        var operation=new ProviderOperation(Guid.NewGuid().ToString("N"),command.Request.Capability,command.Request.Resource,response.Status is >=200 and <300?"completed":"failed",JsonSerializer.Serialize(command.Request,NexusJson.Default.ProviderOperationRequest),response.Body,now,now);
        await store.SaveProviderOperation(command.Owner,operation,ct);return operation;
    }
}

public sealed record GetProviderOperationsQuery(string Owner):IQuery<ProviderOperationList>;
public sealed class GetProviderOperationsHandler(WorkspaceStore store):IQueryHandler<GetProviderOperationsQuery,ProviderOperationList>
{public async ValueTask<ProviderOperationList> Handle(GetProviderOperationsQuery query,CancellationToken ct)=>await store.ProviderOperations(query.Owner,ct);}
