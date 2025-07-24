using Wolverine.Persistence;
using WolverineWebApi.Todos;

namespace Wolverine.Http.Tests.Persistence;

public static class RequiredTodoEndpoint
{
    // Should 404 on missing
    [WolverineGet("/required/todo404/{id}")]
    public static Todo2 Get1([Entity] Todo2 todo) => todo;
    
    // Should 400 w/ ProblemDetails on missing
    [WolverineGet("/required/todo400/{id}")]
    public static Todo2 Get2([Entity(OnMissing = OnMissing.ProblemDetailsWith400)] Todo2 todo) 
        => todo;
    
    // Should 404 w/ ProblemDetails on missing
    [WolverineGet("/required/todo3/{id}")]
    public static Todo2 Get3([Entity(OnMissing = OnMissing.ProblemDetailsWith404)] Todo2 todo) 
        => todo;
    
    // Should throw an exception on missing
    [WolverineGet("/required/todo4/{id}")]
    public static Todo2 Get4([Entity(OnMissing = OnMissing.ThrowException)] Todo2 todo) 
        => todo;
    
    // Should 400 w/ ProblemDetails on missing & custom message
    [WolverineGet("/required/todo5/{id}")]
    public static Todo2 Get5([Entity(OnMissing = OnMissing.ProblemDetailsWith400, MissingMessage = "Wrong id man!")] Todo2 todo) 
        => todo;
    
    // Should 400 w/ ProblemDetails on missing & custom message
    [WolverineGet("/required/todo6/{id}")]
    public static Todo2 Get6([Entity(OnMissing = OnMissing.ProblemDetailsWith400, MissingMessage = "Id '{0}' is wrong!")] Todo2 todo) 
        => todo;
    
    // Should error & custom message
    [WolverineGet("/required/todo7/{id}")]
    public static Todo2 Get7([Entity(OnMissing = OnMissing.ThrowException, MissingMessage = "Id '{0}' is wrong!")] Todo2 todo) 
        => todo;

}