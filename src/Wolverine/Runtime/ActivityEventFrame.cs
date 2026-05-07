using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime;

/// <summary>
/// Codegen frame that emits an <see cref="Activity.AddEvent"/> call against <see cref="Activity.Current"/>.
/// </summary>
internal class ActivityEventFrame : Frame
{
    private readonly string _eventName;

    public ActivityEventFrame(string eventName) : base(false)
    {
        _eventName = eventName;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.AddEvent(new {typeof(ActivityEvent).FullNameInCode()}(\"{_eventName}\"));");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain) => [];
}
