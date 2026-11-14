namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Deletes a session with every session below it, as <b>Delete with all child sessions…</b> of the window does: first
/// read what would be deleted and show it, then delete exactly that.
/// </summary>
public interface ISessionDeletion
{
    /// <summary>Reads a session and the sessions below it, as a deletion would find them; nothing is claimed.</summary>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The session and the sessions below it; null when the session is not in the catalog of this computer.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    Task<SessionTreeForDeletion?> ReadSessionTreeAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Tells which of these sessions something goes on in: a run, a queue, a question, a job, background work.</summary>
    /// <param name="sessionIds">The sessions to look at.</param>
    /// <returns>Those at work.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionIds"/> is null.</exception>
    Task<IReadOnlySet<string>> ReadSessionsAtWorkAsync(IReadOnlyCollection<string> sessionIds);

    /// <summary>
    /// Deletes a session with every session below it, when they are still the ones that were shown and none is at work.
    /// </summary>
    /// <param name="sessionId">Exact catalog session identity.</param>
    /// <param name="projectId">Exact project identity, or null for global scope.</param>
    /// <param name="workspacePath">Exact catalog workspace path.</param>
    /// <param name="confirmedTitle">The title that was shown.</param>
    /// <param name="expectedDescendants">The sessions below it that were shown.</param>
    /// <returns>What was deleted; see <see cref="OwnedSessionTreeDeletion"/> for the statuses.</returns>
    /// <exception cref="ArgumentException">A required identity or confirmation is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="expectedDescendants"/> is null.</exception>
    Task<OwnedSessionTreeDeletion> DeleteCatalogSessionTreeAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle,
        IReadOnlyCollection<string> expectedDescendants);
}

/// <summary>A session and the sessions below it, as a deletion of them reads them before anything is deleted.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">Its project; null for a chat.</param>
/// <param name="WorkspacePath">Its folder, as the list of sessions has it.</param>
/// <param name="Members">The session first, then the sessions below it, each after its parent.</param>
public sealed record SessionTreeForDeletion(string SessionId, string? ProjectId, string WorkspacePath, IReadOnlyList<SessionTreeMember> Members)
{
    /// <summary>Gets the title of the session, as the lists show it.</summary>
    public string Title => Members[0].Title;

    /// <summary>Gets the sessions below the session.</summary>
    public IReadOnlyList<string> Descendants => [.. Members.Skip(1).Select(static member => member.SessionId)];
}

/// <summary>A session of a <see cref="SessionTreeForDeletion"/>.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ParentSessionId">The session it is shown under; null for the session the tree starts from.</param>
/// <param name="Title">Its title, as the lists show it.</param>
/// <param name="Archived">Whether it is archived.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
public sealed record SessionTreeMember(string SessionId, string? ParentSessionId, string Title, bool Archived, DateTimeOffset UpdatedAt);

public sealed partial class OwnedSessionCommandService : ISessionDeletion
{
    /// <inheritdoc/>
    public Task<SessionTreeForDeletion?> ReadSessionTreeAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _runtime.ReadSessionTreeForDeletionAsync(sessionId, cancellationToken);
    }
}
