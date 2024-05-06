using System.Data.Common;
using System.Diagnostics;
using CoreTests.Runtime.Handlers;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using TestingSupport;
using Wolverine.Middleware;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;



public class configuring_return_value_actions
{
    private readonly HandlerChain
        theChain = HandlerChain.For<finding_service_dependencies_of_a_chain.FakeDudeWithAction>(x => x.Handle(null, null, null), null);


    public class ReturnVariableActionTests
    {
        private readonly CommentFrame comment1;
        private readonly CommentFrame comment2;
        private readonly ReturnVariableAction theAction;
        

        public ReturnVariableActionTests()
        {
            comment1 = new CommentFrame("do nothing here");
            comment2 = new CommentFrame("do nothing here");

            theAction = new ReturnVariableAction();
            theAction.Frames.Add(comment1);
            theAction.Frames.Add(comment2);
            
            theAction.Dependencies.Add(typeof(IServiceProvider));
            theAction.Dependencies.Add(typeof(DbConnection));
            
        }

        [Fact]
        public void builds_the_frames()
        {
            theAction.As<IReturnVariableAction>().Frames()
                .ShouldHaveTheSameElementsAs<Frame>(comment1, comment2);
        }

        [Fact]
        public void returns_the_dependencies()
        {
            theAction.As<IReturnVariableAction>()
                .Dependencies().ShouldHaveTheSameElementsAs(typeof(IServiceProvider), typeof(DbConnection));
        }
    }
    
    [Fact]
    public void use_variable_action()
    {
        var comment = new CommentFrame("do nothing here");
        var variable = Variable.For<string>();

        var action = variable.UseReturnAction(v => comment);
        
        action.Description.ShouldBe("Override");
        
        action.Frames.Single().ShouldBe(comment);
    }

    [Fact]
    public void find_variable_action_hit()
    {
        var comment = new CommentFrame("do nothing here");
        var variable = Variable.For<string>();

        var action = variable.UseReturnAction(v => comment);
        
        variable.ReturnAction(theChain).ShouldBeSameAs(action);
    }

    [Fact]
    public void find_variable_action_miss_returns_cascading_message()
    {
        var variable = Variable.For<string>();
        variable.ReturnAction(theChain).ShouldBeOfType<CascadeMessage>()
            .Variable.ShouldBeSameAs(variable);
    }

    [Fact]
    public void do_nothing_with_return_value_variable()
    {
        var variable = Variable.For<string>();
        variable.DoNothingWithReturnValue();

        var action = variable.ReturnAction(theChain);

        action.Frames().Single().ShouldBeOfType<CommentFrame>();
        action.Description.ShouldBe("Do nothing");
        action.Dependencies().ShouldBeEmpty();
    }

    public record Foo;

    public class FooHandler
    {
        public void Handle(Foo foo) => Debug.WriteLine("Got a foo");
    }

    public class when_calling_method_on_return_variable
    {
        private readonly Variable theVariable = Variable.For<WriteFile>();
        private readonly IReturnVariableAction theVariableAction;

        public when_calling_method_on_return_variable()
        {
            theVariable.CallMethodOnReturnVariable<WriteFile>(x => x.Execute(null), "some description" );

            var chain = HandlerChain.For<FooHandler>(x => x.Handle(null), new HandlerGraph());
            theVariableAction = theVariable.ReturnAction(chain);
        }

        [Fact]
        public void variable_action_type()
        {
            theVariableAction.ShouldBeOfType<CallMethodReturnVariableAction<WriteFile>>();
        }

        [Fact]
        public void targets_the_return_variable()
        {
            theVariableAction.ShouldBeOfType<CallMethodReturnVariableAction<WriteFile>>()
                .MethodCall.Target.ShouldBe(theVariable);
        }

        [Fact]
        public void return_dependencies_from_method()
        {
            theVariableAction.Dependencies().ShouldContain(typeof(IFileService));
        }

        [Fact]
        public void should_serve_up_method_call_for_frame()
        {
            var call = theVariableAction.Frames().Single()
                .ShouldBeOfType<MethodCall>();
            
            call.Method.Name.ShouldBe(nameof(WriteFile.Execute));
            call.HandlerType.ShouldBe(typeof(WriteFile));
        }

        [Fact]
        public void pass_the_description_to_action()
        {
            theVariableAction.Description.ShouldBe("some description");
        }

        [Fact]
        public void pass_a_non_null_description_to_comment_text_of_method()
        {
            var call = theVariableAction.Frames().Single()
                .ShouldBeOfType<MethodCall>();
            
            call.CommentText.ShouldBe("some description");
        }
    }
    
    public class when_calling_method_on_return_variable_if_not_null
    {
        private readonly Variable theVariable = Variable.For<WriteFile>();
        private readonly IReturnVariableAction theVariableAction;
        private readonly MethodCall theMethodCall;

        public when_calling_method_on_return_variable_if_not_null()
        {
            theVariable.CallMethodOnReturnVariableIfNotNull<WriteFile>(x => x.Execute(null), "some description" );

            var chain = HandlerChain.For<FooHandler>(x => x.Handle(null), new HandlerGraph());
            theVariableAction = theVariable.ReturnAction(chain);
            
            var wrapper = theVariableAction.ShouldBeOfType<CallMethodReturnVariableAction<WriteFile>>().Frames().Single().ShouldBeOfType<IfElseNullGuardFrame.IfNullGuardFrame>();
            theMethodCall = wrapper.Inners.Single()
                
                .ShouldBeOfType<MethodCall>();
        }

        [Fact]
        public void targets_the_return_variable()
        {
            theMethodCall.Target.ShouldBe(theVariable);
        }

        [Fact]
        public void return_dependencies_from_method()
        {
            theVariableAction.Dependencies().ShouldContain(typeof(IFileService));
        }

        [Fact]
        public void should_serve_up_method_call_for_frame()
        {
            var call = theMethodCall;
            
            call.Method.Name.ShouldBe(nameof(WriteFile.Execute));
            call.HandlerType.ShouldBe(typeof(WriteFile));
        }

        [Fact]
        public void pass_the_description_to_action()
        {
            theVariableAction.Description.ShouldBe("some description");
        }

        [Fact]
        public void pass_a_non_null_description_to_comment_text_of_method()
        {
            theMethodCall.CommentText.ShouldBe("some description");
        }
    }
}


public class WriteFile
{
    public Task Execute(IFileService service)
    {
        return Task.CompletedTask;
    }
}

public interface IFileService;