using System.Globalization;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// `alta session delete`: an agent deletes archived sessions with every session below them, as Delete with all child
// sessions… of the window does, and through the same path (ISessionDeletion). The command itself is the gate: it asks the
// user on a card of the session of the agent that no permission mode answers, and deletes only what the card showed. A
// sandboxed session does not get it (AltaSandboxGate).
internal sealed partial class BuiltInAltaCommandContributor
{
    // The sessions the card names one by one; the others are counted.
    internal const int MaximumDeletionCardRows = 20;

    // What the card shows of a title or a project name, at most.
    private const int MaximumDeletionCardText = 60;

    // What the card holds at most: the review shows no more of a request (SessionPermissionService.OwnedCommandLimit).
    private const int MaximumDeletionCardDetail = 4096;

    private static Command CreateSessionDeleteCommand(AltaCommandContext context)
    {
        var sessionIds = new List<string>();
        var dryRun = false;
        // An agent that loops over the sessions asks the user once per session: the descriptions a script reads say it.
        var command = Leaf("delete", "Delete archived sessions and every session below them, after the user agrees on a card of this session: name them all in one call, since each call shows a card of its own.");
        command.Add("<session-id>*", "CodeAlta session ids, all in one call (an array in a script): each call asks the user again.", value => { if (value is not null) sessionIds.Add(value); });
        command.Add("dry-run", "List what would be deleted, the sessions below included, without asking or deleting.", value => dryRun = value is not null);
        command.Add(async (_, _) => await HandleSessionDeleteAsync(context, sessionIds, dryRun).ConfigureAwait(false));
        AddHelpText(command,
            "Delete sessions only when the user asks for it. To put sessions aside, use `alta session archive`.",
            "Each session and every session below it must be archived and idle. One card lists them all; nothing is",
            "deleted until the user answers Delete there, and no permission mode answers it. A session that changed",
            "since the card (a new child, a new title, a run) is not deleted. An agent deletes only sessions of its",
            "own space and the sessions it created.",
            "Examples:",
            "  `alta session delete <session-id> --dry-run`",
            "  `alta session delete <session-id> <session-id>`");
        return command;
    }

    private static async ValueTask<int> HandleSessionDeleteAsync(AltaCommandContext context, IReadOnlyList<string> sessionIds, bool dryRun)
    {
        const string CommandPath = "alta session delete";
        var ids = sessionIds.Select(static id => id.Trim()).Where(static id => id.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) return UsageError(context, "usage.missingSession", "At least one session id is required.", CommandPath);
        foreach (var id in ids) context.ClaimSessionWork(id);
        if (!context.TryGetRequired<ISessionDeletion>(nameof(ISessionDeletion), out var deletion)) return AltaExitCodes.ServiceUnavailable;

        // What would be deleted: each tree once, and a session below another one named is deleted with it.
        var trees = new List<SessionTreeForDeletion>();
        foreach (var id in ids)
        {
            var info = await ResolveSessionInfoAsync(context, id).ConfigureAwait(false);
            if (info.ExitCode != AltaExitCodes.Success) return info.ExitCode;
            if (await deletion.ReadSessionTreeAsync(info.Info!.Session.SessionId, context.CancellationToken).ConfigureAwait(false) is not { } tree)
                return NotFound(context, "session.notFound", $"Session '{id}' is not in the sessions of this computer: a session of another machine is deleted there.");
            trees.Add(tree);
        }

        trees = [.. trees.DistinctBy(static tree => tree.SessionId, StringComparer.OrdinalIgnoreCase)];
        trees.RemoveAll(tree => trees.Any(other => !ReferenceEquals(other, tree)
            && other.Members.Skip(1).Any(member => string.Equals(member.SessionId, tree.SessionId, StringComparison.OrdinalIgnoreCase))));

        foreach (var tree in trees)
        {
            foreach (var member in tree.Members)
            {
                if (IsCaller(context, member.SessionId))
                {
                    return DeleteRefused(context, "session.callerSession", member.SessionId == tree.SessionId
                        ? $"Session '{member.SessionId}' is this session: a session does not delete itself."
                        : $"Session '{member.SessionId}', below '{tree.SessionId}', is this session: a session does not delete itself, nor the sessions above it.");
                }

                if (await RefuseSessionOfOtherSpaceAsync(context, member.SessionId, "deletes").ConfigureAwait(false) is { } otherSpace) return otherSpace;
                if (!member.Archived)
                {
                    var which = member.SessionId == tree.SessionId ? $"Session '{member.SessionId}'" : $"Session '{member.SessionId}', below '{tree.SessionId}',";
                    return DeleteRefused(context, "session.notArchived",
                        $"{which} is not archived: only archived sessions are deleted. Archive it first with `alta session archive {tree.SessionId} --with-children`.");
                }
            }
        }

        var atWork = await deletion.ReadSessionsAtWorkAsync([.. trees.SelectMany(static tree => tree.Members).Select(static member => member.SessionId)]).ConfigureAwait(false);
        foreach (var tree in trees)
        {
            if (tree.Members.FirstOrDefault(member => atWork.Contains(member.SessionId)) is { } busy)
            {
                var (exitCode, code, message) = DescribeDeleteRefusal("session_in_use", tree.SessionId, busy.SessionId);
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode, message);
                return exitCode;
            }
        }

        var projects = context.Services.Get<ProjectCatalog>() is { } catalog
            ? (await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false)).ToDictionary(static project => project.Id, StringComparer.Ordinal)
            : new Dictionary<string, ProjectDescriptor>(StringComparer.Ordinal);
        var bookmarks = context.Services.Get<CodeAlta.Catalog.Bookmarks.BookmarkService>() is { } bookmarkService
            ? (await bookmarkService.ListAsync(context.CancellationToken).ConfigureAwait(false)).GroupBy(static bookmark => bookmark.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = trees.Select(tree => new DeletionRow(tree, tree.ProjectId is { } projectId && projects.TryGetValue(projectId, out var project) ? project.DisplayName : null,
            tree.Members.Sum(member => bookmarks.GetValueOrDefault(member.SessionId)))).ToArray();
        var count = trees.Sum(static tree => tree.Members.Count);

        if (dryRun)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.session.deletePlan",
                version = 1,
                correlationId = context.CorrelationId,
                count,
                sessions = rows.Select(static row => new
                {
                    sessionId = row.Tree.SessionId,
                    title = row.Tree.Title,
                    projectId = row.Tree.ProjectId,
                    project = row.Project,
                    updatedAt = row.Tree.Members[0].UpdatedAt,
                    below = row.Tree.Descendants,
                    bookmarks = row.Bookmarks,
                }).ToArray(),
            });
            return AltaExitCodes.Success;
        }

        if (await AskSessionDeletionAsync(context, count, DeletionCardDetail(rows)).ConfigureAwait(false) is { } refused) return refused;

        // A session restored while the card waited is not deleted: the card showed archived sessions. The deletion checks
        // the rest (the sessions below, the title, the scope, the work) itself.
        foreach (var tree in trees)
        {
            var now = await deletion.ReadSessionTreeAsync(tree.SessionId, context.CancellationToken).ConfigureAwait(false);
            if (now?.Members.FirstOrDefault(static member => !member.Archived) is { } restored)
            {
                return DeleteRefused(context, "session.notArchived",
                    $"Session '{restored.SessionId}' was restored while the card waited: nothing was deleted. Ask the user before archiving it again.");
            }
        }

        var deleted = new List<string>();
        int? failure = null;
        foreach (var tree in trees)
        {
            var result = await deletion.DeleteCatalogSessionTreeAsync(tree.SessionId, tree.ProjectId, tree.WorkspacePath, tree.Title, tree.Descendants)
                .WaitAsync(context.CancellationToken).ConfigureAwait(false);
            deleted.AddRange(result.Deleted);
            if (result.Status == "ok") continue;
            // What a failure leaves is never retried: the rest is not deleted, and the agent checks what is left.
            var (exitCode, code, message) = DescribeDeleteRefusal(result.Status, tree.SessionId, result.BlockingSessionId ?? tree.SessionId);
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode, message);
            failure = exitCode;
            break;
        }

        // The window reads its lists again: the sessions leave them.
        if (deleted.Count != 0) NotifySpacesChanged(context);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.deleted",
            version = 1,
            correlationId = context.CorrelationId,
            deleted,
            by = CreateProvenance(context),
        });
        return failure ?? AltaExitCodes.Success;
    }

    // A session the command does not delete as it is now: the agent changes that first, or leaves it.
    private static int DeleteRefused(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Unsupported, message);
        return AltaExitCodes.Unsupported;
    }

    // A tree of the card: the session, the name of its project (null for a chat or a project that is gone), and the
    // bookmarks of its sessions.
    internal sealed record DeletionRow(SessionTreeForDeletion Tree, string? Project, int Bookmarks);

    // The card of the session of the agent, which no permission mode answers: null when the user allowed it, else the exit code.
    private static async Task<int?> AskSessionDeletionAsync(AltaCommandContext context, int count, string detail)
    {
        if (context.Caller.Kind != "agent" || context.Caller.SourceSessionId is not { Length: > 0 } sessionId
            || context.Services.Get<SessionRuntimeService>() is not { } runtime)
            return PermissionDenied(context, "session.deleteNoUser", "Only an agent of a session deletes sessions with this command, after the user agrees on its card. Delete them in the CodeAlta window.");
        var request = new AgentSessionDeletionPermissionRequest(new ModelProviderId("alta"), sessionId, DateTimeOffset.UtcNow, null, Guid.NewGuid().ToString("N"), count, detail);
        var decision = await runtime.Permissions.RequestForSessionAsync(sessionId, request, context.CancellationToken).ConfigureAwait(false);
        return decision.Kind is AgentPermissionDecisionKind.AllowOnce
            ? null
            : PermissionDenied(context, "session.deleteDenied", decision.DescribeDenial() + " Nothing was deleted.");
    }

    // What the card says of the sessions: one line per session named, with what the agent cannot choose (where it is,
    // when it changed, what goes with it) and its title quoted, so that a title cannot pass for the rest of the card.
    internal static string DeletionCardDetail(IReadOnlyList<DeletionRow> rows)
    {
        var lines = rows.Select(static row =>
        {
            var line = new StringBuilder(Quote(Shorten(row.Tree.Title)));
            line.Append(" — ").Append(row.Tree.ProjectId is null ? "chat" : row.Project is { } project ? $"project {Quote(Shorten(project))}" : "project")
                .Append(", updated ").Append(row.Tree.Members[0].UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var below = row.Tree.Members.Count - 1;
            if (below != 0) line.Append(", ").Append(below).Append(below == 1 ? " session below" : " sessions below");
            if (row.Bookmarks != 0) line.Append(", ").Append(row.Bookmarks).Append(row.Bookmarks == 1 ? " bookmark" : " bookmarks");
            return line.ToString();
        }).ToList();

        // A card shows what it holds in full: the rows that do not fit are counted, never cut.
        var shown = Math.Min(lines.Count, MaximumDeletionCardRows);
        while (true)
        {
            var more = lines.Count - shown;
            var text = string.Join('\n', lines.Take(shown)) + (more == 0 ? string.Empty : $"\nand {more} more");
            if (text.Length <= MaximumDeletionCardDetail || shown == 0) return text;
            shown--;
        }

        static string Shorten(string text) => text.Length <= MaximumDeletionCardText ? text : string.Concat(text.AsSpan(0, MaximumDeletionCardText - 1), "…");
    }

    // Why sessions were not deleted, as the error of the command.
    internal static (int ExitCode, string Code, string Message) DescribeDeleteRefusal(string status, string sessionId, string blockingId)
    {
        var which = string.Equals(blockingId, sessionId, StringComparison.OrdinalIgnoreCase) ? $"Session '{sessionId}'" : $"Session '{blockingId}', below '{sessionId}',";
        return status switch
        {
            "session_missing" => (AltaExitCodes.NotFound, "session.changed",
                $"{which} changed since the card (its title, its project or its folder) or is gone: it was not deleted. Check it with `alta session info {blockingId}`."),
            "descendants_changed" => (AltaExitCodes.Unsupported, "session.descendantsChanged",
                $"The sessions below '{sessionId}' changed since the card: nothing of it was deleted. Run the command again to show them to the user."),
            "session_in_use" => (AltaExitCodes.Unsupported, "session.running",
                $"{which} is at work: a session is deleted when it and the sessions below it are idle. Nothing of it was deleted."),
            "has_children" => (AltaExitCodes.Unsupported, "session.descendantsChanged",
                $"{which} has a session below it that the card did not show: it was not deleted. Run the command again to show it to the user."),
            "busy" => (AltaExitCodes.Unsupported, "session.busy",
                "Another change of sessions (a deletion, a move, an archive) is under way: run the command again once it has ended."),
            "closed" => (AltaExitCodes.ServiceUnavailable, "service.closed", "CodeAlta is closing: the sessions that are left were not deleted."),
            "delete_partial" => (AltaExitCodes.Failure, "session.deletePartial",
                $"Only some sessions below '{sessionId}' were deleted: session '{blockingId}' was not. Check them with `alta session list --archived`; do not run the command again without the user."),
            _ => (AltaExitCodes.Failure, "session.deleteUnconfirmed",
                $"The sessions of '{sessionId}' may not all have been deleted: check them with `alta session list --archived`; do not run the command again without the user."),
        };
    }
}
