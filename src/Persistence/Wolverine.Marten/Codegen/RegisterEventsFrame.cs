using System;
using System.Collections.Generic;
using System.Reflection;
using Baseline;
using Baseline.Reflection;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten.Events;

namespace Wolverine.Marten.Codegen;

internal class RegisterEventsFrame<T> : MethodCall
{
    internal static MethodInfo FindMethod(Type responseType)
    {
        return responseType.CanBeCastTo<IEnumerable<object>>()
            ? ReflectionHelper.GetMethod<IEventStream<T>>(x => x.AppendMany(new List<object>()))
            : ReflectionHelper.GetMethod<IEventStream<T>>(x => x.AppendOne(null));
    }

    public RegisterEventsFrame(Variable returnVariable) : base(typeof(IEventStream<T>), FindMethod(returnVariable.VariableType))
    {
        Arguments[0] = returnVariable;
        CommentText = "Capturing any possible events returned from the command handlers";
    }
}
