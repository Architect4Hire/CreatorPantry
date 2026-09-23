using System.Runtime.CompilerServices;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// Enforces the module boundaries Phase 4A established, by reading the domain's source rather than its
/// compiled metadata.
/// </summary>
/// <remarks>
/// <para>
/// Source, not reflection, because the rule being enforced is about <em>dependency direction between
/// folders</em> and a <c>using</c> directive states that directly. Reflection would see through type aliasing
/// and generic instantiation but would also miss the thing that matters most here — a file that simply should
/// not know another module exists.
/// </para>
/// <para>
/// What crosses a module boundary is deliberately narrow: a facade interface, the ServiceModels a facade
/// returns, and entity types, which cross only because a foreign key does and EF must name the principal.
/// Repositories, data layers, business types, view models and validators never cross.
/// </para>
/// </remarks>
public sealed class ModuleBoundaryTests
{
    private const string Root = "CreatorPantry.Domain";

    /// <summary>
    /// The shared kernel may not depend on a module. Two exceptions, both structural rather than convenient.
    /// </summary>
    /// <remarks>
    /// <see cref="CreatorPantryDbContext"/> names every module's entities because there is one DbContext and
    /// one migration history — it is the composition point for persistence, as <c>Program.cs</c> is for DI.
    /// The other two are cross-cutting tables whose own foreign keys point at Tenancy; see the finding in the
    /// Phase 4A report.
    /// </remarks>
    private static readonly string[] SharedKernelExemptions =
    [
        "Managers/Persistence/CreatorPantryDbContext.cs",
        "Managers/Persistence/WorkspaceOwnershipConvention.cs",
        "Managers/Audit/AuditLogConfiguration.cs",
        "Managers/Reference/ReferenceDataSeeder.cs",
    ];

    /// <summary>Namespaces of another module that a module may legitimately name.</summary>
    private static readonly Func<string, string, bool>[] PermittedCrossModule =
    [
        // The facade interface itself: Modules.Vocabulary, not a sub-namespace.
        (used, module) => used == $"{Root}.Modules.{module}",

        // The ServiceModels a facade returns, and the shared vocabulary its entities are described with.
        (used, module) => used == $"{Root}.Modules.{module}.Managers",

        // Entities, and only because a foreign key crosses: EF must name the principal entity type to
        // configure the relationship. No behaviour crosses with it.
        (used, module) => used == $"{Root}.Modules.{module}.Data.Entities",

        // Seed data composes across the reference modules by design; the seeder is one deployment-time unit.
        (used, module) => used == $"{Root}.Modules.{module}.Seeding",
    ];

    [Fact]
    public void The_shared_kernel_does_not_depend_on_any_module()
    {
        var offenders = DomainFiles()
            .Where(file => file.RelativePath.StartsWith("Managers/", StringComparison.Ordinal))
            .Where(file => !SharedKernelExemptions.Contains(file.RelativePath))
            .SelectMany(file => file.DomainUsings
                .Where(used => used.StartsWith($"{Root}.Modules.", StringComparison.Ordinal))
                .Select(used => $"{file.RelativePath} -> {used}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "shared kernel code reached into a module:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// A module may not reach past another module's facade — no repositories, no data layers, no business
    /// types, no validators.
    /// </summary>
    [Fact]
    public void A_module_never_reaches_past_another_modules_facade()
    {
        var offenders = new List<string>();

        foreach (var file in DomainFiles().Where(file => file.Module is not null))
        {
            foreach (var used in file.DomainUsings.Where(used => used.StartsWith($"{Root}.Modules.", StringComparison.Ordinal)))
            {
                var other = used[$"{Root}.Modules.".Length..].Split('.')[0];
                if (other == file.Module)
                {
                    continue;
                }

                if (!PermittedCrossModule.Any(permitted => permitted(used, other)))
                {
                    offenders.Add($"{file.RelativePath} -> {used}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a module reached past another module's facade:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The layer-first trees Phase 4A dissolved must not grow back. A second home for the same kind of type is
    /// how the structure erodes: one <c>Models/</c> folder beside the modules and the next feature has a
    /// choice about where its view model goes.
    /// </summary>
    [Theory]
    [InlineData("Models")]
    [InlineData("ViewModels")]
    [InlineData("ServiceModels")]
    [InlineData("DomainModels")]
    [InlineData("Validation")]
    [InlineData("Repositories")]
    [InlineData("Facade")]
    [InlineData("Business")]
    public void No_layer_first_folder_survives_at_the_domain_root(string folder)
    {
        var path = Path.Combine(DomainRoot(), folder);

        Assert.False(Directory.Exists(path), $"{folder}/ still exists at the domain root");
    }

    /// <summary>Every module repeats the same internal shape, so one vertical teaches all of them.</summary>
    [Theory]
    [InlineData("Tenancy")]
    [InlineData("Auth")]
    [InlineData("Measurement")]
    [InlineData("Vocabulary")]
    [InlineData("Ingredients")]
    public void Every_module_has_a_managers_area_a_data_area_and_a_composition_root(string module)
    {
        var moduleRoot = Path.Combine(DomainRoot(), "Modules", module);

        Assert.True(Directory.Exists(Path.Combine(moduleRoot, "Managers")), $"{module} has no Managers area");
        Assert.True(Directory.Exists(Path.Combine(moduleRoot, "Data")), $"{module} has no Data area");
        Assert.True(
            Directory.EnumerateFiles(moduleRoot, "*ServiceCollectionExtensions.cs").Any(),
            $"{module} has no composition root");
    }

    /// <summary>
    /// Within a module, an EF configuration lives beside the entity it configures. That is what lets the
    /// boundary rules above be stated by path at all.
    /// </summary>
    /// <remarks>
    /// Scoped to modules deliberately. The shared kernel's infrastructure tables — audit, outbox, idempotency
    /// — keep their configuration beside their entity in one flat folder per concern, because they have no
    /// facade or business layer to separate it from. Imposing a <c>Data/Configurations</c> tree on a
    /// three-file concern would be shape for its own sake.
    /// </remarks>
    [Fact]
    public void Every_module_entity_configuration_lives_beside_the_entity_it_configures()
    {
        var misplaced = DomainFiles()
            .Where(file => file.Module is not null)
            .Where(file => file.RelativePath.EndsWith("Configuration.cs", StringComparison.Ordinal))
            .Where(file => !file.RelativePath.Contains("/Data/Configurations/", StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToList();

        Assert.True(
            misplaced.Count == 0,
            "EF configurations outside a Data/Configurations folder:\n" + string.Join("\n", misplaced));
    }

    private static IEnumerable<DomainFile> DomainFiles()
    {
        var root = DomainRoot();

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var segments = relative.Split('/');

                return new DomainFile(
                    relative,
                    segments is ["Modules", var module, ..] ? module : null,
                    [.. File.ReadAllLines(path)
                        .Where(line => line.StartsWith($"using {Root}.", StringComparison.Ordinal))
                        .Select(line => line["using ".Length..].TrimEnd(';'))],
                    File.ReadAllText(path));
            })
            .ToList();
    }

    private static string DomainRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "CreatorPantry.Domain"));

    private sealed record DomainFile(
        string RelativePath,
        string? Module,
        IReadOnlyList<string> DomainUsings,
        string Text);
}
