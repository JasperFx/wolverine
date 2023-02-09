using System.Diagnostics;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace WolverineWebApi;

public class ServiceEndpoints
{
    [Special]
    [HttpGet("/data/{id}")]
    public Task<Data?> GetData(Guid id, IDocumentSession session)
    {
        return session.LoadAsync<Data>(id);
    }

    [HttpPost("/publish/marten/message")]
    public async Task PublishData(Data data, IMessageBus bus, IDocumentSession session)
    {
        session.Store(data);
        await bus.PublishAsync(new Data { Id = data.Id, Name = data.Name });
    }

    [HttpGet("/message/{message}")]
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