namespace AgentFactory.Tests.Boundary;

using System.Reflection;

/// <summary>
/// The four assemblies the factory is made of, named in one place.
/// <para>
/// It exists because every structural claim in this suite is a claim about **the whole
/// factory** rather than about the project a line of code happens to sit in today. That
/// was free while the factory was one assembly. It stops being free the moment a slice
/// moves code, because a scan pointed at <c>typeof(FactoryApp).Assembly</c> then stops
/// seeing whatever moved and its expected list shortens without anything failing — which
/// is the failure mode this whole layer exists to prevent, because "no second caller" and
/// "this caller was not seen" are indistinguishable at the assertion.
/// </para>
/// <para>
/// So the scan set is the four, by name, and the name is pinned by
/// <c>ArchitectureTests.The_scan_reads_every_assembly_the_factory_is_made_of</c>: a
/// fifth project, or a set quietly narrowed back to one assembly, is a failing test
/// rather than a weaker scan.
/// </para>
/// </summary>
public static class TheFactory
{
    /// <summary>
    /// The four, alphabetically, and written out rather than discovered from the
    /// directory beside the test binaries — because a discovery that found a fifth
    /// assembly would scan it, and the point of the list is that a fifth assembly is
    /// somebody's deliberate decision rather than something a scan quietly absorbed.
    /// </summary>
    private static readonly string[] Names =
    [
        "AgentFactory.Application",
        "AgentFactory.Domain",
        "AgentFactory.Infrastructure",
        "AgentFactory.Web",
    ];

    private static readonly Lazy<Assembly[]> Loaded = new(() => [.. Names.Select(One)]);

    /// <summary>
    /// The four assemblies, ordered by name. Cached because every structural pin in the
    /// suite asks for them and loading by name four times a pin is a cost paid for
    /// nothing.
    /// </summary>
    public static Assembly[] Assemblies => Loaded.Value;

    /// <summary>
    /// The one assembly with this name. Written as a lookup rather than as four static
    /// properties so that a name a caller asks for and a name this file lists cannot
    /// drift apart without the lookup throwing.
    /// </summary>
    public static Assembly Named(string name) =>
        Assemblies.Single(assembly => assembly.GetName().Name == name);

    /// <summary>
    /// The assembly with this name. `Web` is taken from its type rather than asked for,
    /// because the tests name its types and it is therefore certainly loaded already;
    /// the other three are resolved **by name**, which is the only way to reach a project
    /// nothing in the suite can name a type from. While the slices are unmoved the three
    /// libraries declare no type at all, and <c>typeof</c> cannot name what does not
    /// exist yet — so the scan set has to be able to say "this assembly, by name" for an
    /// assembly that has no code in it.
    /// </summary>
    private static Assembly One(string name) =>
        name == typeof(FactoryApp).Assembly.GetName().Name
            ? typeof(FactoryApp).Assembly
            : Assembly.Load(new AssemblyName(name));
}