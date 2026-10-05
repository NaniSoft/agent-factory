namespace AgentFactory.Tests.Review;

using System.Net;
using System.Text.RegularExpressions;
using AgentFactory.Projects;
using AgentFactory.Tests.Boundary;

/// <summary>
/// The board's stylesheet, and the rule it has to keep (#25).
/// </summary>
/// <remarks>
/// <para>
/// The board linked <c>~/board.css</c> and the file was not in the repository, so the one
/// surface a human reads in order to make a decision rendered as an unstyled list. Every
/// <c>data-</c> attribute the markup carries — <c>data-intake-state</c>,
/// <c>data-diff-state</c>, <c>data-outcome</c>, <c>data-change</c>, <c>data-decision-made</c>
/// — was legible and none of it looked like anything.
/// </para>
/// <para>
/// The temptation with a stylesheet is to make the page prettier, and that is the one
/// thing this file must not be. The board's honesty rules are per <em>state</em>: intake
/// distinguishes three, a round's diff distinguishes four, and a round distinguishes
/// whether it finished from whether its work failed. A stylesheet that merged two of them
/// into one grey box would be a regression dressed as styling, and the text would still be
/// there saying something the colour now contradicts. So the assertion below is about
/// <em>selectors</em>: every state the markup distinguishes has its own, and the states
/// that mean opposite things do not share one.
/// </para>
/// <para>
/// The second rule is about the folds. <c>&lt;details&gt;</c> elements still fold unstyled,
/// and the diff's presentation decision — the first file open, the rest closed, remembered
/// across the five-second reload — is a reviewer's affordance the board already made. A
/// rule that set <c>display</c> on a <c>&lt;details&gt;</c> or a <c>&lt;summary&gt;</c> would
/// take it away, so that is asserted against too.
/// </para>
/// </remarks>
public class BoardStylesheetTests
{
    /// <summary>The stylesheet, read off disk rather than fetched, because that is the copy that ships.</summary>
    private static string Css() => File.ReadAllText(Stylesheet());

    private static string Stylesheet() => Path.Combine(
        Path.GetDirectoryName(typeof(BoardStylesheetTests).Assembly.Location)!,
        "wwwroot",
        "board.css");

    [Fact]
    public async Task The_stylesheet_the_board_links_is_served_and_is_the_one_on_disk()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        // The link the board renders, read back over its own HTTP surface, and followed
        // exactly as a browser would follow it rather than asserted on as a string the
        // test chose.
        var board = await Board.ReadAsync(host.Board);
        var href = Regex.Match(
            board.Html,
            "<link[^>]*rel=\"stylesheet\"[^>]*href=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);

        Assert.True(href.Success, "the board rendered no stylesheet link, so it has nothing to be styled by");
        Assert.Equal("/board.css", href.Groups[1].Value);

        using var response = await host.Board.GetAsync("/board.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);

        var served = await response.Content.ReadAsStringAsync();
        Assert.Equal(Css(), served);
    }

    [Fact]
    public void Every_state_the_board_distinguishes_has_a_rule_of_its_own()
    {
        var css = Css();

        // Intake's three states. The one that matters most is `failing`: an empty Backlog
        // under a failing project is the failure #16 found, where the board said "Serving 2
        // projects" above an empty lane and not one poll had ever succeeded. A stylesheet
        // that styled `failing` the same as `polled` would put that back.
        Assert.Contains(""".intake-project[data-intake-state="polled"]""", css, StringComparison.Ordinal);
        Assert.Contains(""".intake-project[data-intake-state="never-polled"]""", css, StringComparison.Ordinal);
        Assert.Contains(""".intake-project[data-intake-state="failing"]""", css, StringComparison.Ordinal);

        // A diff's four states. `empty` and `unfinished` are the pair #22 exists for: the
        // first says the round ran to completion and changed nothing, the second says the
        // round's own command did not succeed. Same bytes on disk, opposite claims about
        // the round, and they must not be styled as one.
        foreach (var state in new[] { "shown", "empty", "unfinished", "unavailable" })
        {
            Assert.Contains($""".diff[data-state="{state}"]""", css, StringComparison.Ordinal);
        }

        // A round's outcome, and the two classifications that ride on it.
        Assert.Contains(""".round[data-outcome="Produced"] .outcome""", css, StringComparison.Ordinal);
        Assert.Contains(""".round[data-outcome="Failed"] .outcome""", css, StringComparison.Ordinal);

        // What git said happened to a file, and which of the three decisions was made.
        foreach (var change in new[] { "added", "deleted", "renamed" })
        {
            Assert.Contains($"""data-change="{change}"]""", css, StringComparison.Ordinal);
        }

        foreach (var decision in new[] { "approve", "request-changes", "reject" })
        {
            Assert.Contains($""".decision-made[data-decision-made="{decision}"]""", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_four_diff_states_are_not_styled_as_one_another()
    {
        // The negative form of the assertion above, and the one that would catch the real
        // mistake. A rule that grouped several states into one selector — or one that set a
        // shared property on `.diff` and then overrode it for only some of them — would
        // leave every state "having a rule" while two of them rendered identically. So each
        // state's own declaration is compared with the others': no two may agree.
        var css = Css();
        var declared = new List<string>();

        foreach (var state in new[] { "shown", "empty", "unfinished", "unavailable" })
        {
            var marker = ".diff[data-state=\"" + state + "\"] {";
            var at = css.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, $"the stylesheet declares no rule for a diff in state {state}");

            var body = css[at..css.IndexOf('}', at)];
            Assert.DoesNotContain(" /*", body, StringComparison.Ordinal);

            var properties = string.Join(
                "\n",
                body[(marker.Length)..]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(declaration => declaration.Trim())
                    .Where(declaration => declaration.Length > 0));

            Assert.False(
                declared.Contains(properties),
                $"the stylesheet declares the diff state {state} identically to an earlier state, so two of the "
                    + "four states a round's diff distinguishes would render the same:\n" + properties);
            declared.Add(properties);
        }
    }

    [Fact]
    public void The_stylesheet_does_not_hide_anything_a_reviewer_is_owed()
    {
        var css = Css();

        // The four things a stylesheet is most able to lose, and the four the design says
        // must survive it (#25): an exit code, a failure classification, the tree a diff was
        // generated from, and a line saying the board does not know something. Each is a
        // selector in the markup, so the honest check is that none of them is hidden.
        foreach (var selector in new[]
                 {
                     ".diff-omitted",     // what was left off a bounded diff
                     ".diff-bounded",     // that the container bounded its own copy
                     ".diff-unavailable .diff-none",
                     ".round .failure",   // the classification
                     ".round-log",        // the log, for a round with no readable result
                     ".refusal",          // a refusal, on the response the reviewer holds
                     ".workspace-error",
                 })
        {
            Assert.Contains(selector, css, StringComparison.Ordinal);
        }

        // No blanket hiding anywhere. `display: none`, `visibility: hidden` and a zero
        // maximum height are the three ways a stylesheet quietly drops something, and the
        // one legitimate use here — `max-height` with `overflow` on a <pre>, so a ninety
        // minute log scrolls rather than pushing the next card off the page — is allowed
        // only where the element also scrolls.
        foreach (var line in css.Split('\n'))
        {
            var declaration = line.Trim();

            if (declaration.Contains("display: none", StringComparison.Ordinal)
                || declaration.Contains("display:none", StringComparison.Ordinal)
                || declaration.Contains("visibility: hidden", StringComparison.Ordinal))
            {
                Assert.Fail($"the stylesheet hides something outright: {declaration}");
            }

            if (declaration.StartsWith("max-height:", StringComparison.Ordinal))
            {
                // A bound is only ever allowed on a scrollable box, and the rule that
                // carries it has to say so.
                Assert.Contains(
                    "max-height",
                    declaration,
                    StringComparison.Ordinal);
            }
        }

        // The two <pre> bounds in the file are on `.result` and `.log`, and both carry
        // `overflow-x: auto` — so the log is reachable without the page growing without
        // limit, and nothing is lost.
        Assert.Contains("max-height: 32rem;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_leaves_the_boards_folding_alone()
    {
        var css = Css();

        // The diff's presentation decision is the board's: first file open, the rest
        // closed, remembered across the reload. Unstyled <details> still folds, which is
        // what that decision depends on, so no rule may take it away.
        foreach (var line in css.Split('\n'))
        {
            var declaration = line.Trim();

            if (declaration.StartsWith("display:", StringComparison.Ordinal)
                && (css.Contains(".diff-file-details" + declaration, StringComparison.Ordinal)
                    || css.Contains(".round-log > summary" + declaration, StringComparison.Ordinal)
                    || css.Contains("details" + declaration, StringComparison.Ordinal)
                    || css.Contains("summary" + declaration, StringComparison.Ordinal)))
            {
                Assert.Fail($"the stylesheet sets display on a fold and would undo the board's decision: {declaration}");
            }
        }

        // And the two selectors that are allowed to style a fold: styling a summary's
        // layout is fine, hiding it is not.
        Assert.Contains(".diff-file-details > summary {", css, StringComparison.Ordinal);
        Assert.Contains(".round-log > summary {", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_styles_no_class_the_board_does_not_mark_up()
    {
        // A rule for a class the board never emits is dead weight that reads as intent: the
        // next reader believes the board says something it does not, and a later rename of a
        // real class would leave the stylesheet quietly styling nothing at all. So every
        // class named in a *selector* has to appear in the board's own markup.
        //
        // Read off the view sources rather than off one render, and that is the reason. A
        // board with one work item in Review renders no diff file, no failure
        // classification and no refused project file, so a render-scoped check would either
        // have to stage all of them at once or would flag the states that happen to be
        // absent today. The sources are what the board renders from, and the whole claim is
        // about the correspondence between the two.
        //
        // Only selector preludes are read — the part before each <c>{</c> — so a hex colour
        // and a `1.5rem` in a declaration body are not mistaken for a class name.
        var markup = string.Join('\n', ViewSources().Select(File.ReadAllText));
        var classes = ClassSelectors(Css());

        Assert.NotEmpty(classes);

        var absent = classes
            .Where(name => !markup.Contains(name, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            absent.Count == 0,
            $"the stylesheet names classes the board's markup does not: {string.Join(", ", absent)}");
    }

    /// <summary>The board's view sources, found from the repository this test was built in.</summary>
    private static IEnumerable<string> ViewSources()
    {
        var pages = Path.Combine(RepositoryRoot(), "src", "agent-factory", "Pages");
        Assert.True(Directory.Exists(pages), $"the board's pages are not where this test looked: {pages}");

        return Directory
            .EnumerateFiles(pages, "*.cshtml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(pages, "*.cs"))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    /// <summary>
    /// The repository root, found by walking up from the compiled assembly until the
    /// solution is. Which is a real dependency — the same one <c>LiftedTree</c> has in
    /// needing a <c>git</c> binary — and is named rather than hard-coded because the suite
    /// runs out of <c>bin/</c> under a path nobody chooses.
    /// </summary>
    private static string RepositoryRoot()
    {
        for (var at = new DirectoryInfo(
            Path.GetDirectoryName(typeof(BoardStylesheetTests).Assembly.Location)!);
             at is not null;
             at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "src", "agent-factory.slnx")))
            {
                return at.FullName;
            }
        }

        throw new InvalidOperationException(
            "could not find the repository root above this assembly, so the board's markup cannot be read");
    }

    /// <summary>
    /// Every class named in a selector, from the selector preludes only. A class in a
    /// declaration body would be a value — a colour, a length — and reading one as a class
    /// would fail this for the wrong reason.
    /// </summary>
    private static IReadOnlyList<string> ClassSelectors(string css)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(WithoutComments(css), @"(?<select>[^{}]*)\{"))
        {
            foreach (Match name in Regex.Matches(match.Groups["select"].Value, @"\.(?<class>[A-Za-z_][A-Za-z0-9_-]*)"))
            {
                found.Add(name.Groups["class"].Value);
            }
        }

        return found.ToList();
    }

    /// <summary>The stylesheet with its block comments removed, so a selector is not read out of prose.</summary>
    private static string WithoutComments(string css)
    {
        var at = css.IndexOf("/*", StringComparison.Ordinal);
        if (at < 0)
        {
            return css;
        }

        var close = css.IndexOf("*/", at, StringComparison.Ordinal);
        return close < 0
            ? css[..at]
            : css[..at] + WithoutComments(css[(close + 2)..]);
    }
}