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
/// Exercises the EF configuration for <see cref="IngredientDensityReference"/> and
/// <see cref="ReferenceSource"/> against an in-memory SQLite database. This verifies the provenance,
/// uniqueness, and dimension rules the <c>AddIngredientDensityReference</c> migration also creates, without
/// applying it anywhere.
/// </summary>
public sealed class IngredientDensityConstraintTests : IDisposable
{
    private static readonly DateOnly Effective = new(2026, 1, 15);

    private readonly SqliteAuthServices _services = new();

    public void Dispose() => _services.Dispose();

    // --- Global reference data, not workspace-owned ------------------------------------------------------

    [Fact]
    public void Density_entities_carry_no_workspace_id()
    {
        using var scope = _services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        foreach (var type in new[] { typeof(IngredientDensityReference), typeof(ReferenceSource) })
        {
            Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(type));
            Assert.Null(model.FindEntityType(type)!.FindProperty(nameof(IWorkspaceOwned.WorkspaceId)));
        }
    }

    [Fact]
    public async Task Densities_are_readable_without_a_resolved_workspace()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDensityReferences.Add(world.Density());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var densities = await db.IngredientDensityReferences.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(densities);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- Provenance is mandatory ------------------------------------------------------------------------

    /// <summary>
    /// The structural guarantee behind "no universal cup-to-gram conversion": a density row cannot exist
    /// without naming an ingredient, so there is no general-purpose figure to fall back on.
    /// </summary>
    [Fact]
    public async Task A_density_cannot_exist_without_an_ingredient()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.IngredientId = Guid.NewGuid();

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task A_density_cannot_exist_without_a_source()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.ReferenceSourceId = Guid.NewGuid();

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>A citation is what makes a reference fact usable, so it cannot be blank.</summary>
    [Fact]
    public async Task A_source_requires_a_citation()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var source = NewSource("orphan", ReferenceSourceKind.Publisher);
        source.Citation = null!;

        db.ReferenceSources.Add(source);

        await AssertRejectedByAsync(db, "NOT NULL");
    }

    [Fact]
    public async Task Two_sources_cannot_share_a_code()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.ReferenceSources.Add(NewSource("usda-fdc", ReferenceSourceKind.OfficialDatabase));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ReferenceSources.Add(NewSource("usda-fdc", ReferenceSourceKind.Publisher));

        await AssertRejectedByAsync(db, "ReferenceSources.Code");
    }

    [Fact]
    public async Task A_source_still_cited_by_a_density_cannot_be_deleted()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDensityReferences.Add(world.Density());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Cleared first: the guarantee under test is the database's. A tracked dependent lets EF act on the
        // relationship before the database sees the delete, which for a required foreign key means EF raises its
        // own error rather than the database raising one.
        db.ChangeTracker.Clear();
        db.ReferenceSources.Remove(
            await db.ReferenceSources.SingleAsync(source => source.Id == world.SourceId, TestContext.Current.CancellationToken));

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>
    /// The counterpart to <see cref="IngredientConstraintTests.Deleting_a_referenced_unit_with_the_ingredient_tracked_clears_the_reference_instead"/>,
    /// and the reason a required foreign key is the safer shape. There, an <em>optional</em> reference was
    /// silently nulled when the dependent happened to be tracked, so the database's <c>RESTRICT</c> never got a
    /// say. Here the column cannot be nulled, so EF refuses outright — and it does so at <c>Remove</c> rather
    /// than at <c>SaveChanges</c>, failing before a transaction is even opened. Provenance therefore cannot be
    /// dropped by accident from either direction.
    /// </summary>
    [Fact]
    public async Task Deleting_a_cited_source_with_the_density_tracked_fails_at_remove()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDensityReferences.Add(world.Density());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var source = await db.ReferenceSources.SingleAsync(source => source.Id == world.SourceId, TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidOperationException>(() => db.ReferenceSources.Remove(source));

        Assert.Contains("has been severed", exception.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        Assert.Single(await db.ReferenceSources.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.IngredientDensityReferences.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_an_ingredient_removes_its_densities()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDensityReferences.Add(world.Density());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Ingredients.Remove(await db.Ingredients.SingleAsync(TestContext.Current.CancellationToken));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.IngredientDensityReferences.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- A model estimate can never be approved ---------------------------------------------------------

    /// <summary>
    /// The rule worth having in the database: only <see cref="DensityReviewStatus.Approved"/> rows drive a
    /// conversion, so approving a model-generated figure is how an invented number would reach a creator as a
    /// vetted fact. The composite foreign key means the locally stored kind cannot lie about its source either.
    /// </summary>
    [Fact]
    public async Task An_ai_estimated_density_cannot_be_approved()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var density = world.Density();
        density.ReviewStatus = DensityReviewStatus.Approved;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, "CK_IngredientDensityReferences_AiEstimate_NotApproved");
    }

    /// <summary>Recording the estimate is fine; only promoting it is not.</summary>
    [Theory]
    [InlineData(DensityReviewStatus.Unreviewed)]
    [InlineData(DensityReviewStatus.Rejected)]
    [InlineData(DensityReviewStatus.Superseded)]
    public async Task An_ai_estimated_density_may_be_recorded_unapproved(DensityReviewStatus status)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var density = world.Density();
        density.ReviewStatus = status;

        db.IngredientDensityReferences.Add(density);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, (await db.IngredientDensityReferences.SingleAsync(TestContext.Current.CancellationToken)).ReviewStatus);
    }

    /// <summary>
    /// A density cannot misreport its own provenance to dodge the rule above: claiming a vetted kind while
    /// pointing at the model-estimated source fails the composite foreign key.
    /// </summary>
    [Fact]
    public async Task A_density_cannot_claim_a_kind_its_source_does_not_have()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, ReferenceSourceKind.AiEstimated);
        var density = world.Density();
        density.ReferenceSourceKind = ReferenceSourceKind.OfficialDatabase;
        density.ReviewStatus = DensityReviewStatus.Approved;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Theory]
    [InlineData(ReferenceSourceKind.OfficialDatabase)]
    [InlineData(ReferenceSourceKind.Publisher)]
    [InlineData(ReferenceSourceKind.LabMeasured)]
    public async Task A_vetted_source_may_be_approved(ReferenceSourceKind kind)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db, kind);
        var density = world.Density();
        density.ReviewStatus = DensityReviewStatus.Approved;

        db.IngredientDensityReferences.Add(density);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DensityReviewStatus.Approved, (await db.IngredientDensityReferences.SingleAsync(TestContext.Current.CancellationToken)).ReviewStatus);
    }

    // --- Dimension rules --------------------------------------------------------------------------------

    [Fact]
    public async Task The_mass_side_cannot_reference_a_volume_unit()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.MassUnitId = world.MilliliterId; // Still claims Mass, so the composite key finds no such row.

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    [Fact]
    public async Task The_volume_side_cannot_reference_a_mass_unit()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.VolumeUnitId = world.GramId;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, ForeignKeyViolation);
    }

    /// <summary>Declaring the dimension honestly does not help either: the check constraint pins each side.</summary>
    [Fact]
    public async Task The_mass_side_cannot_declare_a_non_mass_dimension()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.MassUnitId = world.MilliliterId;
        density.MassUnitDimension = MeasurementDimension.Volume;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, "CK_IngredientDensityReferences_MassUnit_Dimension");
    }

    [Fact]
    public async Task The_volume_side_cannot_declare_a_non_volume_dimension()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.VolumeUnitId = world.GramId;
        density.VolumeUnitDimension = MeasurementDimension.Mass;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, "CK_IngredientDensityReferences_VolumeUnit_Dimension");
    }

    // --- Quantities and precision -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Mass_must_be_positive(decimal mass)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.MassQuantity = mass;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, "CK_IngredientDensityReferences_MassQuantity_Positive");
    }

    /// <summary>A zero volume would divide by zero wherever this density is used.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Volume_must_be_positive(decimal volume)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.VolumeQuantity = volume;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, "CK_IngredientDensityReferences_VolumeQuantity_Positive");
    }

    [Theory]
    [InlineData(DensityPolicy.MinDisplayPrecision - 1)]
    [InlineData(DensityPolicy.MaxDisplayPrecision + 1)]
    public async Task Display_precision_outside_the_allowed_range_is_rejected(int displayPrecision)
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.DisplayPrecision = displayPrecision;

        db.IngredientDensityReferences.Add(density);

        await AssertRejectedByAsync(db, "CK_IngredientDensityReferences_DisplayPrecision");
    }

    /// <summary>
    /// The figures must survive the round trip at full scale, because a conversion divides these values rather
    /// than reading a stored ratio — rounding here would be inherited by every derived quantity.
    /// </summary>
    [Fact]
    public async Task Recorded_quantities_keep_their_declared_scale()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var density = world.Density();
        density.MassQuantity = 120.284375m;
        density.VolumeQuantity = 236.5882365m;
        db.IngredientDensityReferences.Add(density);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var saved = await db.IngredientDensityReferences.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(120.284375m, saved.MassQuantity);
        Assert.Equal(236.5882365m, saved.VolumeQuantity);
    }

    // --- Uniqueness -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_ingredient_condition_source_and_date_cannot_repeat()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDensityReferences.Add(world.Density("sifted"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.IngredientDensityReferences.Add(world.Density("Sifted"));

        await AssertRejectedByAsync(db, "IngredientDensityReferences.IngredientId");
    }

    /// <summary>
    /// Proves the empty-string choice for an unspecified condition behaves as a real key value. Were it
    /// nullable, SQL Server would reject the second row and SQLite would accept it — the same schema enforcing
    /// two different rules.
    /// </summary>
    [Fact]
    public async Task Two_densities_with_no_stated_condition_cannot_repeat()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        db.IngredientDensityReferences.Add(world.Density());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.IngredientDensityReferences.Add(world.Density());

        await AssertRejectedByAsync(db, "IngredientDensityReferences.IngredientId");
    }

    /// <summary>
    /// Sifted and scooped flour differ by roughly a fifth, so one ingredient holding several conditions is the
    /// point of the model rather than a conflict.
    /// </summary>
    [Fact]
    public async Task One_ingredient_may_hold_several_conditions()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);

        db.IngredientDensityReferences.AddRange(
            world.Density("sifted"),
            world.Density("spooned and leveled"),
            world.Density("packed"),
            world.Density());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, await db.IngredientDensityReferences.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Two authorities may measure the same thing and disagree. The schema records both; choosing between them
    /// is a domain decision, not something a unique index should settle.
    /// </summary>
    [Fact]
    public async Task Two_sources_may_disagree_about_the_same_ingredient_and_condition()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var other = NewSource("king-arthur", ReferenceSourceKind.Publisher);
        db.ReferenceSources.Add(other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = world.Density("sifted");
        first.MassQuantity = 120m;
        var second = world.Density("sifted");
        second.ReferenceSourceId = other.Id;
        second.ReferenceSourceKind = other.Kind;
        second.MassQuantity = 113m;
        db.IngredientDensityReferences.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var masses = await db.IngredientDensityReferences
            .Select(density => density.MassQuantity)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([113m, 120m], masses.Order());
    }

    /// <summary>One source revising its own figure is a second row on a later date, not an edit.</summary>
    [Fact]
    public async Task One_source_may_revise_its_figure_on_a_later_date()
    {
        await using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var world = await SeedAsync(db);
        var original = world.Density("sifted");
        original.ReviewStatus = DensityReviewStatus.Superseded;
        var revised = world.Density("sifted");
        revised.EffectiveFrom = Effective.AddYears(1);

        db.IngredientDensityReferences.AddRange(original, revised);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.IngredientDensityReferences.CountAsync(TestContext.Current.CancellationToken));
    }

    // --- Normalization ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Sifted", "sifted")]
    [InlineData("Spooned and Leveled", "spooned and leveled")]
    [InlineData("  packed  ", "packed")]
    [InlineData("at 20 °C", "at 20 c")]
    public void Normalizing_a_condition_folds_case_and_punctuation(string note, string expected) =>
        Assert.Equal(expected, DensityPolicy.NormalizeCondition(note));

    [Fact]
    public void An_unstated_condition_normalizes_to_the_unspecified_value() =>
        Assert.Equal(DensityPolicy.UnspecifiedCondition, DensityPolicy.NormalizeCondition("   "));

    // --- Helpers ----------------------------------------------------------------------------------------

    /// <summary>Flour, a gram, a millilitre, and one source — the minimum any density test needs.</summary>
    private static async Task<DensityWorld> SeedAsync(
        CreatorPantryDbContext db, ReferenceSourceKind kind = ReferenceSourceKind.OfficialDatabase)
    {
        var gram = NewUnit("g", MeasurementDimension.Mass);
        var milliliter = NewUnit("ml", MeasurementDimension.Volume);
        var flour = new Ingredient
        {
            Id = Guid.NewGuid(),
            CanonicalName = "All-Purpose Flour",
            NormalizedName = IngredientPolicy.NormalizeName("All-Purpose Flour"),
            SearchText = IngredientPolicy.BuildSearchText("all purpose flour", []),
            IsActive = true,
        };
        var source = NewSource("usda-fdc", kind);

        db.MeasurementUnits.AddRange(gram, milliliter);
        db.Ingredients.Add(flour);
        db.ReferenceSources.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new DensityWorld(flour.Id, source.Id, kind, gram.Id, milliliter.Id);
    }

    private sealed record DensityWorld(Guid IngredientId, Guid SourceId, ReferenceSourceKind SourceKind, Guid GramId, Guid MilliliterId)
    {
        /// <summary>A valid, unreviewed density for the seeded world, ready to be spoiled by one assignment.</summary>
        public IngredientDensityReference Density(string conditionNote = "") => new()
        {
            Id = Guid.NewGuid(),
            IngredientId = IngredientId,
            ReferenceSourceId = SourceId,
            ReferenceSourceKind = SourceKind,
            MassQuantity = 125m,
            MassUnitId = GramId,
            MassUnitDimension = MeasurementDimension.Mass,
            VolumeQuantity = 240m,
            VolumeUnitId = MilliliterId,
            VolumeUnitDimension = MeasurementDimension.Volume,
            ConditionNote = conditionNote,
            NormalizedCondition = DensityPolicy.NormalizeCondition(conditionNote),
            DisplayPrecision = 1,
            EffectiveFrom = Effective,
            ReviewStatus = DensityReviewStatus.Unreviewed,
        };
    }

    private static ReferenceSource NewSource(string code, ReferenceSourceKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Name = code,
        Kind = kind,
        Citation = $"{code} reference data",
        IsActive = true,
    };

    private static MeasurementUnit NewUnit(string code, MeasurementDimension dimension) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = code,
        PluralName = $"{code}s",
        Abbreviation = code,
        Dimension = dimension,
        System = MeasurementSystem.Metric,
        BaseUnitFactor = 1m,
        DisplayPrecision = 0,
        IsActive = true,
    };
}
