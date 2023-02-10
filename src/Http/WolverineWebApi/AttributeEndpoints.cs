using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class AttributeEndpoints
{
    [HttpPost("/fromservices")]
    public string PostFromServices([FromServices] Recorder recorder)
    {
        recorder.Actions.Add("Called AttributesEndpoints.Post()");
        return "all good";
    }
    
    [HttpPost("/notbody")]
    public string PostNotBody([NotBody] Recorder recorder)
    {
        recorder.Actions.Add("Called AttributesEndpoints.Post()");
        return "all good";
    }
}