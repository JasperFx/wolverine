using MassTransit;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi;

public class CreateEndpoint
{
    [WolverinePost("/issue")]
    public (IssueCreated, InsertDoc<Issue>) Create(CreateIssue command)
    {
        var id = NewId.NextSequentialGuid();
        var issue = new Issue
        {
            Id = id, Title = command.Title
        };

        return (new IssueCreated(id), MartenOps.Insert(issue));
    }
}

public record CreateIssue(string Title);

public record IssueCreated(Guid Id) : CreationResponse($"/issue/{Id}");

public class Issue
{
    public Guid Id { get; set; }
    public string Title { get; set; }
}