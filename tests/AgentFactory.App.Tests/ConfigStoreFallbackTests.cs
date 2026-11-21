using AgentFactory.App.Fleet;

namespace AgentFactory.App.Tests;

/// <summary>The fleet fallback binding's store contract (ticket 044): set
/// with the same guards as the utility binding, persisted, referrer-guarded
/// on profile delete, and cleared when its profile's flavour is stripped as
/// unknown.</summary>
public sealed class ConfigStoreFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-factory-tests-" + Guid.NewGuid().ToString("N"));

    private ConfigStore NewStore() => new(_dir, "o/r");

    private static AgentProfile Profile(string id, string name, AgentFlavour flavour, params string[] models) => new()
    {
        Id = id,
        Name = name,
        Flavour = flavour,
        Models = [.. models],
    };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Unset_by_default()
    {
        var store = NewStore();
        Assert.Null(store.Fallback());
    }

    [Fact]
    public void Set_and_clear_round_trips_through_disk()
    {
        var store = NewStore();
        store.SaveProfile(Profile("go", "Go", AgentFlavour.OpencodeGo, "opencode-go/deepseek-v4.1-flash"));

        store.SetFallback(new UtilityBinding { ProfileId = "go", ModelId = "opencode-go/deepseek-v4.1-flash" });

        // A fresh store over the same document reads the binding back.
        Assert.Equal("go", NewStore().Fallback()?.ProfileId);

        store.SetFallback(null);
        Assert.Null(NewStore().Fallback());
    }

    [Fact]
    public void Unknown_profile_throws()
    {
        var store = NewStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.SetFallback(new UtilityBinding { ProfileId = "ghost", ModelId = "m" }));
    }

    [Fact]
    public void Model_outside_the_palette_throws()
    {
        var store = NewStore();
        store.SaveProfile(Profile("go", "Go", AgentFlavour.OpencodeGo, "opencode-go/deepseek-v4.1-flash"));

        Assert.Throws<InvalidOperationException>(() =>
            store.SetFallback(new UtilityBinding { ProfileId = "go", ModelId = "opencode-go/not-in-palette" }));
    }

    [Fact]
    public void Unknown_flavour_cannot_bind()
    {
        var store = NewStore();
        store.SaveProfile(Profile("go", "Go", AgentFlavour.OpencodeGo, "opencode-go/deepseek-v4.1-flash"));

        // Non-executable flavours no longer exist (only opencode-go remains) — an unknown
        // flavour name strips at load (covered below), so binding guard is now palette-only.
        Assert.Throws<InvalidOperationException>(() =>
            store.SetFallback(new UtilityBinding { ProfileId = "go", ModelId = "opencode-go/not-in-palette" }));
    }

    [Fact]
    public void Profile_without_model_throws_binding_is_atomic()
    {
        var store = NewStore();
        store.SaveProfile(Profile("go", "Go", AgentFlavour.OpencodeGo, "opencode-go/deepseek-v4.1-flash"));

        Assert.Throws<InvalidOperationException>(() =>
            store.SetFallback(new UtilityBinding { ProfileId = "go", ModelId = null }));
    }

    [Fact]
    public void Profile_delete_refused_while_fallback_references_it()
    {
        var store = NewStore();
        store.SaveProfile(Profile("go", "Go", AgentFlavour.OpencodeGo, "opencode-go/deepseek-v4.1-flash"));
        store.SetFallback(new UtilityBinding { ProfileId = "go", ModelId = "opencode-go/deepseek-v4.1-flash" });

        var (ok, error) = store.DeleteProfile("go");

        Assert.False(ok);
        Assert.Contains("fallback", error);
    }

    [Fact]
    public void Unknown_flavour_profile_strips_and_clears_the_fallback()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"),
            """
            {
              "DefaultPipelineId": "standard",
              "Pipelines": [],
              "Targets": [],
              "Orgs": [],
              "Profiles": [
                { "Id": "ghost", "Name": "Ghost", "Flavour": "ghost-flavour", "ApiKey": "", "Models": ["x"] }
              ],
              "Fallback": { "ProfileId": "ghost", "ModelId": "x" }
            }
            """);

        var store = NewStore();

        Assert.Empty(store.Profiles());
        Assert.Null(store.Fallback());
    }
}
