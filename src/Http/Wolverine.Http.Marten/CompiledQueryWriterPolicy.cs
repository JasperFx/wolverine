using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq;
using Wolverine.Http.Resources;
using QueryableExtensions = Marten.AspNetCore.QueryableExtensions;

namespace Wolverine.Http.Marten;

#nullable enable

public class CompiledQueryWriterPolicy : IResourceWriterPolicy
{
    private readonly string _responseType;
    private readonly int _successStatusCode;

    public CompiledQueryWriterPolicy(string responseType = "application/json", int successStatusCode = 200)
    {
        _responseType = responseType;
        _successStatusCode = successStatusCode;
    }
    
    public bool TryApply(HttpChain chain)
    {
        var result = chain.Method.Creates.FirstOrDefault();
        if (result is null) return false;

        // Are we dealing with a compiled query or not?
        var compiledQueryClosure = result.VariableType.FindInterfaceThatCloses(typeof(ICompiledQuery<,>));
        if (compiledQueryClosure is null) return false;

        var arguments = compiledQueryClosure.GetGenericArguments();
        
        // If we're dealing with a primitive return type we need to write its string representation directly
        if (arguments[1].IsPrimitive)
        {
            // This call runs the query
            var queryCall =
                typeof(MartenQueryMethodCall<,>).CloseAndBuildAs<MethodCall>(result, arguments);
            chain.Postprocessors.Add(queryCall);

            // This call writes the response directly to the HttpContext as a string
            var writeStringCall = MethodCall.For<HttpHandler>(handler => HttpHandler.WriteString(null!, ""));
            writeStringCall.Arguments[1] = new Variable(queryCall.ReturnVariable!.VariableType,
                $"{queryCall.ReturnVariable.Usage}.ToString()", queryCall);
            chain.Postprocessors.Add(writeStringCall);
        }
        else
        {

            var writeJsonCall =
                typeof(MartenWriteJsonToStreamMethodCall<,>).CloseAndBuildAs<MethodCall>(result,
                    (_responseType, _successStatusCode), arguments);
            chain.Postprocessors.Add(writeJsonCall);
        }

        return true;
    }
}

public class MartenQueryMethodCall<TDoc, TOut> : MethodCall
{
    public MartenQueryMethodCall(Variable resultVariable) : base(typeof(IDocumentSession), FindMethod())
    {
        Arguments[0] = resultVariable;
    }

    static MethodInfo FindMethod()
    {
        return ReflectionHelper.GetMethod<IDocumentSession>(x =>
            x.QueryAsync((ICompiledQuery<TDoc, TOut>)null!, default))!;
    }
}

public class MartenWriteJsonToStreamMethodCall<TDoc, TOut> : MethodCall
{
    public MartenWriteJsonToStreamMethodCall(Variable resultVariable, (string responseType, int successStatusCode) options) : base(typeof(QueryableExtensions), FindMethod(resultVariable))
    {
        Arguments[1] = resultVariable;
        Arguments[3] = Constant.ForString(options.responseType);
        Arguments[4] = Constant.For(options.successStatusCode);
    }

    static MethodInfo FindMethod(Variable resultVariable)
    {
        // If we are dealing with a list type we want WriteArray, otherwise we want WriteOne
        return resultVariable.VariableType.Closes(typeof(ICompiledListQuery<,>))
            ? ReflectionHelper.GetMethod((string x) =>
                QueryableExtensions.WriteArray(null!, (ICompiledQuery<TDoc, TOut>)null!, null!, null!, 0))!
            : ReflectionHelper.GetMethod((string x) =>
                QueryableExtensions.WriteOne(null!, (ICompiledQuery<TDoc, TOut>)null!, null!, null!, 0))!;
    }
}
#nullable disable