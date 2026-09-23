using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CreatorPantry.Tests.Reference.ReferenceConstraintAssertions;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Exercises the EF configuration for <see cref="Cuisine"/>, <see cref="Course"/>,
/// <see cref="CookingTechnique"/>, <see cref="EquipmentType"/>, and their aliases against an in-memory
/// SQLite database, the same pattern <c>IngredientConstraintTests</c> uses. This verifies the stable keys
/// and alias rules the <c>AddRecipeVocabularies</c> migration also creates, without applying it anywhere.
/// </summary>
/// <remarks>
/// Two things under test are easy to miss. First, that each vocabulary really does get its own table
/// despite sharing a base class — a shared table with a discriminator would silently make one code unique
/// across all four vocabularies, so <c>grill</c> the technique and <c>grill</c> the equipment could not
/// coexist. Second, that <see cref="CookingTechnique.RequiresSafetyCaution"/> reads in one direction only.
/// </remarks>
public sealed class RecipeVocabularyConstraintTests : IDisposable
{
    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    // --- Global reference data, not workspace-owned ------------------------------------------------------

    [Fact]
    public void Vocabulary_entities_carry_no_workspace_id()
    {
        using var scope = _services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        foreach (var type in new[]
            {
                typeof(Cuisine), typeof(CuisineAlias), typeof(Course), typeof(CourseAlias),
                typeof(CookingTechnique), typeof(CookingTechniqueAlias),
                typeof(EquipmentType), typeof(EquipmentTypeAlias),
            })
        {
            Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(type));
            Assert.Null(model.FindEntityType(type)!.FindProperty(nameof(IWorkspaceOwned.WorkspaceId)));
        }
    }

    [Fact]
    public async Task Vocabularies_are_readable_without_a_resolved_workspace()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Cuisines.Add(NewEntry<Cuisine>("tex-mex", "Tex-Mex"));
        db.Courses.Add(NewEntry<Course>("main-course", "Main Course"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("tex-mex", Assert.Single(await db.Cuisines.ToListAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal("main-course", Assert.Single(await db.Courses.ToListAsync(TestContext.Current.CancellationToken)).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- One table per vocabulary -----------------------------------------------------------------------

    /// <summary>
    /// The shared <see cref="ControlledVocabulary"/> base is a base <em>class</em>, not a mapped entity. If
    /// EF ever picked it up as one, these four would collapse into a single table with a discriminator and
    /// their codes would start competing with each other.
    /// </summary>
    [Theory]
    [InlineData(typeof(Cuisine), "Cuisines")]
    [InlineData(typeof(Course), "Courses")]
    [InlineData(typeof(CookingTechnique), "CookingTechniques")]
    [InlineData(typeof(EquipmentType), "EquipmentTypes")]
    public void Each_vocabulary_maps_to_its_own_table(Type vocabulary, string tableName)
    {
        using var scope = _services.CreateScope();
        var entityType = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model.FindEntityType(vocabulary)!;

        Assert.Equal(tableName, entityType.GetTableName());
        Assert.Null(entityType.BaseType);
        Assert.Null(entityType.FindDiscriminatorProperty());
    }

    /// <summary>
    /// The proof that matters to a reader of the database rather than of the model: the same code in two
    /// vocabularies is two independent facts, because they live in two tables.
    /// </summary>
    [Fact]
    public async Task The_same_code_in_two_vocabularies_is_two_independent_entries()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.CookingTechniques.Add(NewEntry<CookingTechnique>("grill", "Grill"));
        db.EquipmentTypes.Add(NewEntry<EquipmentType>("grill", "Grill"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await db.CookingTechniques.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.EquipmentTypes.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>An alias is likewise scoped to its own vocabulary.</summary>
    [Fact]
    public async Task The_same_alias_in_two_vocabularies_is_two_independent_entries()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var technique = NewEntry<CookingTechnique>("grill", "Grill");
        var equipment = NewEntry<EquipmentType>("outdoor-grill", "Outdoor Grill");
        db.CookingTechniques.Add(technique);
        db.EquipmentTypes.Add(equipment);

        db.CookingTechniqueAliases.Add(NewAlias<CookingTechniqueAlias>(technique.Id, "barbecue"));
        db.EquipmentTypeAliases.Add(NewAlias<EquipmentTypeAlias>(equipment.Id, "barbecue"));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await db.CookingTechniqueAliases.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.EquipmentTypeAliases.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- Stable keys ------------------------------------------------------------------------------------

    /// <remarks>
    /// Exact duplicates only. Whether <c>Italian</c> collides with <c>italian</c> depends on collation —
    /// SQL Server's default is case-insensitive, SQLite's index is not — so codes are held lowercase by
    /// <see cref="VocabularyPolicy.CodePattern"/> rather than by asserting provider-specific behavior here.
    /// </remarks>
    [Fact]
    public Task Two_cuisines_cannot_share_a_code() => AssertCodeIsUniqueAsync<Cuisine>("italian", "Cuisines.Code");

    [Fact]
    public Task Two_courses_cannot_share_a_code() => AssertCodeIsUniqueAsync<Course>("main-course", "Courses.Code");

    [Fact]
    public Task Two_techniques_cannot_share_a_code() => AssertCodeIsUniqueAsync<CookingTechnique>("braise", "CookingTechniques.Code");

    [Fact]
    public Task Two_equipment_types_cannot_share_a_code() => AssertCodeIsUniqueAsync<EquipmentType>("dutch-oven", "EquipmentTypes.Code");

    /// <summary>
    /// The shape that makes a code stable: lowercase alphanumeric segments joined by single hyphens, and
    /// nothing that varies with how someone typed it. A code is permanent because seed data and saved
    /// filters resolve by it, so the rule is checked here rather than discovered when a seed run diverges.
    /// </summary>
    [Theory]
    [InlineData("italian", true)]
    [InlineData("tex-mex", true)]
    [InlineData("main-course", true)]
    [InlineData("sheet-pan-9x13", true)]
    [InlineData("Italian", false)]
    [InlineData("tex mex", false)]
    [InlineData("tex_mex", false)]
    [InlineData("-italian", false)]
    [InlineData("italian-", false)]
    [InlineData("tex--mex", false)]
    [InlineData("café", false)]
    [InlineData("", false)]
    public void Code_pattern_admits_only_stable_keys(string code, bool expected) =>
        Assert.Equal(expected, Regex.IsMatch(code, VocabularyPolicy.CodePattern));

    /// <summary>
    /// A retired entry keeps its key and stays readable. Recipes that already reference it must keep
    /// resolving; deactivating only removes it from the pickers.
    /// </summary>
    [Fact]
    public async Task A_deactivated_entry_is_still_readable_by_its_code()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var cuisine = NewEntry<Cuisine>("fusion", "Fusion");
        db.Cuisines.Add(cuisine);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        cuisine.IsActive = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var retired = await db.Cuisines.SingleAsync(entry => entry.Code == "fusion", TestContext.Current.CancellationToken);
        Assert.False(retired.IsActive);
        Assert.Empty(await db.Cuisines.Where(entry => entry.IsActive).ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- Alias resolution -------------------------------------------------------------------------------

    [Fact]
    public Task One_cuisine_alias_cannot_resolve_to_two_cuisines() =>
        AssertAliasIsUnambiguousAsync<Cuisine, CuisineAlias>("italian", "sicilian", "Siciliano", "siciliano", "CuisineAliases.NormalizedAlias");

    /// <summary>"Entrée", "ENTREE", and "entree." are one lookup key, so the second claim is refused.</summary>
    [Fact]
    public Task One_course_alias_cannot_resolve_to_two_courses() =>
        AssertAliasIsUnambiguousAsync<Course, CourseAlias>("main-course", "dinner", "Entrée", "ENTREE.", "CourseAliases.NormalizedAlias");

    /// <summary>
    /// The other side of the rule: "Sauté" and "sauteed" are different keys after normalization — the
    /// phrase rule does not stem — so they are two aliases and may name two techniques.
    /// </summary>
    [Fact]
    public Task Technique_aliases_that_differ_after_normalization_both_resolve() =>
        AssertAliasIsUnambiguousAsync<CookingTechnique, CookingTechniqueAlias>("saute", "pan-fry", "Sauté", "sauteed", "CookingTechniqueAliases.NormalizedAlias", sameKey: false);

    [Fact]
    public Task One_technique_alias_cannot_resolve_to_two_techniques() =>
        AssertAliasIsUnambiguousAsync<CookingTechnique, CookingTechniqueAlias>("saute", "pan-fry", "Sauté", "SAUTE.", "CookingTechniqueAliases.NormalizedAlias");

    [Fact]
    public Task One_equipment_alias_cannot_resolve_to_two_equipment_types() =>
        AssertAliasIsUnambiguousAsync<EquipmentType, EquipmentTypeAlias>("slow-cooker", "dutch-oven", "Crock Pot", "crock-pot", "EquipmentTypeAliases.NormalizedAlias");

    /// <summary>
    /// The phrase rule these vocabularies borrow from the ingredient catalogue: diacritics folded, case
    /// dropped, punctuation reduced to boundaries — and word boundaries <em>kept</em>, unlike the unit-alias
    /// rule, so "main course" does not collapse to "maincourse".
    /// </summary>
    [Theory]
    [InlineData("Entrée", "entree")]
    [InlineData("ENTREE.", "entree")]
    [InlineData("Main Course", "main course")]
    [InlineData("Stir-Fry", "stir fry")]
    [InlineData("  Crock Pot  ", "crock pot")]
    [InlineData("Sauté", "saute")]
    public void Alias_normalization_folds_case_diacritics_and_punctuation(string written, string expected) =>
        Assert.Equal(expected, VocabularyPolicy.NormalizeAlias(written));

    [Fact]
    public async Task One_entry_can_have_many_aliases()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var course = NewEntry<Course>("main-course", "Main Course");
        db.Courses.Add(course);
        db.CourseAliases.AddRange(
            NewAlias<CourseAlias>(course.Id, "Entrée"),
            NewAlias<CourseAlias>(course.Id, "main dish"),
            NewAlias<CourseAlias>(course.Id, "supper"));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await db.CourseAliases.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_alias_cannot_point_at_an_entry_that_does_not_exist()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.CuisineAliases.Add(NewAlias<CuisineAlias>(Guid.NewGuid(), "Italiana"));

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>An alias has no meaning without its entry, so removing the entry removes the aliases.</summary>
    [Fact]
    public async Task Deleting_an_entry_removes_its_aliases()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var equipment = NewEntry<EquipmentType>("slow-cooker", "Slow Cooker");
        db.EquipmentTypes.Add(equipment);
        db.EquipmentTypeAliases.Add(NewAlias<EquipmentTypeAlias>(equipment.Id, "Crock Pot"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.EquipmentTypes.Remove(equipment);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.EquipmentTypeAliases.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- The safety caution reads in one direction only --------------------------------------------------

    /// <summary>
    /// <c>true</c> means a caution is required. Nothing else in the schema says anything about safety, and
    /// that is the point: there is no column a caller could read as a clearance (recipes.md).
    /// </summary>
    [Fact]
    public async Task A_cautioned_technique_records_that_a_caution_is_required()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var canning = NewEntry<CookingTechnique>("water-bath-canning", "Water-Bath Canning");
        canning.RequiresSafetyCaution = true;
        db.CookingTechniques.Add(canning);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var cautioned = await db.CookingTechniques
            .Where(technique => technique.RequiresSafetyCaution)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("water-bath-canning", Assert.Single(cautioned).Code);
    }

    /// <summary>
    /// The test about nothing, and the one worth keeping. A technique deliberately reviewed as needing no
    /// caution and a technique nobody has ever looked at are the same stored value, so <c>false</c> cannot
    /// mean "reviewed and safe" — only "no caution attached". A caller that renders it as reassurance is
    /// reading a default as a finding.
    /// </summary>
    [Fact]
    public async Task An_unreviewed_technique_is_indistinguishable_from_one_carrying_no_caution()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var reviewed = NewEntry<CookingTechnique>("bake", "Bake");
        reviewed.RequiresSafetyCaution = false;
        db.CookingTechniques.Add(reviewed);
        db.CookingTechniques.Add(NewEntry<CookingTechnique>("saute", "Sauté"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var techniques = await db.CookingTechniques
            .OrderBy(technique => technique.Code)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(techniques, technique => Assert.False(technique.RequiresSafetyCaution));

        // And the column is not nullable, so there is no third state a caller could read as "unknown"
        // either — the unknowns are simply inside the false.
        var property = db.Model.FindEntityType(typeof(CookingTechnique))!
            .FindProperty(nameof(CookingTechnique.RequiresSafetyCaution))!;
        Assert.False(property.IsNullable);
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private async Task AssertCodeIsUniqueAsync<TVocabulary>(string code, string rule)
        where TVocabulary : ControlledVocabulary, new()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Set<TVocabulary>().Add(NewEntry<TVocabulary>(code, "First"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Set<TVocabulary>().Add(NewEntry<TVocabulary>(code, "Second"));

        await AssertRejectedByAsync(db, rule);
    }

    /// <param name="sameKey">
    /// Whether the two written forms normalize to one key. False proves the negative case in the same
    /// place: forms that differ after normalization are genuinely different aliases and both are stored.
    /// </param>
    private async Task AssertAliasIsUnambiguousAsync<TVocabulary, TAlias>(
        string firstCode, string secondCode, string firstAlias, string secondAlias, string rule, bool sameKey = true)
        where TVocabulary : ControlledVocabulary, new()
        where TAlias : VocabularyAlias, new()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var first = NewEntry<TVocabulary>(firstCode, "First");
        var second = NewEntry<TVocabulary>(secondCode, "Second");
        db.Set<TVocabulary>().AddRange(first, second);
        db.Set<TAlias>().Add(NewAlias<TAlias>(first.Id, firstAlias));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Set<TAlias>().Add(NewAlias<TAlias>(second.Id, secondAlias));

        if (sameKey)
        {
            await AssertRejectedByAsync(db, rule);
            return;
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await db.Set<TAlias>().CountAsync(TestContext.Current.CancellationToken));
    }

    private static TVocabulary NewEntry<TVocabulary>(string code, string displayName)
        where TVocabulary : ControlledVocabulary, new() =>
        new() { Id = Guid.NewGuid(), Code = code, DisplayName = displayName };

    private static TAlias NewAlias<TAlias>(Guid vocabularyId, string alias)
        where TAlias : VocabularyAlias, new() =>
        new()
        {
            Id = Guid.NewGuid(),
            VocabularyId = vocabularyId,
            Alias = alias,
            NormalizedAlias = VocabularyPolicy.NormalizeAlias(alias),
        };
}
