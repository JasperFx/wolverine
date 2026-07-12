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
        Name = usage.SanitizeFormNameForVariable();
    }

    public string Name { get; set; }

    /// <summary>
    /// Re-home this variable to a different creating frame. Used when AsParametersBindingFrame
    /// absorbs the original binding frame and generates it inline: any other consumer of the
    /// variable (e.g. a compound handler LoadAsync/Before method binding the same route value)
    /// must be ordered after the [AsParameters] binding frame instead of re-scheduling the
    /// absorbed frame as a second top-level frame, which emitted the binding code once per
    /// consuming scope and produced uncompilable duplicate locals. See GH-3374.
    /// </summary>
    internal void ReassignCreator(Frame creator)
    {
        Creator = creator;
    }
}