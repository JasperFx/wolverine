using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class AttributeEndpoints
{
    [WolverinePost("/fromservices")]
    public string PostFromServices([FromServices] Recorder recorder)
    {
        recorder.Actions.Add("Called AttributesEndpoints.Post()");
        return "all good";
    }

    #region sample_using_not_body_attribute

    [WolverinePost("/notbody")]
    // The Recorder parameter will be sourced as an IoC service
    // instead of being treated as the HTTP request body
    public string PostNotBody([NotBody] Recorder recorder)
    {
        recorder.Actions.Add("Called AttributesEndpoints.Post()");
        return "all good";
    }

    #endregion
}