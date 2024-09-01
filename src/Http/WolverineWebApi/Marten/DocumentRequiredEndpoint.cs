using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.Marten;

namespace WolverineWebApi.Marten;

public class DocumentRequiredEndpoint
{
    public static ProblemDetails Load(Invoice? invoice)
    {
        if (invoice is null)
        {
            return new ProblemDetails
            {
                Title = "Invoice is not found",
                Detail = "We only get here with [Document][Required]",
                Status = 404,
            };
        }

        return WolverineContinue.NoProblems;
    }
    
    [WolverineGet("document-required/separate-attributes/{id}")]
    public static Invoice SeparateAttributes([Document(Required = false)][Required] Invoice invoice)
    {
        return invoice;
    }
    
    [WolverineGet("document-required/document-attribute-only/{id}")]
    public static Invoice DocumentAttributeOnly([Document] Invoice invoice)
    {
        return invoice;
    }
}