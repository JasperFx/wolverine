using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5701. Does a saga method complete the saga? A message such a method returns is what the saga sends when it is
/// finished, such as a trip's <c>TripBooked</c>, not a step that can fail and be compensated, so a catch-all
/// <c>Compensate(object, ...)</c> leaves it out. Read from the method body: a direct call to
/// <see cref="Saga.MarkCompleted" />, in the method itself or in its async state machine.
/// </summary>
/// <remarks>
/// A method that completes the saga through a helper of its own isn't seen, and its messages stay covered, as they were
/// before. Name a message with <c>[Compensates]</c> to cover it either way.
/// </remarks>
internal static class SagaCompletion
{
    private static readonly MethodInfo MarkCompleted =
        typeof(Saga).GetMethod("MarkCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(x => x.GetValue(null))
        .OfType<OpCode>()
        .ToDictionary(x => x.Value);

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reads saga methods' own bodies. With no body to read the method counts as not completing the saga, as before GH-5701.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The async state machine of a saga method, rooted with the method by handler discovery.")]
    public static bool CompletesTheSaga(MethodInfo method)
    {
        var body = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                       .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                   ?? (MethodBase)method;

        try
        {
            return callsMarkCompleted(body);
        }
        catch (Exception)
        {
            // An unreadable body (NativeAOT has none) counts as not completing the saga
            return false;
        }
    }

    [RequiresUnreferencedCode("Reads the method's IL")]
    private static bool callsMarkCompleted(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var position = 0;
        while (position < il.Length)
        {
            var value = (short)il[position++];
            if (value == 0xFE && position < il.Length) value = (short)(0xFE00 | il[position++]);
            if (!OpCodesByValue.TryGetValue(value, out var code)) return false;

            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    var targets = BitConverter.ToInt32(il, position);
                    position += 4 + 4 * targets;
                    break;
                case OperandType.InlineMethod:
                    var token = BitConverter.ToInt32(il, position);
                    position += 4;
                    if ((code == OpCodes.Call || code == OpCodes.Callvirt) &&
                        method.Module.ResolveMethod(token, typeArguments, methodArguments) is { } called &&
                        called.Name == MarkCompleted.Name && called.DeclaringType == typeof(Saga))
                    {
                        return true;
                    }

                    break;
                default:
                    position += 4;
                    break;
            }
        }

        return false;
    }
}
