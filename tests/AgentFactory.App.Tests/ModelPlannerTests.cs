using AgentFactory.App.Fleet;

namespace AgentFactory.App.Tests;

/// <summary>The model planner's decision table (ticket 044): pin vs fleet
/// fallback vs engine defaults, decided purely from probe verdicts taken
/// before planning.</summary>
public sealed class ModelPlannerTests
{
    private static readonly AgentProfile Pin = new()
    {
        Id = "go---pin", Name = "Go pin", Flavour = AgentFlavour.OpencodeGo,
        Models = ["opencode-go/deepseek-v4.1-flash"],
    };
    private static readonly AgentProfile Go = new()
    {
        Id = "go---open-weights", Name = "Go — open weights", Flavour = AgentFlavour.OpencodeGo,
        Models = ["opencode-go/gpt-5.6-luna"],
    };

    private static readonly string PinModel = "opencode-go/deepseek-v4.1-flash";
    private static readonly string GoModel = "opencode-go/gpt-5.6-luna";

    private static ModelHealthProbe.Verdict Alive(string detail = "probe ok") => new(true, detail);
    private static ModelHealthProbe.Verdict Dead(string detail = "probe exceeded 90s") => new(false, detail);

    private static (AgentProfile Profile, string ModelId) Fallback(AgentProfile p, string m) => (p, m);

    // --- healthy pin: the ordinary path ---

    [Fact]
    public void Healthy_pin_runs_as_pinned()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict> { [PinModel] = Alive() };

        var plan = ModelPlanner.Plan(Pin, PinModel, Fallback(Go, GoModel), health);

        Assert.Null(plan.FailReason);
        Assert.Same(Pin, plan.Profile);
        Assert.Equal(PinModel, plan.ModelId);
        Assert.StartsWith("pinned", plan.Note);
        Assert.Contains(PinModel, plan.Note);
    }

    [Fact]
    public void Healthy_pin_ignores_a_healthy_fallback()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict>
        {
            [PinModel] = Alive(), [GoModel] = Alive(),
        };

        var plan = ModelPlanner.Plan(Pin, PinModel, Fallback(Go, GoModel), health);

        Assert.Same(Pin, plan.Profile);
        Assert.Equal(PinModel, plan.ModelId);
        Assert.StartsWith("pinned", plan.Note);
        Assert.DoesNotContain("FALLBACK", plan.Note);
    }

    // --- dead pin: substitute or fail fast ---

    [Fact]
    public void Dead_pin_with_healthy_fallback_substitutes_loudly()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict>
        {
            [PinModel] = Dead("probe exceeded 90s and was aborted"), [GoModel] = Alive(),
        };

        var plan = ModelPlanner.Plan(Pin, PinModel, Fallback(Go, GoModel), health);

        Assert.Null(plan.FailReason);
        Assert.Same(Go, plan.Profile);
        Assert.Equal(GoModel, plan.ModelId);
        Assert.Contains("PIN FALLBACK", plan.Note);
        Assert.Contains(PinModel, plan.Note);
        Assert.Contains("probe exceeded 90s", plan.Note);
    }

    [Fact]
    public void Dead_pin_without_fallback_fails_fast()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict> { [PinModel] = Dead() };

        var plan = ModelPlanner.Plan(Pin, PinModel, fallback: null, health);

        Assert.NotNull(plan.FailReason);
        Assert.Contains(PinModel, plan.FailReason);
        Assert.Contains("no fallback binding", plan.FailReason);
    }

    [Fact]
    public void Dead_pin_with_dead_fallback_fails_fast_naming_both()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict>
        {
            [PinModel] = Dead("hung"), [GoModel] = Dead("empty reply"),
        };

        var plan = ModelPlanner.Plan(Pin, PinModel, Fallback(Go, GoModel), health);

        Assert.NotNull(plan.FailReason);
        Assert.Contains(PinModel, plan.FailReason);
        Assert.Contains("hung", plan.FailReason);
        Assert.Contains(GoModel, plan.FailReason);
        Assert.Contains("empty reply", plan.FailReason);
    }

    // --- unpinned: deterministic default or honest refusal ---

    [Fact]
    public void Unpinned_without_fallback_stays_engine_default()
    {
        var plan = ModelPlanner.Plan(null, null, fallback: null, health: new Dictionary<string, ModelHealthProbe.Verdict>());

        Assert.Null(plan.FailReason);
        Assert.Null(plan.Profile);
        Assert.Null(plan.ModelId);
        Assert.Equal("", plan.Note);
    }

    [Fact]
    public void Unpinned_with_healthy_fallback_runs_it_deterministically()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict> { [GoModel] = Alive() };

        var plan = ModelPlanner.Plan(null, null, Fallback(Go, GoModel), health);

        Assert.Null(plan.FailReason);
        Assert.Same(Go, plan.Profile);
        Assert.Equal(GoModel, plan.ModelId);
        Assert.Contains("unpinned", plan.Note);
        Assert.Contains(GoModel, plan.Note);
    }

    [Fact]
    public void Unpinned_with_dead_fallback_refuses_instead_of_roulette()
    {
        var health = new Dictionary<string, ModelHealthProbe.Verdict> { [GoModel] = Dead("upstream dead") };

        var plan = ModelPlanner.Plan(null, null, Fallback(Go, GoModel), health);

        Assert.NotNull(plan.FailReason);
        Assert.Contains(GoModel, plan.FailReason);
        Assert.Contains("upstream dead", plan.FailReason);
    }
}
