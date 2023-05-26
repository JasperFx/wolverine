using System.Diagnostics;
using Marten;
using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

public class ServiceEndpoints
{
    [SpecialModifyHttpChain]
    [WolverineGet("/data/{id}")]
    public Task<Data?> GetData(Guid id, IDocumentSession session)
    {
        return session.LoadAsync<Data>(id);
    }

    [WolverinePost("/publish/marten/message")]
    public async Task PublishData(Data data, IMessageBus bus, IDocumentSession session)
    {
        session.Store(data);
        await bus.PublishAsync(new Data { Id = data.Id, Name = data.Name });
    }

    [WolverineGet("/message/{message}")]
    public string GetMessage(string message, Recorder recorder)
    {
        recorder.Actions.Add("Got: " + message);
        return $"Message was {message}";
    }
}

public class DataHandler
{
    public void Handle(Data data)
    {
        Debug.WriteLine("Got me a data");
    }
}

public class Data
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class Recorder
{
    public readonly List<string> Actions = new();
}