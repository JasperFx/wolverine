using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

public class GetServiceFromScopedContainerFrame : SyncFrame
{
    private readonly Variable _scoped;


    public GetServiceFromScopedContainerFrame(Variable scoped, Type serviceType)
    {
        if (scoped.VariableType != typeof(IServiceProvider))
        {
            throw new ArgumentOutOfRangeException(nameof(scoped),
                $"Wrong type for the variable. Expected {typeof(IServiceProvider).FullNameInCode()} but got {scoped.VariableType.FullNameInCode()}");
        }
        
        _scoped = scoped;
        uses.Add(_scoped);

        Variable = new Variable(serviceType, this);
    }

    /// <summary>
    ///     <summary>
    ///         Optional code fragment to write at the beginning of this
    ///         type in code
    ///     </summary>
    public ICodeFragment? Header { get; set; }

    public Variable Variable { get; }


    /// <summary>
    ///     Add a single line comment as the header to this type
    /// </summary>
    /// <param name="text"></param>
    public void Comment(string text)
    {
        Header = new OneLineComment(text);
    }

    /// <summary>
    ///     Add a multi line comment as the header to this type
    /// </summary>
    /// <param name="text"></param>
    public void MultiLineComment(string text)
    {
        Header = new MultiLineComment(text);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Header != null)
        {
            writer.WriteLine("");
            Header.Write(writer);
        }

        writer.Write(
            $"var {Variable.Usage} = {typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{Variable.VariableType.FullNameInCode()}>({_scoped.Usage});");
        Next?.GenerateCode(method, writer);
    }
}