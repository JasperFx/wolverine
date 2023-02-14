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
    
    [WolverinePost("/notbody")]
    public string PostNotBody([NotBody] Recorder recorder)
    {
        recorder.Actions.Add("Called AttributesEndpoints.Post()");
        return "all good";
    }
}