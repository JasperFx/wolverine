using JasperFx.Core;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Spectre.Console;
using Wolverine.Http;

namespace WolverineWebApi;

//Uses same test data as QuerystringEndpoints
public static class FormEndpoints{
    [WolverinePost("/form/enum")]
    public static string UsingEnumQuerystring([FromForm]Direction direction)
    {
        return direction.ToString();
    }

    [WolverinePost("/form/explicit")]
    public static string UsingEnumQuerystring([FromForm(Name = "name")] string value)
    {
        return value ?? "";
    }

    [WolverinePost("/form/enum/nullable")]
    public static string UsingNullableEnumQuerystring([FromForm]Direction? direction)
    {
        return direction?.ToString() ?? "none";
    }

    [WolverinePost("/form/stringarray")]
    public static string StringArray([FromForm]string[]? values)
    {
        if (values == null || values.IsEmpty()) return "none";

        return values.Join(",");
    }

    [WolverinePost("/form/intarray")]
    public static string IntArray([FromForm]int[]? values)
    {
        if (values == null || values.IsEmpty()) return "none";

        return values.OrderBy(x => x).Select(x => x.ToString()).Join(",");
    }

    [WolverinePost("/form/datetime")]
    public static string DateTime([FromForm]DateTime value)
    {
        return value.ToString("O");
    }
    
    [WolverinePost("/form/datetime2")]
    public static string DateTime2([FromForm] DateTime value)
    {
        return value.ToString("O");
    }

    [WolverinePost("/form/datetime/nullable")]
    public static string DateTimeNullable([FromForm]DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("O") : "Value is missing";
    }

    [WolverinePost("/form/dateonly")]
    public static string DateOnly([FromForm]DateOnly value)
    {
        return value.ToString("O");
    }

    [WolverinePost("/form/dateonly/nullable")]
    public static string DateOnlyNullable([FromForm]DateOnly? value)
    {
        return value.HasValue ? value.Value.ToString("O") : "Value is missing";
    }
}


public static class FromFormEndpoints{
    [WolverinePost("/api/fromform1")]
    public static Query1 Post([FromForm] Query1 query) => query;

    [WolverinePost("/api/fromform2")]
    public static Query2 Post([FromForm] Query2 query) => query;

    [WolverinePost("/api/fromform3")]
    public static Query3 Post([FromForm] Query3 query) => query;

    [WolverinePost("/api/fromform4")]
    public static Query4 Post([FromForm] Query4 query) => query;

    #region sample_using_[fromform]_binding
    [WolverinePost("/api/fromformbigquery")]
    public static BigQuery Post([FromForm] BigQuery query) => query;
    #endregion
}

#region sample_using_as_parameters_binding
public static class AsParametersEndpoints{
    [WolverinePost("/api/asparameters1")]
    public static AsParametersQuery Post([AsParameters] AsParametersQuery query)
    {
        return query;
    }
}

public class AsParametersQuery{
    [FromQuery]
    public Direction EnumFromQuery{ get; set; }
    [FromForm]
    public Direction EnumFromForm{ get; set; }

    public Direction EnumNotUsed{get;set;}

    [FromQuery]
    public string StringFromQuery { get; set; }
    [FromForm]
    public string StringFromForm { get; set; }
    public string StringNotUsed { get; set; }
    [FromQuery]
    public int IntegerFromQuery { get; set; }
    [FromForm]
    public int IntegerFromForm { get; set; }
    public int IntegerNotUsed { get; set; }
    [FromQuery]
    public float FloatFromQuery { get; set; }
    [FromForm]
    public float FloatFromForm { get; set; }
    public float FloatNotUsed { get; set; }
    [FromQuery]
    public bool BooleanFromQuery { get; set; }
    [FromForm]
    public bool BooleanFromForm { get; set; }
    public bool BooleanNotUsed { get; set; }
}
#endregion