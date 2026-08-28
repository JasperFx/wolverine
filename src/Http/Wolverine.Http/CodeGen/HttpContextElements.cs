using System.Linq.Expressions;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class HttpContextElements : IParameterStrategy
{
    private readonly List<PropertyInfo> _properties;

    public HttpContextElements()
    {
        // GH-4171: IServiceProvider is deliberately excluded. HttpContext.RequestServices is one of
        // these properties, and matching it here quietly bound every IServiceProvider parameter to
        // httpContext.RequestServices regardless of ServiceProviderSource. Let the normal service
        // machinery answer IServiceProvider instead.
        _properties = typeof(HttpContext).GetProperties().Where(x =>
                x.PropertyType != typeof(string) && x.PropertyType != typeof(IServiceProvider))
            .ToList();
    }

    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        variable = default!;

        var prop = _properties.FirstOrDefault(x => x.PropertyType == parameter.ParameterType);
        if (prop != null)
        {
            variable = new Variable(parameter.ParameterType, $"httpContext.{prop.Name}");
            return true;
        }

        if (parameter.ParameterType == typeof(string) &&
            parameter.Name!.EqualsIgnoreCase(nameof(HttpContext.TraceIdentifier)))
        {
            variable = new Variable(parameter.ParameterType, $"httpContext.{nameof(HttpContext.TraceIdentifier)}");
            return true;
        }

        if (parameter.ParameterType == typeof(CancellationToken))
        {
            variable = For(x => x.RequestAborted);
            return true;
        }

        if (parameter.ParameterType == typeof(HttpRequest))
        {
            variable = For(x => x.Request);
            return true;
        }

        return false;
    }

    public static Variable For(Expression<Func<HttpContext, object>> expression)
    {
        var property = ReflectionHelper.GetProperty(expression);
        return new Variable(property.PropertyType, $"httpContext.{property.Name}");
    }
}