using Marten;
using Microsoft.AspNetCore.Mvc;

namespace WolverineWebApi;

public class ServiceEndpoints
{
    [HttpGet("/data/{id}")]
    public Task<Data?> GetData(Guid id, IDocumentSession session)
    {
        return session.LoadAsync<Data>(id);
    }
}

public class Data
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}