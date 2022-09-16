using System;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Xunit;

namespace CoreTests.ErrorHandling;

public class ExceptionMatchTypeTests
{
    [Fact]
    public void exclude_match()
    {
        var match = new ExcludeType<BadImageFormatException>();
        ShouldBeBooleanExtensions.ShouldBeTrue(match.Matches(new Exception()));
        ShouldBeBooleanExtensions.ShouldBeFalse(match.Matches(new BadImageFormatException()));
    }

    [Fact]
    public void type_match()
    {
        var match = new TypeMatch<BadImageFormatException>();
        ShouldBeBooleanExtensions.ShouldBeTrue(match.Matches(new BadImageFormatException()));
        ShouldBeBooleanExtensions.ShouldBeFalse(match.Matches(new DivideByZeroException()));
    }

    [Fact]
    public void and_match()
    {
        var match = new TypeMatch<BadImageFormatException>()
            .And(new MessageContains("bad"));

        match.Matches(new InvalidOperationException("bad"))
            .ShouldBeFalse();

        match.Matches(new BadImageFormatException("good"))
            .ShouldBeFalse();

        match.Matches(new BadImageFormatException("bad"))
            .ShouldBeTrue();
    }

    [Fact]
    public void or_match()
    {
        var match = new TypeMatch<BadImageFormatException>()
            .Or(new MessageContains("bad"));

        match.Matches(new InvalidOperationException("bad"))
            .ShouldBeTrue();

        match.Matches(new BadImageFormatException("good"))
            .ShouldBeTrue();

        match.Matches(new BadImageFormatException("bad"))
            .ShouldBeTrue();

        match.Matches(new Exception())
            .ShouldBeFalse();
    }

    [Fact]
    public void inner_match()
    {
        var match = new InnerMatch(new TypeMatch<BadImageFormatException>());

        ShouldBeBooleanExtensions.ShouldBeFalse(match.Matches(new BadImageFormatException()));
        ShouldBeBooleanExtensions.ShouldBeFalse(match.Matches(new Exception("bad", new DivideByZeroException())));

        ShouldBeBooleanExtensions.ShouldBeTrue(match.Matches(new Exception("bad", new BadImageFormatException())));
    }
}
