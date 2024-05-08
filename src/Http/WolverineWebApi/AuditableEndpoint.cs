using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

public class AuditableEndpoint
{

    [WolverinePost("/auditable/post"), EmptyResponse]
    public string Post(AuditablePostBody body)
    {
        return "Hello";
    }

    [WolverinePost("/auditable/empty"), EmptyResponse]
    public void EmptyPost(AuditablePostBody command)
    {
    }
}

public class AuditablePostBody
{
    [Audit]
    public Guid Id { get; set; }
}
