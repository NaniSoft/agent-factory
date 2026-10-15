namespace AgentFactory.Domain.Issues;

/// <summary>
/// One turn of an issue's conversation: who said it, what they said, and when. The
/// created time is carried because it is how the loop tells an answer from the silence it
/// landed in — a Waiting work item watches its questions issue for anything said after the
/// question was asked, and a timestamp is the only honest test of that.
/// </summary>
/// <remarks>
/// It is the factory's own vocabulary rather than GitHub's, for the same reason
/// <c>WorkItem</c> is: an issue is a thing this factory reasons about, and a round's brief
/// carries the whole conversation with it. It is in `Domain` because <c>Round</c> is —
/// a brief that could not name the comments it was handing the agent would be a Domain
/// type pointing outward at the adapter that read them.
/// </remarks>
public sealed record IssueComment(string Author, string Body, DateTimeOffset CreatedUtc);
