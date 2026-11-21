namespace AgentFactory.App.Fleet;

/// <summary>
/// Host-authored issue-facing text: the PR body for a finished run and the
/// failure comment after both attempts (ADR 0001 keeps these host-owned).
/// The run prompt itself is a pipeline template rendered per phase — see
/// <see cref="Placeholders.Render"/>; v1's house template lives on, verbatim,
/// as the seeded develop phase in <see cref="ConfigStore"/>.
/// </summary>
public static class PromptBuilder
{
    /// <summary>PR body for a finished run: phase walk with per-phase sessions,
    /// then the final phase's summary (host-authored, ADR 0001). Child runs
    /// link their parent's `.github` ticket instead of a repo issue.</summary>
    public static string PrBody(RunRecord run)
    {
        var phaseLines = string.Join("\n", run.Phases.Select(p =>
            $"- **{p.Name}**{Pin(p)} — session `{p.SessionId}`, {p.TokensIn}→{p.TokensOut} tokens, ${p.Cost:0.####}"));
        var summary = run.Phases.LastOrDefault()?.Summary ?? run.AgentSummary ?? "";

        if (run.Kind == RunKind.Child)
        {
            return $"""
                {run.IssueTitle}

                Opened by the Agent Factory fleet — no human wrote this PR.

                - Child run: `{run.RunId}` (order {run.ChildOrder + 1}, attempt {run.Attempt}, pipeline {run.PipelineName})
                - Parent: `{run.ParentRunId}` on {run.IssueUrl}
                - Target: `{run.Repo}`
                - Branch: `{run.Branch}`

                ## Phases

                {phaseLines}

                ## Agent summary

                {summary}
                """;
        }

        return $"""
            Implements #{run.IssueNumber}: {run.IssueTitle}

            Opened by the Agent Factory fleet — no human wrote this PR.

            - Run: `{run.RunId}` (attempt {run.Attempt}, pipeline {run.PipelineName})
            - Target: `{run.Repo}`
            - Branch: `{run.Branch}`

            ## Phases

            {phaseLines}

            ## Agent summary

            {summary}

            Closes #{run.IssueNumber}
            """;
    }

    /// <summary>The profile pin a phase ran under, for the PR-body phase list
    /// (ticket 030): empty for engine-default phases.</summary>
    private static string Pin(PhaseRecord p) =>
        p.ProfileName is null ? "" : $" ({p.Flavour ?? "pinned"}: {p.ProfileName} · {p.ModelId})";

    /// <summary>Failure comment posted on the issue after both attempts fail.</summary>
    public static string FailureComment(RunRecord run, string logHint)
    {
        return $"""
            The Agent Factory fleet could not complete this issue after {run.Attempt} attempt(s).

            Last error: {run.Error ?? "unknown"}

            Run `{run.RunId}` (pipeline {run.PipelineName ?? "n/a"}, {run.Phases.Count} phase(s) walked);
            last engine session `{run.SessionId ?? "n/a"}`. {logHint}
            Remove and re-add the `agent-factory` label to retry.
            """;
    }
}
