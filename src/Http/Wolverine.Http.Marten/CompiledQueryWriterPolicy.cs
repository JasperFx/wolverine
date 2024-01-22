using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.RuntimeCompiler;
using Marten.Linq;
using Wolverine.Http.Resources;
using QueryableExtensions = Marten.AspNetCore.QueryableExtensions;

namespace Wolverine.Http.Marten;

#nullable enable

public class CompiledQueryWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        bool ImplementsGenericInterface(Type type, Type assigned, [NotNullWhen(true)] out Type[]? types)
        {
            types = null;
            var result = type.GetInterfaces().FirstOrDefault(x =>
                x.IsGenericType && x.GetGenericTypeDefinition() == assigned);
            if (result is null) return false;
            types = result.GetGenericArguments();
            return true;
        }

        var result = chain.Method.Creates.FirstOrDefault();
        if (result is null) return false;
        // Are we dealing with a compiled query or not?
        if (ImplementsGenericInterface(result.VariableType, typeof(ICompiledQuery<,>), out var arguments))
        {
            MethodInfo methodToCall;
            
            // Is it a compiled list query? 
            if (ImplementsGenericInterface(result.VariableType, typeof(ICompiledListQuery<,>), out _))
            {
                // Cannot use GetMethod here because its ambiguous, can't use GetMethod with type parameters because of
                // WriteArray being a generic method where binding on types passed doesn't work.
                var method = typeof(QueryableExtensions).GetMethods().Where(x =>
                    x is { Name: nameof(QueryableExtensions.WriteArray), IsGenericMethod: true } &&
                    x.GetGenericArguments().Length == 2).ToArray();
                if (method.Length != 1)
                {
                    throw new CodeGenerationException(method, new Exception(
                        "Cannot find correct method for Marten.AspNetCore.QueryableExtensions.WriteArray to bind to. Incompatible Versions?"));
                }

                methodToCall = method[0].MakeGenericMethod(arguments);
            }
            else
            {
                var method = typeof(QueryableExtensions).GetMethod(nameof(QueryableExtensions.WriteOne));
                if (method is null)
                {
                    throw new CodeGenerationException(typeof(QueryableExtensions), new Exception(
                        "Cannot find correct method for Marten.AspNetCore.QueryableExtensions.WriteOne to bind to. Incompatible Versions?"));
                }

                methodToCall = method.MakeGenericMethod(arguments);
            }


            var call = new MethodCall(typeof(QueryableExtensions), methodToCall)
                            {
                                Arguments =
                                {
                                    [1] = result,
                                    [3] = new StaticVariable(typeof(string), "\"application/json\""),
                                    [4] = new StaticVariable(typeof(int), "200")
                                }
                            };
                            chain.Postprocessors.Add(call);
                            return true;
        }
        return false;
    }
}
#nullable disable