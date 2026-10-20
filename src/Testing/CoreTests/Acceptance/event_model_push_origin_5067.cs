using Shouldly;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
///     GH-5067: <c>event-model --url</c> stamps the pushed Event Model with the checkout it came from
///     (<c>root</c>, <c>repository</c>, <c>branch</c>), so Stoat can flag a half pushed from a different
///     checkout or repository than the newest one (stoat#92, stoat#126).
/// </summary>
public class event_model_push_origin_5067 : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "gh5067_" + Guid.NewGuid().ToString("N"));

    public event_model_push_origin_5067()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, true);
        }
        catch (IOException)
        {
        }
    }

    private string checkout(string name, string head, string? originUrl)
    {
        var root = Path.Combine(_scratch, name);
        var git = Path.Combine(root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), head + "\n");

        var config = "[core]\n\tbare = false\n";
        if (originUrl != null)
        {
            config += $"[remote \"upstream\"]\n\turl = https://github.com/someone/else.git\n" +
                      $"[remote \"origin\"]\n\turl = {originUrl}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";
        }

        File.WriteAllText(Path.Combine(git, "config"), config);
        return root;
    }

    [Fact]
    public void reads_root_repository_and_branch_from_a_nested_content_root()
    {
        var root = checkout("app", "ref: refs/heads/feature/push-origin", "git@github.com:JasperFx/crittercrush.git");
        var contentRoot = Path.Combine(root, "src", "Api");
        Directory.CreateDirectory(contentRoot);

        var origin = EventModelPushOrigin.Detect(contentRoot);

        origin.Root.ShouldBe(Path.GetFullPath(root));
        origin.Repository.ShouldBe("JasperFx/crittercrush");
        origin.Branch.ShouldBe("feature/push-origin");
    }

    [Fact]
    public void a_detached_head_has_no_branch()
    {
        var root = checkout("detached", "3eb4b6db9a1f2c3d4e5f60718293a4b5c6d7e8f9", "https://github.com/JasperFx/wolverine.git");

        var origin = EventModelPushOrigin.Detect(root);

        origin.Branch.ShouldBeNull();
        origin.Repository.ShouldBe("JasperFx/wolverine");
    }

    [Fact]
    public void a_linked_worktree_reads_its_own_branch_and_the_shared_remotes()
    {
        var main = checkout("main", "ref: refs/heads/main", "https://github.com/JasperFx/wolverine");

        // What `git worktree add` lays down: .git is a file pointing at .git/worktrees/<name>, whose
        // commondir points back at the shared git directory holding config
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/gh-5067\n");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..\n");

        var worktree = Path.Combine(_scratch, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");

        var origin = EventModelPushOrigin.Detect(worktree);

        origin.Root.ShouldBe(Path.GetFullPath(worktree));
        origin.Branch.ShouldBe("gh-5067");
        origin.Repository.ShouldBe("JasperFx/wolverine");
    }

    [Fact]
    public void outside_a_checkout_the_root_is_the_directory_itself()
    {
        var plain = Path.Combine(_scratch, "plain");
        Directory.CreateDirectory(plain);

        // The temp directory is not a checkout on any sane machine; skip rather than lie if it is
        Assert.SkipWhen(hasGitAbove(plain), "the temp directory sits inside a git checkout");

        EventModelPushOrigin.Detect(plain).ShouldBe(new EventModelPushOrigin(Path.GetFullPath(plain), null, null));
    }

    [Theory]
    [InlineData("https://github.com/JasperFx/wolverine.git", "JasperFx/wolverine")]
    [InlineData("https://github.com/JasperFx/wolverine", "JasperFx/wolverine")]
    [InlineData("https://github.com/JasperFx/wolverine/", "JasperFx/wolverine")]
    [InlineData("git@github.com:JasperFx/wolverine.git", "JasperFx/wolverine")]
    [InlineData("ssh://git@github.com/JasperFx/wolverine.git", "JasperFx/wolverine")]
    [InlineData("https://dev.azure.com/org/project/_git/repo", "project/repo")]
    [InlineData("/Users/someone/code/wolverine", null)]
    public void repository_from_remote_url(string url, string? expected)
    {
        EventModelPushOrigin.RepositoryFromRemoteUrl(url).ShouldBe(expected);
    }

    [Fact]
    public void appends_the_origin_as_query_parameters()
    {
        var origin = new EventModelPushOrigin("/code/my app", "JasperFx/wolverine", "feature/x");

        var uri = origin.AppendTo(new Uri("http://localhost:5525/api/event-model"));

        uri.Query.ShouldBe("?root=%2Fcode%2Fmy%20app&repository=JasperFx%2Fwolverine&branch=feature%2Fx");
        uri.AbsolutePath.ShouldBe("/api/event-model");
    }

    [Fact]
    public void keeps_the_callers_query_and_never_overrides_a_parameter_they_set()
    {
        var origin = new EventModelPushOrigin("/code/app", null, "main");

        var uri = origin.AppendTo(new Uri("http://localhost:5525/api/event-model/specs?branch=release&x=1"));

        uri.Query.ShouldBe("?branch=release&x=1&root=%2Fcode%2Fapp");
    }

    private static bool hasGitAbove(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
        {
            if (Path.Exists(Path.Combine(current.FullName, ".git"))) return true;
        }

        return false;
    }
}
