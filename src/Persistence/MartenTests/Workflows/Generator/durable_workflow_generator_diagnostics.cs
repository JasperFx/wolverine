using Shouldly;
using static MartenTests.Workflows.Generator.GeneratorHarness;

namespace MartenTests.Workflows.Generator;

// GH-5660 SPIKE: what the first cut of the generator refuses, each a compile-time error, and the splits it does make
// producing code that compiles
public class durable_workflow_generator_diagnostics
{
    [Fact]
    public void refuses_a_method_that_is_not_static()
    {
        Run(Workflow("        await ctx.SendAsync(new Note(\"hi\"));", modifiers: "public"))
            .ShouldBeRefusedWith("WOLV1001");
    }

    [Fact]
    public void refuses_an_await_on_anything_but_the_context()
    {
        Run(Workflow("""
                    await Task.Delay(10);
                    var answer = await ctx.InvokeAsync<Answer>(new Ask(go.Id));
            """)).ShouldBeRefusedWith("WOLV1002");
    }

    [Theory]
    [InlineData("for (var i = 0; i < go.Count; i++) {", "}")]
    [InlineData("foreach (var i in new[] { 1, 2 }) {", "}")]
    [InlineData("while (go.Count > 0) {", "}")]
    [InlineData("do {", "} while (go.Count > 0);")]
    public void refuses_a_loop_around_a_wait(string open, string close)
    {
        Run(Workflow($$"""
                    {{open}}
                        var answer = await ctx.InvokeAsync<Answer>(new Ask(go.Id));
                        Use(answer);
                    {{close}}
            """)).ShouldBeRefusedWith("WOLV1003");
    }

    [Theory]
    [InlineData("try { var a = await ctx.InvokeAsync<Answer>(new Ask(go.Id)); Use(a); } catch (Exception) { }")]
    [InlineData("try { Use(1); } catch (Exception) { var a = await ctx.InvokeAsync<Answer>(new Ask(go.Id)); Use(a); }")]
    [InlineData("try { Use(1); } finally { await ctx.DelayAsync(TimeSpan.FromMinutes(1)); }")]
    [InlineData("using (var source = new CancellationTokenSource()) { await ctx.DelayAsync(TimeSpan.FromMinutes(1)); }")]
    [InlineData("using var source = new CancellationTokenSource(); await ctx.DelayAsync(TimeSpan.FromMinutes(1));")]
    public void refuses_try_catch_finally_or_using_around_a_wait(string body)
    {
        Run(Workflow("        " + body)).ShouldBeRefusedWith("WOLV1004");
    }

    [Theory]
    [InlineData("Use(await ctx.InvokeAsync<Answer>(new Ask(go.Id)));")]
    [InlineData("var both = await Task.WhenAll(ctx.InvokeAsync<Answer>(new Ask(go.Id)), ctx.InvokeAsync<Answer>(new Ask(go.Id))); Use(both);")]
    [InlineData("await ctx.RunAsync(async () => { await ctx.DelayAsync(TimeSpan.FromSeconds(1)); });")]
    public void refuses_a_wait_that_is_not_a_statement_of_the_method(string body)
    {
        var run = Run(Workflow("        " + body)).ShouldCompileBeforeGeneration();

        run.Ids.ShouldContain("WOLV1005");
        run.Generated.ShouldBeEmpty();
    }

    [Fact]
    public void refuses_a_switch_around_a_wait()
    {
        Run(Workflow("""
                    switch (go.Count)
                    {
                        case 1:
                            await ctx.DelayAsync(TimeSpan.FromMinutes(1));
                            break;
                    }
            """)).ShouldBeRefusedWith("WOLV1006");
    }

    [Fact]
    public void refuses_a_pattern_variable_that_lives_across_a_wait()
    {
        Run(Workflow("""
                    if (go.Count is var count && count > 1)
                    {
                        await ctx.DelayAsync(TimeSpan.FromMinutes(1));
                        Use(count);
                    }
            """)).ShouldBeRefusedWith("WOLV1007");
    }

    [Theory]
    [InlineData("var source = new CancellationTokenSource();", "source")]
    [InlineData("Func<int> next = () => 1;", "next")]
    [InlineData("var token = CancellationToken.None;", "token")]
    [InlineData("object boxed = 1;", "boxed")]
    public void refuses_a_local_living_across_a_wait_that_cannot_be_stored(string declaration, string name)
    {
        Run(Workflow($"""
                    {declaration}
                    await ctx.DelayAsync(TimeSpan.FromMinutes(1));
                    Use({name});
            """)).ShouldBeRefusedWith("WOLV1009");
    }

    [Fact]
    public void refuses_a_private_member_of_the_workflow_class()
    {
        Run(Workflow("""
                    await ctx.SendAsync(new Note(secret().ToString()));
            """)).ShouldBeRefusedWith("WOLV1010");
    }

    [Fact]
    public void warns_when_a_waited_for_message_does_not_name_its_workflow()
    {
        var run = Run(Workflow("""
                    var note = await ctx.WaitForAsync<Note>(TimeSpan.FromHours(1));
                    Use(note);
            """));

        run.Ids.ShouldBe(["WOLV1011"]);
        run.ShouldGenerateASaga();
    }

    [Fact]
    public void loops_and_try_blocks_without_a_wait_inside_are_copied_as_they_are()
    {
        var saga = Run(Workflow("""
                    var total = 0;
                    for (var i = 0; i < go.Count; i++)
                    {
                        total += i;
                        await ctx.SendAsync(new Note($"item {i}"));
                    }

                    try
                    {
                        Use(total);
                    }
                    finally
                    {
                        Use(go);
                    }

                    var answer = await ctx.InvokeAsync<Answer>(new Ask(go.Id));
                    Use($"{answer.Text} after {total}");
            """)).ShouldGenerateASaga();

        saga.ShouldContain("public int Total { get; set; }");
        saga.ShouldContain("for (var i = 0; i < Go.Count; i++)");
        saga.ShouldContain("Use($\"{answer.Text} after {Total}\");");
    }

    [Fact]
    public void every_kind_of_split_compiles()
    {
        // A wait in each branch of an if/else if/else, an assignment from a wait, a wait whose result is dropped, a
        // return from inside a branch, and a const of the workflow class used without its class name
        var saga = Run(Workflow("""
                    string outcome;
                    if (go.Count > Limit)
                    {
                        var approved = await ctx.WaitForAsync<Approved>(TimeSpan.FromHours(1));
                        if (approved is null) return;
                        outcome = approved.By;
                    }
                    else if (go.Count > 1)
                    {
                        var answer = await ctx.InvokeAsync<Answer>(new Ask(go.Id));
                        outcome = answer.Text;
                    }
                    else
                    {
                        outcome = "small";
                    }

                    Answer? last = null;
                    last = await ctx.InvokeAsync<Answer>(new Ask(go.Id));
                    await ctx.DelayAsync(TimeSpan.FromMinutes(5));
                    await ctx.SendAsync(new Note($"{outcome} {last.Text}"));
            """)).ShouldGenerateASaga();

        saga.ShouldContain("public string Outcome { get; set; }");
        saga.ShouldContain("public Answer? Last { get; set; }");
        saga.ShouldContain("Flow.Limit");
        saga.ShouldContain("private async Task resumeAtStep3(Answer result, WorkflowContext ctx)");
        saga.ShouldContain("Last = result;");
    }
}
