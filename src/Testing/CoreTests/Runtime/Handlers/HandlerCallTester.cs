using System.Reflection;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class HandlerCallTester
{
    [Fact]
    public void could_handle()
    {
        var handler1 = HandlerCall.For<SomeHandler>(x => x.Interface(null));
        var handler2 = HandlerCall.For<SomeHandler>(x => x.BaseClass(null));

        handler1.CouldHandleOtherMessageType(typeof(Input1)).ShouldBeTrue();
        handler2.CouldHandleOtherMessageType(typeof(Input1)).ShouldBeTrue();

        handler1.CouldHandleOtherMessageType(typeof(Input2)).ShouldBeFalse();
        handler1.CouldHandleOtherMessageType(typeof(Input2)).ShouldBeFalse();
    }

    [Fact]
    public void could_handle_is_false_for_its_own_input_type()
    {
        var handler = HandlerCall.For<ITargetHandler>(x => x.OneInOneOut(null));
        handler.CouldHandleOtherMessageType(typeof(Input)).ShouldBeFalse();
    }


    [Fact]
    public void handler_call_should_not_match_property_setters()
    {
        var handlerType = typeof(ITargetHandler);
        var property = handlerType.GetTypeInfo().GetProperty("Message");
        var method = property.GetSetMethod();
        HandlerCall.IsCandidate(method).ShouldBeFalse();
    }


    [Fact]
    public void is_candidate()
    {
        HandlerCall.IsCandidate(ReflectionHelper.GetMethod<ITargetHandler>(x => x.ZeroInZeroOut())).ShouldBeFalse();
        HandlerCall.IsCandidate(ReflectionHelper.GetMethod<ITargetHandler>(x => x.OneInOneOut(null)))
            .ShouldBeTrue();


        HandlerCall.IsCandidate(ReflectionHelper.GetMethod<ITargetHandler>(x => x.OneInZeroOut(null)))
            .ShouldBeTrue();


        HandlerCall.IsCandidate(ReflectionHelper.GetMethod<ITargetHandler>(x => x.ManyIn(null, null)))
            .ShouldBeTrue();

        HandlerCall.IsCandidate(ReflectionHelper.GetMethod<ITargetHandler>(x => x.ReturnsValueType(null)))
            .ShouldBeFalse();
    }

    [Fact]
    public void throws_chunks_if_you_try_to_use_a_method_with_no_inputs()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => { HandlerCall.For<ITargetHandler>(x => x.ZeroInZeroOut()); });
    }

    public interface ITargetHandler
    {
        string Message { get; set; }
        Output OneInOneOut(Input input);
        void OneInZeroOut(Input input);
        object OneInManyOut(Input input);
        void ZeroInZeroOut();

        void ManyIn(Input i2, ISomeService i1);

        bool ReturnsValueType(Input input);
    }

    public interface ISomeService
    {
    }

    public class Input
    {
    }

    public class DifferentInput
    {
    }

    public class SpecialInput : Input
    {
    }

    public class Output
    {
    }

    public interface IInput
    {
    }

    public abstract class InputBase
    {
    }

    public class Input1 : InputBase, IInput
    {
    }

    public class Input2
    {
    }

    public class SomeHandler
    {
        public void Interface(IInput input)
        {
        }

        public void BaseClass(InputBase input)
        {
        }
    }
}