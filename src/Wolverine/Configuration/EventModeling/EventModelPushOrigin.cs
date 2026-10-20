using System.Text.RegularExpressions;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-5067: where a pushed Event Model half came from, sent to the monitor as the optional
///     <c>root</c>, <c>repository</c> and <c>branch</c> query parameters on
///     <c>PUT /api/event-model</c> (JasperFx/stoat#92, stoat#126). Stoat uses them to flag a half pushed from
///     a different checkout or repository than the newest one. A monitor that predates them ignores them.
/// </summary>
/// <param name="Root">
///     The absolute path of the git checkout root, or the starting directory when it is not inside a checkout
/// </param>
/// <param name="Repository">The canonical <c>owner/name</c> of the checkout's <c>origin</c> remote, if known</param>
/// <param name="Branch">The checkout's current branch, or null on a detached HEAD or outside a checkout</param>
public sealed record EventModelPushOrigin(string Root, string? Repository, string? Branch)
{
    /// <summary>
    ///     Read the checkout around <paramref name="startDirectory" /> (the host's content root). The git files
    ///     are read directly rather than through a <c>git</c> process, so this works where git is not installed,
    ///     and never fails: anything it cannot read is left out.
    /// </summary>
    public static EventModelPushOrigin Detect(string startDirectory)
    {
        var start = Path.GetFullPath(startDirectory);

        try
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var dotGit = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(dotGit))
                {
                    return fromGitDirectory(directory.FullName, dotGit, dotGit);
                }

                if (File.Exists(dotGit))
                {
                    // A linked worktree or submodule: ".git" is a file naming the real git directory, and a
                    // worktree's config and remotes live in the shared "commondir"
                    var gitDir = readGitDirPointer(directory.FullName, dotGit);
                    if (gitDir == null) return new EventModelPushOrigin(directory.FullName, null, null);

                    return fromGitDirectory(directory.FullName, gitDir, readCommonDir(gitDir) ?? gitDir);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Provenance is best effort; the push must never fail for want of it
        }

        return new EventModelPushOrigin(start, null, null);
    }

    /// <summary>
    ///     The monitor URL with <c>root</c>, <c>repository</c> and <c>branch</c> appended. A parameter the URL
    ///     already carries is left as the caller wrote it.
    /// </summary>
    public Uri AppendTo(Uri monitor)
    {
        var query = monitor.Query.TrimStart('?');
        var existing = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Uri.UnescapeDataString(x.Split('=')[0]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        if (query.Length > 0) parts.Add(query);

        void add(string name, string? value)
        {
            if (string.IsNullOrEmpty(value) || existing.Contains(name)) return;
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }

        add("root", Root);
        add("repository", Repository);
        add("branch", Branch);

        return new UriBuilder(monitor) { Query = string.Join("&", parts) }.Uri;
    }

    private static EventModelPushOrigin fromGitDirectory(string root, string gitDir, string commonDir)
    {
        return new EventModelPushOrigin(root, readOriginRepository(commonDir), readBranch(gitDir));
    }

    private static string? readGitDirPointer(string root, string dotGitFile)
    {
        var line = File.ReadLines(dotGitFile).FirstOrDefault(x => x.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line == null) return null;

        var path = line["gitdir:".Length..].Trim();
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    private static string? readCommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        if (!File.Exists(file)) return null;

        var path = File.ReadAllText(file).Trim();
        return path.Length == 0 ? null : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(gitDir, path));
    }

    private static string? readBranch(string gitDir)
    {
        var head = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(head)) return null;

        const string prefix = "ref: refs/heads/";
        var content = File.ReadAllText(head).Trim();

        // Anything else is a detached HEAD: a commit id, not a branch
        return content.StartsWith(prefix, StringComparison.Ordinal) ? content[prefix.Length..] : null;
    }

    private static string? readOriginRepository(string commonDir)
    {
        var config = Path.Combine(commonDir, "config");
        if (!File.Exists(config)) return null;

        var inOrigin = false;
        foreach (var raw in File.ReadLines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = Regex.IsMatch(line, "^\\[remote\\s+\"origin\"\\]$");
                continue;
            }

            if (!inOrigin) continue;

            var match = Regex.Match(line, "^url\\s*=\\s*(.+)$");
            if (match.Success)
            {
                return RepositoryFromRemoteUrl(match.Groups[1].Value.Trim());
            }
        }

        return null;
    }

    /// <summary>
    ///     <c>owner/name</c> from a remote URL in any of the usual shapes: <c>https://github.com/owner/name.git</c>,
    ///     <c>git@github.com:owner/name.git</c>, <c>ssh://git@host/owner/name</c>, or Azure DevOps'
    ///     <c>https://dev.azure.com/org/project/_git/repo</c> as <c>project/repo</c>. Null when it has no owner and
    ///     name, such as a local path.
    /// </summary>
    public static string? RepositoryFromRemoteUrl(string url)
    {
        var match = Regex.Match(url, @"^(?:[a-z][a-z0-9+.-]*://[^/]+/|[^@/\s]+@[^:/\s]+:)(?<path>.+?)(?:\.git)?/*$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        // Azure DevOps puts a "_git" segment between the project and the repository
        var segments = match.Groups["path"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x != "_git")
            .ToArray();
        return segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : null;
    }
}
