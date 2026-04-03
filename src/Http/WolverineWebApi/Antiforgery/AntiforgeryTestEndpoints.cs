using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi.Antiforgery;

public class AntiforgeryTestEndpoints
{
    [WolverinePost("/antiforgery/form")]
    public string PostForm([FromForm] string name) => name;

    [WolverinePost("/antiforgery/json")]
    public string PostJson(AntiforgeryDto dto) => dto.Name;

    [WolverinePost("/antiforgery/form-disabled")]
    [DisableAntiforgery]
    public string PostFormDisabled([FromForm] string name) => name;

    [WolverinePost("/antiforgery/json-required")]
    [ValidateAntiforgery]
    public string PostJsonRequired(AntiforgeryDto dto) => dto.Name;
}

[DisableAntiforgery]
public class DisabledAntiforgeryEndpoints
{
    [WolverinePost("/antiforgery/class-disabled")]
    public string PostForm([FromForm] string name) => name;
}

public record AntiforgeryDto(string Name);
