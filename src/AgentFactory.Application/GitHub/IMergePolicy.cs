namespace AgentFactory.Application.GitHub;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// "Ship the change this work item produced", which is the whole of what the loop ever
/// asks about merging.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the merge's seam, and it is the one port whose implementation is not
/// an adapter.</strong> The other eight are declared here and implemented in
/// `Infrastructure`, because what is behind them is a boundary — a network, a disk, a
/// clock. What is behind this one is <see cref="MergePolicy"/>, which is policy, and
/// policy lives here. So this is a seam in the ordinary sense: the loop holds it, the
/// composition root supplies the real one, and the suite substitutes a fake — which is
/// exactly why it is an interface rather than a direct dependency on
/// <see cref="MergePolicy"/>. Without one, every test of the loop's merge behaviour would
/// need a repository on a disk and a round's tree to point at, for a test about what the
/// loop does with an answer.
/// </para>
/// <para>
/// The arguments are deliberately what they were: the work item, and nothing else. The
/// loop does not know what a pull request is, does not know what a branch is called and
/// does not know how a merge is sequenced; it knows the work item whose change is
/// shipping and it learns whether it shipped. Everything else is resolved behind this
/// call, out of the store, by the policy.
/// </para>
/// </remarks>
public interface IMergePolicy
{
    /// <summary>
    /// Pushes the work item's change, opens the pull request if there is not already one,
    /// and merges it, from the host and under the factory's own credential — the container
    /// that wrote the change holds nothing that can write to a remote (ADR-0006).
    /// </summary>
    /// <param name="workItem">
    /// The work item whose change is shipping. It is handed over rather than looked up, so
    /// that "which change" is a fact the caller already holds and cannot be disagreed
    /// with; the policy reads the work item's own rounds out of the store.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <exception cref="Failures.FactoryFailure">
    /// The change was refused, and the refusal is classified: permanent for a decline, a
    /// branch somebody else owns, a conflict, or a tree that never came out of its
    /// container; transient for a base that moved under the attempt or a connection that
    /// was not there.
    /// </exception>
    Task MergeAsync(WorkItem workItem, CancellationToken cancellationToken);
}