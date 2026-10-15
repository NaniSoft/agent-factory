namespace AgentFactory.Tests.Failures;

using System.Xml.Linq;
using AgentFactory.Tests.Boundary;

/// <summary>
/// The dependency rule, read where the compiler reads it.
/// <para>
/// <c>docs/architecture.md</c> says the graph is exactly four edges and that
/// <c>Domain</c> points nowhere, and it says so about <c>csproj</c> files. So these are
/// assertions over the project files themselves rather than over IL or over namespaces,
/// and they work today, before a single line has moved — which is the only moment at
/// which they can be written without guessing what the split will have produced.
/// </para>
/// <para>
/// The distinction they draw is the one the whole ticket turns on. **A project reference
/// makes an outward dependency impossible to compile; it says nothing about a caller, a
/// construction site, a constructor signature or a closed vocabulary.** Eighteen pins in
/// <see cref="PolicyTests"/> are all of the second kind and all of them outlived the
/// assembly. None of them is here, because none of them is about a reference graph.
/// </para>
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void The_project_graph_is_exactly_the_four_edges_and_no_others()
    {
        // The rule as the architecture document draws it: Web → Infrastructure →
        // Application → Domain, and Domain → nothing. Written out rather than derived from
        // the files, because a graph read back from the files is a graph that agrees with
        // itself — the assertion has to be a list somebody decided on, or a fifth edge is
        // a new fact rather than a failure.
        Assert.Equal(
            [
                "AgentFactory.Application -> AgentFactory.Domain",
                "AgentFactory.Domain -> (nothing)",
                "AgentFactory.Infrastructure -> AgentFactory.Application",
                "AgentFactory.Web -> AgentFactory.Infrastructure",
            ],
            TheFactoryProjects()
                .Select(project => ProjectReferences(project.Value).Count == 0
                    ? $"{project.Key} -> (nothing)"
                    : $"{project.Key} -> {string.Join(", ", ProjectReferences(project.Value))}")
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    [Fact]
    public void The_domain_points_nowhere_at_all()
    {
        // "Not to the BCL's abstractions, not to anything" — and it is stated three times
        // over because there are three ways a dependency arrives in a project file, and a
        // check that watched only one of them would be a check about one spelling rather
        // than about the property. A project reference, a package reference and a
        // framework reference are three different doors into an assembly.
        //
        // This is also the one edge in the graph there is no way to add later without a
        // slice deciding to: `Domain` is where the factory's own vocabulary is supposed to
        // be findable by pointing at a type and knowing it is the factory's own, and a
        // type from a package is not that.
        var domain = TheFactoryProjects()["AgentFactory.Domain"];

        Assert.Empty(ProjectReferences(domain));
        Assert.Empty(Names(domain, "PackageReference"));
        Assert.Empty(Names(domain, "FrameworkReference"));
    }

    [Fact]
    public void Only_web_declares_the_aspNet_sdk_and_the_aspNet_framework_reference()
    {
        // **"The three libraries do not use ASP.NET types" is compiler-enforced today, and
        // this is the pin that keeps it true on purpose rather than by accident.**
        //
        // It was worth checking rather than assuming, because the Web SDK's
        // `Microsoft.AspNetCore.App` framework reference is transitive, and the reasonable
        // fear was that it flowed *inward* and handed `Domain` the whole of ASP.NET. It
        // does not: transitivity flows from a project to the projects that reference it,
        // and nothing references `Web`. Measured, not assumed — a probe file naming
        // `IHttpContext` fails to compile in all three libraries — and the rule below is
        // what stops it becoming true.
        foreach (var (name, project) in TheFactoryProjects())
        {
            var isWeb = name == "AgentFactory.Web";

            // The SDK first, because the Web SDK adds the framework reference *implicitly*.
            // A library switched to `Microsoft.NET.Sdk.Web` would gain ASP.NET with nothing
            // in its file mentioning it, so a scan of `FrameworkReference` items alone
            // would read as clean and the compiler would disagree.
            Assert.Equal(isWeb ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk", Sdk(project));

            // And then the two doors that carry ASP.NET in without the Web SDK:
            // `Microsoft.AspNetCore.App` as a framework reference, and any ASP.NET package
            // — `Microsoft.AspNetCore.App.Ref` being the one that arrives by accident, from
            // a package reference meant for something else.
            if (!isWeb)
            {
                Assert.DoesNotContain(
                    Names(project, "FrameworkReference"),
                    reference => reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));

                Assert.DoesNotContain(
                    Names(project, "PackageReference"),
                    reference => reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void The_three_libraries_compile_against_no_aspNet_assembly_and_web_compiles_against_many()
    {
        // The other half of the last test, and the half that is a *fact about the compiled
        // output* rather than about what a file says it intends: read off the assemblies
        // themselves, which is what the compiler actually resolved.
        //
        // Both halves are here on purpose. The csproj pin is the intent — it is what a
        // slice trips, loudly, before anything is written. The metadata pin is the
        // consequence, and it is the one that would still hold if a package pulled ASP.NET
        // in from a direction the csproj scan was not looking at.
        //
        // **What this half is not, today, is the guard.** All three libraries are empty,
        // so the negative assertion is satisfied by there being nothing in them at all —
        // handing `Application` a `FrameworkReference` does not fail it, which is measured
        // rather than assumed, and is exactly why the csproj pin beside it carries the
        // rule while this one carries the consequence. What this half does today is
        // answer a question the csproj cannot: does the compiler's own output agree.
        foreach (var library in TheFactory.Assemblies.Where(assembly => assembly.GetName().Name != "AgentFactory.Web"))
        {
            Assert.DoesNotContain(
                library.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty),
                reference => reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        }

        // **The positive control, and the reason this is a check rather than a fact that
        // happens to hold.** `AgentFactory.Web` *does* reference ASP.NET — `Program.cs`,
        // `FactoryApp.cs` and the `Api/` projections are built on it — and the assertion
        // that would make this whole test vacuous is that the reader finds nothing
        // anywhere. So the same read, over the one assembly that must have the references,
        // has to find them. A check that never fires looks exactly like a check that is
        // not there; this one is proved to fire.
        Assert.Contains(
            TheFactory.Named("AgentFactory.Web").GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty),
            reference => reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scan_reads_every_assembly_the_factory_is_made_of()
    {
        // The one thing about the eighteen pins that a set of four names does not by
        // itself establish: that the walk reads all four. Every line of the factory is
        // still in `AgentFactory.Web`, so a scan handed four assemblies and reading only
        // the first would pass every pin in `PolicyTests` today — correctly, silently, and
        // then be wrong for the rest of the restructure.
        //
        // Three claims, and each fails differently. The names pin the *set*. The count
        // pins the *walk*. The files-beside-the-tests pin the *boundary of the question*:
        // a fifth factory assembly appearing in the output is somebody's decision that
        // this file has to be told about, rather than a file nothing scans.
        Assert.Equal(
            ["AgentFactory.Application", "AgentFactory.Domain", "AgentFactory.Infrastructure", "AgentFactory.Web"],
            TheFactory.Assemblies.Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToList());

        Assert.Equal(
            TheFactory.Assemblies.Sum(assembly => assembly.GetTypes().Length),
            PolicyTests.TypesIn(TheFactory.Assemblies).Count());

        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "AgentFactory.*.dll"))
        {
            Assert.Contains(
                Path.GetFileNameWithoutExtension(file),
                TheFactory.Assemblies.Select(assembly => assembly.GetName().Name));
        }
    }

    /// <summary>
    /// The factory's own four project files, by project name, read from the repository
    /// root. Walked up to rather than hard-coded, so a checkout at any depth finds them,
    /// and a path that is *not* found throws rather than scanning nothing — a check that
    /// quietly reads nothing is the failure this whole file exists to prevent.
    /// </summary>
    /// <remarks>
    /// One directory under <c>src/</c> per project, with the project file inside it,
    /// which is where this repository puts them. Enumerated per directory rather than
    /// with a recursive glob so that a file under <c>bin/</c> or <c>obj/</c> cannot be
    /// read in place of the real one.
    /// <para>
    /// <c>agent-factory.tests</c> is deliberately not among them. Its own project
    /// references are not part of the factory's layering, and
    /// <c>docs/architecture.md</c>'s Testing section asks for unit tests over `Domain`
    /// and `Application`, which will add edges *out of* the test project. Those are a
    /// decision about the tests; pinning them here would forbid the thing the same
    /// document asks for.
    /// </para>
    /// </remarks>
    private static SortedDictionary<string, XDocument> TheFactoryProjects()
    {
        var projects = new SortedDictionary<string, XDocument>(StringComparer.Ordinal);

        foreach (var directory in Directory.GetDirectories(SourceDirectory()))
        {
            var name = Path.GetFileName(directory);
            if (name == "agent-factory.tests")
            {
                continue;
            }

            var file = Path.Combine(directory, $"{name}.csproj");
            if (!File.Exists(file))
            {
                continue;
            }

            using var opened = File.OpenRead(file);
            projects[name] = XDocument.Load(opened);
        }

        Assert.Equal(
            ["AgentFactory.Application", "AgentFactory.Domain", "AgentFactory.Infrastructure", "AgentFactory.Web"],
            projects.Keys.ToList());

        return projects;
    }

    /// <summary>
    /// The project's references, as the project names rather than as the paths written, so
    /// that a path written differently on two platforms is not a failing test.
    /// </summary>
    private static IReadOnlyList<string> ProjectReferences(XDocument project) => Names(project, "ProjectReference")
        .Select(reference => Path.GetFileNameWithoutExtension(reference))
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>The <c>Include</c> of every element with this name, anywhere in the file.</summary>
    private static IReadOnlyList<string> Names(XDocument project, string element) => project
        .Descendants(element)
        .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty)
        .ToList();

    private static string Sdk(XDocument project) =>
        (string?)project.Root?.Attribute("Sdk") ?? string.Empty;

    /// <summary>
    /// The <c>src/</c> directory, found by walking up from the test binaries. How deep the
    /// binaries sit is a build-output detail, so the walk is what keeps this independent
    /// of it.
    /// </summary>
    private static string SourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(source))
            {
                return source;
            }
        }

        throw new DirectoryNotFoundException($"no src directory above {AppContext.BaseDirectory}");
    }
}