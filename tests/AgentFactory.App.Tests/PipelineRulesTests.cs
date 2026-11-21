using AgentFactory.App.Api;
using AgentFactory.App.Fleet;

namespace AgentFactory.App.Tests;

/// <summary>
/// The save-time rules the API applies to a pipeline. They are the same rules
/// the board validates in the browser, and the API is the authoritative copy.
/// </summary>
public sealed class PipelineRulesTests
{
    private static AgentProfile Go() => new()
    {
        Id = "go",
        Name = "Go",
        Flavour = AgentFlavour.OpencodeGo,
        Models = ["opencode-go/deepseek-v4.1-flash"],
    };

    private static PipelineDefinition Standard() => new()
    {
        Id = "standard",
        Name = "Standard",
        Phases =
        [
            new PhaseDefinition
            {
                Name = "develop",
                Skills = ["tdd"],
                Template = "Implement {{issue_title}} in {{repo}}.",
            },
        ],
    };

    private static AgentProfile[] Profiles() => [Go()];

    [Fact]
    public void A_valid_pipeline_has_no_errors()
    {
        var errors = PipelineRules.Validate(Standard(), Profiles(), ["tdd"]);

        Assert.Empty(errors);
    }

    [Fact]
    public void A_new_pipeline_gets_a_slug_id()
    {
        var pipeline = Standard();
        pipeline.Id = "";
        pipeline.Name = "Standard Copy!";

        PipelineRules.EnsureId(pipeline, [Standard()]);

        Assert.Equal("standard-copy", pipeline.Id);
    }

    [Fact]
    public void An_id_that_already_exists_gets_a_suffix()
    {
        var pipeline = Standard();
        pipeline.Id = "";
        pipeline.Name = "Standard";

        PipelineRules.EnsureId(pipeline, [Standard()]);

        Assert.Equal("standard-2", pipeline.Id);
    }

    [Fact]
    public void An_unknown_placeholder_is_rejected_with_the_allowed_set_in_the_message()
    {
        var pipeline = Standard();
        pipeline.Phases[0].Template = "{{nope}}";

        var errors = PipelineRules.Validate(pipeline, Profiles(), ["tdd"]);

        var error = Assert.Single(errors);
        Assert.Contains("{{nope}}", error);
        Assert.Contains("{{issue_title}}", error);
    }

    [Fact]
    public void An_unknown_skill_is_rejected()
    {
        var pipeline = Standard();
        pipeline.Phases[0].Skills = ["not-a-skill"];

        var errors = PipelineRules.Validate(pipeline, Profiles(), ["tdd"]);

        Assert.Contains("unknown skill 'not-a-skill'", Assert.Single(errors));
    }

    [Fact]
    public void A_pin_without_its_model_is_rejected()
    {
        var pipeline = Standard();
        pipeline.Phases[0].ProfileId = "go";

        var errors = PipelineRules.Validate(pipeline, Profiles(), ["tdd"]);

        Assert.Contains("pin is atomic", Assert.Single(errors));
    }

    [Fact]
    public void A_model_outside_the_palette_is_rejected()
    {
        var pipeline = Standard();
        pipeline.Phases[0].ProfileId = "go";
        pipeline.Phases[0].ModelId = "opencode-go/other";

        var errors = PipelineRules.Validate(pipeline, Profiles(), ["tdd"]);

        Assert.Contains("not in 'Go's palette", Assert.Single(errors));
    }

    [Fact]
    public void A_dangling_pin_is_rejected()
    {
        var pipeline = Standard();
        pipeline.Phases[0].ProfileId = "ghost";
        pipeline.Phases[0].ModelId = "opencode-go/other";

        var errors = PipelineRules.Validate(pipeline, Profiles(), ["tdd"]);

        Assert.Contains("dangling pin", Assert.Single(errors));
    }

    [Fact]
    public void The_sample_issue_has_a_value_for_every_placeholder()
    {
        foreach (var placeholder in Placeholders.All)
        {
            Assert.True(PipelineRules.SampleIssue.ContainsKey(placeholder), placeholder);
        }
    }
}
