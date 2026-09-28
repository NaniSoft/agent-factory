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
    public static string For(Round round)
    {
        ArgumentNullException.ThrowIfNull(round);

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
