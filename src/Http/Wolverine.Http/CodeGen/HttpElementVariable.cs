using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.CodeGen;

/// <summary>
/// Represents a variable to an element resolved out of an HTTP Request collection
/// </summary>
public class HttpElementVariable : Variable
{
    public HttpElementVariable(Type variableType, string usage, Frame? creator) : base(variableType, usage, creator)
    {
        Name = usage;
    }

    public string Name { get; set; }

}