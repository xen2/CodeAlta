using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

// `alta session delete`: an agent deletes archived sessions with the sessions below them, after the user agrees on a card
// of its session, through the deletion of the window.
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task SessionDelete_RefusesWhatIsNotArchived_NamesTheChild_AndListsTheTreeOnADryRun()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        var caller = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var agent = Agent(caller);
        var root = await fixture.CreateAsync(agent, "--global", "--title", "Old work").ConfigureAwait(false);
        var leaf = await fixture.CreateAsync(Agent(root), "--global", "--title", "Old child").ConfigureAwait(false);

        AssertDeleteRefused(await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(root)], caller: agent).ConfigureAwait(false), "session.notArchived", Id(root));
        AssertDeleteRefused(await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(caller)], caller: agent).ConfigureAwait(false), "session.callerSession", Id(caller));

        // The session alone is archived: its child, which would go with it, is named.
        await ArchiveAsync(fixture, Id(root)).ConfigureAwait(false);
        AssertDeleteRefused(await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(root)], caller: agent).ConfigureAwait(false), "session.notArchived", Id(leaf));

        await ArchiveAsync(fixture, Id(leaf)).ConfigureAwait(false);
        // The child named too is deleted with its parent, once.
        var plan = await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(root), Id(leaf), "--dry-run"], caller: agent).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, plan.ExitCode, plan.Stdout + plan.Stderr);
        var record = ReadJsonLines(plan.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.deletePlan");
        Assert.AreEqual(2, record.GetProperty("count").GetInt32());
        var session = record.GetProperty("sessions").EnumerateArray().Single();
        Assert.AreEqual(Id(root), session.GetProperty("sessionId").GetString());
        Assert.AreEqual("Old work", session.GetProperty("title").GetString());
        CollectionAssert.AreEqual(new[] { Id(leaf) }, session.GetProperty("below").EnumerateArray().Select(static id => id.GetString()).ToArray());
        Assert.IsNotNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(root), CancellationToken.None).ConfigureAwait(false), "A dry run deletes nothing.");

        // Only an agent deletes, on the card of its session: the command line is sent to the window.
        var cli = await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(root)], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, cli.ExitCode, cli.Stdout + cli.Stderr);
        StringAssert.Contains(cli.Stdout + cli.Stderr, "session.deleteNoUser");
    }

    [TestMethod]
    public async Task SessionDelete_AsksTheUserOnACard_DeletesNothingOnDeny_AndTheWholeTreeOnDelete()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        var caller = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var agent = Agent(caller);
        var root = await fixture.CreateAsync(agent, "--global", "--title", "Old \"work\"").ConfigureAwait(false);
        var leaf = await fixture.CreateAsync(Agent(root), "--global").ConfigureAwait(false);
        await ArchiveAsync(fixture, Id(leaf)).ConfigureAwait(false);
        await ArchiveAsync(fixture, Id(root)).ConfigureAwait(false);

        // Denied: nothing goes.
        var denied = fixture.Dispatcher.InvokeAsync(["session", "delete", Id(root)], caller: agent).AsTask();
        var card = await WaitForCardAsync(fixture, Id(caller), denied).ConfigureAwait(false);
        Assert.AreEqual(AgentSessionDeletionPermissionRequest.SessionDeletionKind, card.Request.Kind);
        Assert.AreEqual(2, card.Request.SessionDeletion!.Count);
        StringAssert.StartsWith(card.Request.SessionDeletion.Detail, "\"Old \\\"work\\\"\" — chat, updated ", "the title is quoted, so that it cannot pass for the card");
        StringAssert.Contains(card.Request.SessionDeletion.Detail, "1 session below");
        Assert.AreEqual(0, await fixture.Runtime.Permissions.GrantWaitingOwnedAsync(Id(caller), SessionPermissionPolicy.Approve), "no permission mode answers it");
        Assert.IsTrue(await fixture.Runtime.Permissions.ResolveOwnedCommandAsync(card.Handle, AgentPermissionDecisionKind.Deny, CancellationToken.None));
        var refused = await denied.ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode, refused.Stdout + refused.Stderr);
        StringAssert.Contains(refused.Stdout + refused.Stderr, "session.deleteDenied");
        Assert.IsNotNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(root), CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(leaf), CancellationToken.None).ConfigureAwait(false));

        // Allowed: the child first, then its parent.
        var allowed = fixture.Dispatcher.InvokeAsync(["session", "delete", Id(root)], caller: agent).AsTask();
        card = await WaitForCardAsync(fixture, Id(caller), allowed).ConfigureAwait(false);
        Assert.IsFalse(await fixture.Runtime.Permissions.ResolveOwnedCommandAsync(card.Handle, AgentPermissionDecisionKind.AllowAlways, CancellationToken.None), "a deletion is allowed once only");
        Assert.IsTrue(await fixture.Runtime.Permissions.ResolveOwnedCommandAsync(card.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None));
        var result = await allowed.ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        var deleted = ReadJsonLines(result.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.deleted");
        CollectionAssert.AreEqual(new[] { Id(leaf), Id(root) }, deleted.GetProperty("deleted").EnumerateArray().Select(static id => id.GetString()).ToArray());
        Assert.IsNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(root), CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(leaf), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SessionDelete_ThroughTheTool_WaitsForTheCardPastTheTimeLimit_AndACardTakenBackIsNotADenial()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        var caller = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var first = await fixture.CreateAsync(Agent(caller), "--global").ConfigureAwait(false);
        var second = await fixture.CreateAsync(Agent(caller), "--global").ConfigureAwait(false);
        await ArchiveAsync(fixture, Id(first)).ConfigureAwait(false);
        await ArchiveAsync(fixture, Id(second)).ConfigureAwait(false);
        // The limit of the call is for the work of the command: the user answers the card when they see it.
        var tool = AltaSessionToolFactory.Create(fixture.Dispatcher, new AltaSessionToolOptions { SourceSessionId = Id(caller), DefaultTimeout = TimeSpan.FromMilliseconds(300) });
        static JsonElement Delete(string id) => JsonDocument.Parse(JsonSerializer.Serialize(new { args = new[] { "session", "delete", id } })).RootElement;

        var allowed = tool.Handler(CreateInvocation(Delete(Id(first))), CancellationToken.None);
        var card = await WaitForCardAsync(fixture, Id(caller), allowed).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false);
        Assert.IsFalse(allowed.IsCompleted, "the card still waits past the time limit");
        Assert.IsTrue(await fixture.Runtime.Permissions.ResolveOwnedCommandAsync(card.Handle, AgentPermissionDecisionKind.AllowOnce, CancellationToken.None));
        var result = await allowed.ConfigureAwait(false);
        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(first), CancellationToken.None).ConfigureAwait(false));

        // The run stops while the card waits: the card goes, and the agent is not told that the user refused.
        using var run = new CancellationTokenSource();
        var stopped = tool.Handler(CreateInvocation(Delete(Id(second))), run.Token);
        await WaitForCardAsync(fixture, Id(caller), stopped).ConfigureAwait(false);
        await run.CancelAsync().ConfigureAwait(false);
        var taken = await stopped.ConfigureAwait(false);
        Assert.IsFalse(taken.Success);
        StringAssert.Contains(AssertTextItem(taken), "session.deleteNotAnswered");
        Assert.IsNotNull(await fixture.Runtime.ResolveOwnedSessionAsync(Id(second), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ATimeLimit_DoesNotRunWhilePaused_AndRunsOnWithTheTimeLeft()
    {
        using var run = new CancellationTokenSource();
        using var limit = new AltaTimeLimit(TimeSpan.FromMilliseconds(300), run.Token);
        var pause = limit.Pause();
        using (limit.Pause())
        {
        }

        await Task.Delay(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        Assert.IsFalse(limit.Token.IsCancellationRequested, "a pause nested in another one does not resume the time");
        pause.Dispose();
        pause.Dispose();
        Assert.IsTrue(limit.Token.WaitHandle.WaitOne(TestTime.Seconds(10)), "the time runs again after the last pause");

        using var stopped = new CancellationTokenSource();
        using var paused = new AltaTimeLimit(TimeSpan.FromMinutes(5), stopped.Token);
        using (paused.Pause())
        {
            await stopped.CancelAsync().ConfigureAwait(false);
            Assert.IsTrue(paused.Token.IsCancellationRequested, "a pause does not hold back the call that stops");
        }
    }

    [TestMethod]
    public async Task SessionDelete_ReachesOnlySessionsOfTheOwnSpace_AndTheOwnDescendants()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync(withOwner: true).ConfigureAwait(false);
        var caller = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var agent = Agent(caller);
        var unrelated = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        var own = await fixture.CreateAsync(agent, "--global", "--space", "personal").ConfigureAwait(false);
        await ArchiveAsync(fixture, Id(unrelated)).ConfigureAwait(false);
        await ArchiveAsync(fixture, Id(own)).ConfigureAwait(false);

        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(unrelated), "--dry-run"], caller: agent).ConfigureAwait(false), "space.notOwnSpace");
        var plan = await fixture.Dispatcher.InvokeAsync(["session", "delete", Id(own), "--dry-run"], caller: agent).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, plan.ExitCode, plan.Stdout + plan.Stderr);
    }

    [TestMethod]
    public void SessionDeleteCard_NamesTwentySessions_AndCountsTheOthers()
    {
        var updated = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 23).Select(index => new BuiltInAltaCommandContributor.DeletionRow(
            new SessionTreeForDeletion($"s{index}", index % 2 == 0 ? "p" : null, "C:/work",
                [new($"s{index}", null, $"Session {index}" + new string('x', index == 0 ? 100 : 0), true, updated)]),
            index % 2 == 0 ? "Project" : null, index == 1 ? 2 : 0)).ToArray();

        var lines = BuiltInAltaCommandContributor.DeletionCardDetail(rows).Split('\n');

        Assert.HasCount(BuiltInAltaCommandContributor.MaximumDeletionCardRows + 1, lines);
        Assert.AreEqual("and 3 more", lines[^1]);
        StringAssert.StartsWith(lines[0], "\"Session 0xxx");
        StringAssert.Contains(lines[0], "…\" — project \"Project\", updated 2026-11-1");
        Assert.IsLessThan(200, lines[0].Length, "a long title is shortened");
        StringAssert.Contains(lines[1], "\"Session 1\" — chat, updated ");
        StringAssert.EndsWith(lines[1], ", 2 bookmarks");
    }

    // One session at a time: the fixture lists no sessions, which --with-children reads.
    private static async Task ArchiveAsync(SpaceResourceFixture fixture, string sessionId)
    {
        var archived = await fixture.Dispatcher.InvokeAsync(["session", "archive", sessionId], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, archived.ExitCode, archived.Stdout + archived.Stderr);
    }

    private static void AssertDeleteRefused(AltaCommandResult result, string code, string named)
    {
        Assert.AreEqual(AltaExitCodes.Unsupported, result.ExitCode, result.Stdout + result.Stderr);
        var error = ReadJsonLines(result.Stdout + result.Stderr).First(static line => line.GetProperty("type").GetString() == "alta.error");
        Assert.AreEqual(code, error.GetProperty("code").GetString());
        StringAssert.Contains(error.GetProperty("message").GetString(), named);
    }

    // The card the command waits on, on the session of the agent.
    private static async Task<SessionOwnedPermissionSnapshot> WaitForCardAsync(SpaceResourceFixture fixture, string sessionId, Task command)
    {
        var deadline = DateTime.UtcNow + TestTime.Seconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (command.IsCompleted)
            {
                Assert.Fail("The command ended without a card: " + command switch
                {
                    Task<AltaCommandResult> { IsCompletedSuccessfully: true } ended => ended.Result.Stdout + ended.Result.Stderr,
                    Task<AgentToolResult> { IsCompletedSuccessfully: true } ended => AssertTextItem(ended.Result),
                    _ => command.Exception?.Message,
                });
            }

            var page = await fixture.Runtime.Permissions.ListOwnedCommandsAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (page.Entries.Count != 0) return page.Entries[0];
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail("No card was shown.");
        return null!;
    }
}
