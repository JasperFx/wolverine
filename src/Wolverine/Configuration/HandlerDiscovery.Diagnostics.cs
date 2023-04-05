using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Configuration;

public sealed partial class HandlerDiscovery
{
    internal string DescribeHandlerMatch(WolverineOptions options, Type candidateType)
    {
        var writer = new StringWriter();

        if (candidateType.IsOpenGeneric())
        {
            writer.WriteLine("MISS -- Wolverine cannot use open generic types as handlers");
            return writer.ToString();
        }

        if (!candidateType.IsStatic() && (candidateType.IsInterface || candidateType.IsAbstract))
        {
            writer.WriteLine("MISS -- Handler types can only be concrete types");
            return writer.ToString();
        }
        
        if (!Assemblies.Contains(candidateType.Assembly))
        {
            writeAssemblyIsNotRegistered(options, candidateType, writer);
            return writer.ToString();
        }

        bool typeNotFound = false;
        if (!HandlerQuery.Includes.Matches(candidateType))
        {
            writeTypeIncludeMiss(candidateType, writer);
            typeNotFound = true;
        }

        if (HandlerQuery.Excludes.Matches(candidateType))
        {
            writeTypeExcludeMatch(candidateType, writer);
            typeNotFound = true;
        }

        if (typeNotFound)
        {
            return writer.ToString();
        }
        
        writer.WriteLine($"Successfully found {candidateType.FullNameInCode()} during scanning.");
        writer.WriteLine("Methods:");
        writer.WriteLine();

        var methods = candidateType.GetMethods()
            .Where(x => x.DeclaringType != typeof(object));

        foreach (var method in methods)
        {
            writer.WriteLine($"Method: {method.Name}({method.GetParameters().Select(x => x.ParameterType.ShortNameInCode()).Join(", ")})" );
            
            foreach (var filter in MethodIncludes)
            {
                if (filter.Matches(method))
                {
                    writer.WriteLine("HIT -- " + filter.Description);
                }
                else
                {
                    writer.WriteLine("MISS -- " + filter.Description);
                }
            }

            foreach (var filter in MethodExcludes)
            {
                if (filter.Matches(method))
                {
                    writer.WriteLine("EXCLUDED -- " + filter.Description);
                }
                else
                {
                    writer.WriteLine("OK -- " + filter.Description);
                }
            }
            
            writer.WriteLine();
            
        }

        return writer.ToString();
    }

    private void writeTypeExcludeMatch(Type candidateType, StringWriter writer)
    {
        foreach (var filter in HandlerQuery.Excludes)
        {
            if (filter.Matches(candidateType))
            {
                writer.WriteLine("EXCLUDED -- " + filter.Description);
            }
            else
            {
                writer.WriteLine("OK -- " + filter.Description);
            }
        }
    }

    private void writeTypeIncludeMiss(Type candidateType, StringWriter writer)
    {
        foreach (var filter in HandlerQuery.Includes)
        {
            if (filter.Matches(candidateType))
            {
                writer.WriteLine("HIT -- " + filter.Description);
            }
            else
            {
                writer.WriteLine("MISS -- " + filter.Description);
            }
        }
    }

    private void writeAssemblyIsNotRegistered(WolverineOptions options, Type candidateType, StringWriter writer)
        {
            writer.WriteLine($"Handler type is in assembly {candidateType.Assembly.GetName().Name} that is not currently being scanned by Wolverine");
            writer.WriteLine("To fix this, add the assembly to your application by either:");
            writer.WriteLine("1. Either add the [assembly: WolverineModule] attribute to this assembly");
            writer.WriteLine(
                $"2. Or add WolverineOptions.Discovery.IncludeAssembly({candidateType.FullNameInCode()}.Assembly); within your UseWolverine() setup");
            writer.WriteLine();

            if (options.ApplicationAssembly != null)
            {
                writer.WriteLine($"The application assembly is {options.ApplicationAssembly}");
            }
            else
            {
                writer.WriteLine("There is no application assembly");
            }

            foreach (var assembly in Assemblies)
            {
                if (assembly == options.ApplicationAssembly) continue;
                writer.WriteLine("Scanning Assembly " + assembly.GetName().Name);
            }
        }
}