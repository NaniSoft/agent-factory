namespace AgentFactory.Rounds;

using System.Text;

/// <summary>
/// The brief one round is handed: the issue, the project it is being built for, and
/// whatever the reviewer last said about it. The factory's own words around the words
/// that are not the factory's — a reviewer's feedback is quoted into here verbatim and
/// never paraphrased, because the next round is being asked to do what they said.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here decides what the round should do. It says which work item this is, what
/// the issue asked for, what the reviewer's reasons were, and leaves the rest to the
/// agent: the factory does not know a repository's toolchain, and a brief that told the
/// agent which commands to run would be the factory imposing a test convention on the
/// project it is building (ADR-0011).
/// </para>
/// <para>
/// The one instruction it does give is about how to work rather than what to build: the
/// round is asked to prefix its shell work with the image's recording wrapper. The image
/// documents that this under-reports, because a command the agent reaches by another
/// route is not recorded, and that the guarantee is carried by git rather than by the
/// command log. Pointing the agent at the wrapper is the option the image asks whoever
/// wires the agent up to choose, and the other option — pointing the agent's own shell at
/// the wrapper — does not survive contact with OpenCode v2. See AGENTS.md.
/// </para>
/// </remarks>
public static class RoundBrief
{
    /// <summary>
    /// The brief for one round. Plain markdown, because that is what a maintainer wrote
    /// the issue in and what a reviewer wrote their reasons in, and neither of them wrote
    /// it for a template.
    /// </summary>
    /// <remarks>
    /// Two documents, not one with a section that is sometimes left out. A build brief tells
    /// the agent to commit its work and stop; a routing brief tells it to write a verdict and
    /// not to touch anything. The two briefs have nothing else in common — no tree, no tests,
    /// no commit — so making one conditional document out of them would leave every sentence
    /// hedged about which round it was in, which is the kind of brief a model follows worse
    /// rather than better (ADR-0015).
    /// </remarks>
    public static string For(Round round)
    {
        ArgumentNullException.ThrowIfNull(round);

        return round.Purpose == RoundPurpose.Route ? RoutingBrief(round) : BuildingBrief(round);
    }

    /// <summary>
    /// What a routing round is handed: the issue, and the shape of the answer. No repository,
    /// no base branch and no commit instruction, because it fetches nothing and commits
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The answer's shape is in the brief rather than being left to the agent, because the
    /// host reads it and a verdict the factory cannot read is a work item parked saying so.
    /// The agent is told plainly that a verdict nobody can read is worth nothing, which is
    /// not a threat but the truth about the thing it is writing for.
    /// </remarks>
    private static string RoutingBrief(Round round)
    {
        var brief = new StringBuilder();

        brief.Append("# The issue\n\n")
            .Append("Project: ").Append(round.Project).Append('\n')
            .Append("Repository: ").AppendLine(round.RepoUrl)
            .Append("Issue: #").Append(round.IssueNumber).Append(' ').AppendLine(round.IssueTitle)
            .AppendLine()
            .AppendLine("This repository has not been fetched and you are not going to read it. You have the issue ")
            .AppendLine("and nothing else, and that is enough: you are being asked what kind of work this is, ")
            .AppendLine("not what the work would be.");

        Section(brief, "What the issue asked for", round.IssueBody);

        brief.AppendLine()
            .AppendLine("## What to decide")
            .AppendLine()
            .AppendLine("Answer with exactly one of these three, and nothing else:")
            .AppendLine()
            .AppendLine("- `Build` — the issue is already a build ticket. It is specific: it names what to change, ")
            .AppendLine("  and a worker could be handed it as it stands and a reviewer could judge the result.")
            .AppendLine("- `Grill` — it is buildable, but branches of the design are undecided and a person has to ")
            .AppendLine("  decide them before anyone writes code. Write the questions as `questions`.")
            .AppendLine("- `Wayfinder` — it is bigger than one round of work and the way to the destination is not ")
            .AppendLine("  visible. Chart it as `map`: the destination, the decision tickets you can already ")
            .AppendLine("  specify, and the fog you cannot.")
            .AppendLine()
            .AppendLine("If it is genuinely unclear which of the three it is, say `Build` and put your doubt in ")
            .AppendLine("`because`. A wrong `Build` costs a reviewer one click; a wrong `Grill` or `Wayfinder` costs ")
            .AppendLine("somebody an afternoon of questions they did not need to answer.")
            .AppendLine()
            .AppendLine("Judge the shape of the request, not its difficulty. Whether this repository already does ")
            .AppendLine("the thing is a question for whoever builds it, not for you: a ticket that is already ")
            .AppendLine("implemented is still a `Build`, and finding that out is the round's job, not yours.")
            .AppendLine()
            .AppendLine("## How to answer")
            .AppendLine()
            .AppendLine("Write one JSON object to /out/routing.json. The host reads it after this container is ")
            .AppendLine("gone, and it is the only thing you produce.")
            .AppendLine()
            .AppendLine("```json")
            .AppendLine("{")
            .AppendLine("  \"schema\": \"" + Routing.RoutingReader.Schema + "\",")
            .AppendLine("  \"verdict\": \"Build\",")
            .AppendLine("  \"because\": \"the issue names what to change and a reviewer could judge it\"")
            .AppendLine("}")
            .AppendLine("```")
            .AppendLine()
            .AppendLine("`because` is required and is read by a human, so write it in words rather than a label.")
            .AppendLine()
            .AppendLine("For `Grill`, add a `questions` array. Each entry is the question and why the answer is ")
            .AppendLine("needed, and asking a question is a claim that a person must answer it:")
            .AppendLine()
            .AppendLine("```json")
            .AppendLine("\"questions\": [")
            .AppendLine("  { \"question\": \"...\", \"because\": \"what the answer unblocks\" }")
            .AppendLine("]")
            .AppendLine("```")
            .AppendLine()
            .AppendLine("For `Wayfinder`, add a `map`. Each ticket names the others it `blocks` **by title**, ")
            .AppendLine("because the host creates the issues in dependency order and a title is the only handle ")
            .AppendLine("that exists before they have numbers:")
            .AppendLine()
            .AppendLine("```json")
            .AppendLine("\"map\": {")
            .AppendLine("  \"destination\": \"what reaching the end of this looks like\",")
            .AppendLine("  \"notes\": \"standing preferences for this effort\",")
            .AppendLine("  \"notYetSpecified\": [\"a decision you can see but cannot yet phrase sharply\" ],")
            .AppendLine("  \"outOfScope\": [\"work ruled beyond the destination\" ],")
            .AppendLine("  \"tickets\": [")
            .AppendLine("    { \"title\": \"...\", \"body\": \"the question this resolves\", \"blocks\": [ ] }")
            .AppendLine("  ]")
            .AppendLine("}")
            .AppendLine("```")
            .AppendLine()
            .AppendLine("Do not change anything else. There is no repository here to change, and nothing you ")
            .AppendLine("write outside /out/routing.json is read by anybody.");

        return brief.ToString();
    }

    private static string BuildingBrief(Round round)
    {
        var brief = new StringBuilder();

        brief.Append("# The work item\n\n")
            .Append("Project: ").Append(round.Project).Append('\n')
            .Append("Repository: ").Append(round.RepoUrl).Append('\n')
            .Append("Issue: #").Append(round.IssueNumber).Append(' ').AppendLine(round.IssueTitle)
            .Append("Base branch: ").AppendLine(round.BaseBranch)
            .Append("Working tree: /work, fetched at the base branch above. Do not change branches.\n");

        Section(brief, "What the issue asked for", round.IssueBody);
        Section(brief, "What the reviewer asked for last time", round.Feedback);

        brief.AppendLine()
            .AppendLine("## How to work")
            .AppendLine()
            .AppendLine("This repository's own configuration and its own scripts decide what running its tests means. ")
            .AppendLine("Read them and do what they say. Nothing outside this container can be reached, and nothing you ")
            .AppendLine("do here can reach a remote: commit your work to the branch you are on and stop there.")
            .AppendLine()
            .AppendLine("Run your shell work through the image's recording wrapper, so the round records what it ran:")
            .AppendLine()
            .AppendLine("```")
            .AppendLine("run <command>          # instead of running it directly")
            .AppendLine("run -o 'why' <command>  # with a label, which reads better on the board")
            .AppendLine("```")
            .AppendLine()
            .AppendLine("Commit your change when the work is done. The host retrieves the commit; nothing here pushes.")
            .AppendLine()
            .AppendLine("When you are finished, write a short note to /out/note.md saying what you changed and why. ")
            .AppendLine("It is optional and it is the only thing here that is your own words rather than an observation, ")
            .AppendLine("so a round without one is recorded whole and a round whose note is wrong is contradicted by the diff.");

        return brief.ToString();
    }

    private static void Section(StringBuilder brief, string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        brief.AppendLine().Append("## ").AppendLine(heading).AppendLine().AppendLine(body.Trim()).AppendLine();
    }
}
