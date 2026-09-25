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

    /// <summary>
    /// The only namespaces of another module a module may name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list was wrong when first written, in a way worth recording. It permitted
    /// <c>{Root}.Modules.{module}</c> — the module root — on the stated grounds that it was "the facade
    /// interface itself". At the time, each reference module declared its facade, business <em>and</em> data
    /// layer interfaces in that one namespace, so the rule permitted a business class to inject another
    /// module's <c>IDataLayer</c> and the whole suite stayed green. The fix was structural: the interfaces now
    /// live in <c>.Facade</c>, <c>.Business</c> and <c>.Data</c>, and only <c>.Facade</c> is permitted.
    /// </para>
    /// <para>
    /// <c>.Managers</c> is permitted, and only in the shape this remark used to describe as hypothetical.
    /// 4A.7 allows the ServiceModel a facade returns to cross, and a namespace cannot express "only the
    /// ServiceModels" — so the pairing this rule was always documented to need is in force: the namespace is
    /// open here, and <see cref="No_module_names_another_modules_internal_manager_types"/> keeps the module's
    /// ViewModels, validators, query types, repository records and policies out by name.
    /// </para>
    /// <para>
    /// What made it real is Phase 7: Recipes now calls Measurement's facade to resolve units, so
    /// <c>MeasurementUnitServiceModel</c> genuinely crosses, and with it the enums reachable through its own
    /// surface (<c>MeasurementDimension</c>, <c>TemperatureScale</c>) — a ServiceModel a caller cannot name
    /// the members of is not a ServiceModel that crossed. The pure calculators cross on the same footing:
    /// they are stateless functions over those models with no state, no dependencies and no I/O, which is
    /// the opposite of the module-internal reach this rule exists to stop.
    /// </para>
    /// </remarks>
    private static readonly Func<string, string, bool>[] PermittedCrossModule =
    [
        // The facade interface, and nothing else in the module's own namespace tree.
        (used, module) => used == $"{Root}.Modules.{module}.Facade",

        // Entities, and only because a foreign key crosses: EF must name the principal entity type to
        // configure the relationship. No behaviour crosses with it.
        (used, module) => used == $"{Root}.Modules.{module}.Data.Entities",

        // Seed data composes across the reference modules by design; the seeder is one deployment-time unit.
        (used, module) => used == $"{Root}.Modules.{module}.Seeding",

        // The ServiceModels a facade returns, and the stateless calculators over them. Everything this rule
        // actually protects — repositories, data layers, business types, view models, validators, policies —
        // is kept out by name in No_module_names_another_modules_internal_manager_types, which is what makes
        // opening the namespace safe rather than a hole.
        (used, module) => used == $"{Root}.Modules.{module}.Managers",
    ];

    /// <summary>
    /// Type-name suffixes that identify a module's internal manager types — the ones 4A.7 says never cross.
    /// </summary>
    /// <remarks>
    /// <c>Policy</c> was added when the sixth module arrived. Every module now ships one — <c>AccountPolicy</c>,
    /// <c>DensityPolicy</c>, <c>MeasurementPolicy</c>, <c>VocabularyPolicy</c>, <c>WorkspacePolicy</c>,
    /// <c>RecipePolicy</c> — and they are exactly the "domain models and policies" 4A.7 says never cross.
    /// They are public static classes, so nothing but this list stops another module naming one.
    /// </remarks>
    private static readonly string[] InternalManagerSuffixes =
    [
        "ViewModel", "Validator", "QueryFactory", "Record", "Query", "Policy",
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
    /// The scan found the files it claims to be checking.
    /// </summary>
    /// <remarks>
    /// Without this, every rule in this file passes vacuously. All of them assert "no offenders", so a scan
    /// that returns nothing — wrong root, a path map under CI, a changed <c>using</c> format — reports success
    /// while checking nothing at all. These numbers are deliberately exact so the guard fails when the shape of
    /// the domain changes, rather than drifting quietly toward zero.
    /// </remarks>
    [Fact]
    public void The_scan_actually_sees_the_domain()
    {
        var files = DomainFiles();

        Assert.True(Directory.Exists(Path.Combine(DomainRoot(), "Modules")), "source scan cannot find the domain project");
        Assert.InRange(files.Count, 150, 400);
        Assert.Equal(6, files.Where(file => file.Module is not null).Select(file => file.Module).Distinct().Count());

        // The kernel genuinely imports module namespaces in its four exempted files; if this hits zero the
        // using-extraction has stopped working and the kernel rule below is no longer checking anything.
        Assert.Equal(
            SharedKernelExemptions.Length,
            files.Count(file => file.RelativePath.StartsWith("Managers/", StringComparison.Ordinal)
                && file.DomainUsings.Any(used => used.StartsWith($"{Root}.Modules.", StringComparison.Ordinal))));

        // And modules genuinely import each other, through the permitted namespaces.
        Assert.NotEmpty(files.Where(file => file.Module is not null)
            .SelectMany(file => file.DomainUsings.Where(used =>
                used.StartsWith($"{Root}.Modules.", StringComparison.Ordinal)
                && !used.StartsWith($"{Root}.Modules.{file.Module}", StringComparison.Ordinal))));
    }

    /// <summary>
    /// Every exemption still earns its place.
    /// </summary>
    /// <remarks>
    /// An exemption whose file no longer imports a module is a permanently widened hole that nothing else
    /// would report.
    /// </remarks>
    [Fact]
    public void No_shared_kernel_exemption_is_stale()
    {
        var files = DomainFiles();

        Assert.All(SharedKernelExemptions, path =>
        {
            var file = files.SingleOrDefault(candidate => candidate.RelativePath == path);

            Assert.True(file is not null, $"exempted file no longer exists: {path}");
            Assert.Contains(file!.DomainUsings, used => used.StartsWith($"{Root}.Modules.", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// A module never names another module's internal manager types, however the reference is written.
    /// </summary>
    /// <remarks>
    /// The <c>using</c>-based rules above cannot see a fully-qualified reference, an alias, a
    /// <c>global using</c>, or a <c>using static</c>. This scans the file text instead, so the escape hatches
    /// close. It is the check that makes 4A.7's "never another module's ViewModels, validators, or domain
    /// models" enforceable rather than merely written down.
    /// </remarks>
    [Fact]
    public void No_module_names_another_modules_internal_manager_types()
    {
        var files = DomainFiles().Where(file => file.Module is not null).ToList();

        var internalTypes = files
            .Where(file => file.RelativePath.Contains("/Managers/", StringComparison.Ordinal))
            .SelectMany(file => TypeNames(file.Text).Select(name => (file.Module, Name: name)))
            .Where(entry => InternalManagerSuffixes.Any(suffix => entry.Name.EndsWith(suffix, StringComparison.Ordinal)))
            .ToList();

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var body = CodeOnly(file.Text);

            foreach (var (owner, name) in internalTypes.Where(entry => entry.Module != file.Module))
            {
                // Word-boundary match, so "IngredientQuery" does not fire on "IngredientQueryViewModel".
                if (System.Text.RegularExpressions.Regex.IsMatch(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"))
                {
                    offenders.Add($"{file.RelativePath} names {owner}'s {name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a module named another module's internal manager type:\n" + string.Join("\n", offenders.Distinct()));
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

    /// <summary>
    /// Every module repeats the same internal shape, so one vertical teaches all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 4A.1 fixed that shape as four areas — Facade, Business, Data, Managers — plus the module's own
    /// composition root. An earlier version of this test asserted only two of them, and omitted precisely the
    /// two that three modules were missing: it passed 5 of 5 while 3 of 5 diverged. Asserting the shape you
    /// intend rather than the shape you happen to have is the whole point of a test like this.
    /// </para>
    /// <para>
    /// The module list is discovered, not listed, so a sixth module cannot arrive unverified.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Modules))]
    public void Every_module_repeats_the_same_internal_shape(string module)
    {
        var moduleRoot = Path.Combine(DomainRoot(), "Modules", module);

        Assert.All(
            (string[])["Facade", "Business", "Data", "Managers"],
            area => Assert.True(Directory.Exists(Path.Combine(moduleRoot, area)), $"{module} has no {area} area"));

        Assert.True(
            Directory.EnumerateFiles(moduleRoot, "*ServiceCollectionExtensions.cs").Any(),
            $"{module} has no composition root");
    }

    public static TheoryData<string> Modules()
    {
        var discovered = Directory.EnumerateDirectories(Path.Combine(DomainRoot(), "Modules"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();

        Assert.NotEmpty(discovered);

        return [.. discovered];
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

    private static IReadOnlyList<DomainFile> DomainFiles()
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

    /// <summary>Public type names declared in a file, for the text-based boundary rule.</summary>
    private static IEnumerable<string> TypeNames(string text) =>
        System.Text.RegularExpressions.Regex
            .Matches(text, @"^\s*public (?:sealed |abstract |static |partial )*(?:record|class|interface|enum)\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value);

    /// <summary>
    /// The file's executable text: no using block, no comments.
    /// </summary>
    /// <remarks>
    /// Comments are stripped because this rule is about <em>code</em> coupling. An <c>&lt;inheritdoc&gt;</c>
    /// pointing at a sibling module's validator is a documentation smell worth fixing on its own terms — it
    /// rots the moment either type moves — but it creates no dependency, and counting it here would train
    /// people to read a boundary failure as noise.
    /// </remarks>
    private static string CodeOnly(string text) =>
        string.Join(
            '\n',
            text.Split('\n')
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("using ", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("*", StringComparison.Ordinal)));

    private static string DomainRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "CreatorPantry.Domain"));

    private sealed record DomainFile(
        string RelativePath,
        string? Module,
        IReadOnlyList<string> DomainUsings,
        string Text);
}
