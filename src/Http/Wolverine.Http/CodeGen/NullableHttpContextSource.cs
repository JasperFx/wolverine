using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.CodeGen;

internal class NullableHttpContextSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(HttpContext);
    }

    public Variable Create(Type type)
    {
        return new Variable(typeof(HttpContext), "null");
    }
}

