using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
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

        var arguments = result.VariableType.FindInterfaceThatCloses(typeof(ICompiledQuery<,>))!.GetGenericArguments();

        var methodCall = typeof(MartenWriteArrayMethodCall<,>).CloseAndBuildAs<MethodCall>(result, (_responseType, _successStatusCode), arguments);

        chain.Postprocessors.Add(methodCall);
        return true;
    }
}

public class MartenWriteArrayMethodCall<TDoc, TOut> : MethodCall
{
    public MartenWriteArrayMethodCall(Variable resultVariable, (string responseType, int successStatusCode) options) : base(typeof(QueryableExtensions), FindMethod(resultVariable))
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