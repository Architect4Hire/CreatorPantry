using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Nutrition;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Tests.Reference;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// A real SQL Server, built by the migrations, for the nutrition mapping tests (14.2): row versions that move,
/// check constraints that bite, and the unique (workspace, ingredient) index.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The evidence source is scripted</strong> (<see cref="ScriptedEvidence"/>), so a test says exactly what
/// the source answers — a newer version, a withdrawn record, an outage — without a network. Everything between
/// it and the table is real: the Ingredients facade, the Measurement facade, the mapping seam and the database.
/// </para>
/// <para>
/// <strong>The evidence cache is turned off</strong>, so a test that changes what the source says sees the change
/// at once. In a host the cache holds a found food for a day, which a refresh then reads.
/// </para>
/// <para>
/// Tests share the database, so each one makes its own ingredients (<see cref="NewIngredientAsync"/>): a mapping is
/// unique per workspace and ingredient, and fresh ingredients keep tests from deciding each other's answers.
/// </para>
/// </remarks>
public sealed class SqlServerNutritionFixture : IAsyncLifetime
{
    private readonly SqlServerTestDatabase _container = new();

    private ServiceProvider? _provider;

    public static readonly DateTimeOffset Now = new(2037, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public static Guid WorkspaceA { get; } = Guid.NewGuid();

    public static Guid WorkspaceB { get; } = Guid.NewGuid();

    public static Guid GramId { get; } = Guid.NewGuid();

    public static Guid CupId { get; } = Guid.NewGuid();

    public static Guid EachId { get; } = Guid.NewGuid();

    public static Guid CelsiusId { get; } = Guid.NewGuid();

    /// <summary>The approved source's own row, as the seeder writes it in every environment.</summary>
    public static Guid FdcSourceId { get; } = SeedId.For("ReferenceSources", EvidenceSourceCatalog.UsdaFoodDataCentral.Code);

    /// <summary>Two vetted trait sources, so two of them can disagree.</summary>
    public static Guid OfficialListId { get; } = Guid.NewGuid();

    public static Guid PublisherId { get; } = Guid.NewGuid();

    public static Guid MilkId { get; } = Guid.NewGuid();

    public static Guid WheatId { get; } = Guid.NewGuid();

    /// <summary>A retired allergen: not one an analysis may be asked about.</summary>
    public static Guid RetiredAllergenId { get; } = Guid.NewGuid();

    public static Guid VeganId { get; } = Guid.NewGuid();

    public static Guid GlutenFreeId { get; } = Guid.NewGuid();

    public ScriptedEvidence Evidence { get; } = new();

    public ManualClock Clock { get; } = new(Now);

    /// <summary>The recipe versions an estimate can read. The Recipes module's own read is tested on its side.</summary>
    public StubRecipeVersions Versions { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddSingleton<IClock>(Clock)
            .AddSingleton<IReferenceEvidenceGateway>(Evidence)
            .AddSingleton<IApplicationCache>(new FakeApplicationCache { Disabled = true })
            .AddTenancy()
            .AddIngredientModule()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddNutritionModule()
            // After the module's own registration, so this one resolves.
            .AddSingleton<INutritionRecipeVersionReader>(Versions)
            .AddApplicationTime()
            .AddAudit()
            .AddLogging()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        await db.Database.MigrateAsync();
        await SeedAsync(db);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>A scope resolved as a member of <paramref name="workspaceId"/> in <paramref name="role"/>, the way a request arrives.</summary>
    public AsyncServiceScope ScopeFor(Guid workspaceId, WorkspaceRole role = WorkspaceRole.Editor, Guid? membershipId = null)
    {
        var scope = _provider!.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            membershipId ?? Guid.NewGuid(),
            role,
            "test-account");

        return scope;
    }

    /// <summary>A scope with no workspace resolved, for writing global reference data.</summary>
    public AsyncServiceScope PlatformScope() => _provider!.CreateAsyncScope();

    /// <summary>A platform ingredient no other test uses.</summary>
    public async Task<Guid> NewIngredientAsync(string canonicalName, bool isActive = true)
    {
        await using var scope = PlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var normalized = NameNormalization.NormalizeName(canonicalName);

        var ingredient = new Ingredient
        {
            Id = Guid.NewGuid(),
            CanonicalName = canonicalName,
            NormalizedName = normalized,
            SearchText = normalized,
            IsActive = isActive,
        };

        db.Ingredients.Add(ingredient);
        await db.SaveChangesAsync();

        return ingredient.Id;
    }

    private static async Task SeedAsync(CreatorPantryDbContext db)
    {
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "Workspace A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "Workspace B", Slug = "workspace-b", CreatedAt = Now });

        db.MeasurementUnits.AddRange(
            Unit(GramId, "g", MeasurementDimension.Mass, 1m),
            Unit(CupId, "cup-us", MeasurementDimension.Volume, 236.5882365m),
            Unit(EachId, "each", MeasurementDimension.Count, 1m),
            Unit(CelsiusId, "celsius", MeasurementDimension.Temperature, null));

        var fdc = EvidenceSourceCatalog.UsdaFoodDataCentral;
        db.ReferenceSources.Add(new ReferenceSource
        {
            Id = FdcSourceId,
            Code = fdc.Code,
            Name = fdc.Name,
            Kind = fdc.Kind,
            Url = fdc.Url,
            Citation = fdc.Citation,
            LicenseCode = fdc.LicenseCode,
            LicenseUrl = fdc.LicenseUrl,
        });

        db.ReferenceSources.AddRange(
            new ReferenceSource { Id = OfficialListId, Code = "test-official-list", Name = "Test official list", Kind = ReferenceSourceKind.OfficialDatabase, Citation = "Test official list." },
            new ReferenceSource { Id = PublisherId, Code = "test-publisher", Name = "Test publisher", Kind = ReferenceSourceKind.Publisher, Citation = "Test publisher's tables." });

        db.Allergens.AddRange(
            new Allergen { Id = MilkId, Code = "milk", DisplayName = "Milk", Description = "Milk." },
            new Allergen { Id = WheatId, Code = "wheat", DisplayName = "Wheat", Description = "Wheat." },
            new Allergen { Id = RetiredAllergenId, Code = "lupin", DisplayName = "Lupin", Description = "Lupin.", IsActive = false });

        db.DietaryProfiles.AddRange(
            new DietaryProfile { Id = VeganId, Code = "vegan", DisplayName = "Vegan" },
            new DietaryProfile { Id = GlutenFreeId, Code = "gluten-free", DisplayName = "Gluten-Free" });

        await db.SaveChangesAsync();
    }

    private static MeasurementUnit Unit(Guid id, string code, MeasurementDimension dimension, decimal? factor) => new()
    {
        Id = id,
        Code = code,
        DisplayName = code,
        PluralName = code,
        Abbreviation = code,
        Dimension = dimension,
        System = MeasurementSystem.Neutral,
        BaseUnitFactor = factor,
        DisplayPrecision = 2,
    };
}

/// <summary>Recipe versions by recipe and version id, as the reader would return them.</summary>
public sealed class StubRecipeVersions : INutritionRecipeVersionReader
{
    private readonly Dictionary<(Guid, Guid), NutritionRecipeVersion> _versions = [];

    public void Add(Guid recipeId, Guid versionId, NutritionRecipeVersion version)
    {
        lock (_versions)
        {
            _versions[(recipeId, versionId)] = version;
        }
    }

    public Task<NutritionRecipeVersion?> ReadAsync(Guid recipeId, Guid versionId, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            return Task.FromResult(_versions.GetValueOrDefault((recipeId, versionId)));
        }
    }

    /// <summary>The version added under that recipe with that number, or the Recipes module's refusal for none.</summary>
    public Task<OperationResult<NumberedNutritionRecipeVersion>> ReadByNumberAsync(Guid recipeId, int versionNumber, CancellationToken cancellationToken)
    {
        lock (_versions)
        {
            var found = _versions.FirstOrDefault(entry => entry.Key.Item1 == recipeId && entry.Value.VersionNumber == versionNumber);

            return Task.FromResult(found.Value is { } version
                ? OperationResult<NumberedNutritionRecipeVersion>.Success(new(found.Key.Item2, version))
                : OperationResult<NumberedNutritionRecipeVersion>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>())));
        }
    }
}

public sealed class ManualClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// The evidence source, as a script: what a search for a name finds, and what each record says. A record not in
/// the script is not found; <see cref="Down"/> makes every read unavailable.
/// </summary>
public sealed class ScriptedEvidence : IReferenceEvidenceGateway
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<string>> _searches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReferenceFoodFetch> _foods = new(StringComparer.Ordinal);

    public bool Down { get; set; }

    public int Searches { get; private set; }

    /// <summary>Every term the source was asked to search for — what left the platform.</summary>
    public List<string> Terms { get; } = [];

    public EvidenceCoverage Describe(ReferenceEvidenceKind kind) =>
        EvidenceCoverage.Of(kind, [EvidenceSourceCatalog.UsdaFoodDataCentral.Code]);

    /// <summary>A search for <paramref name="ingredientName"/> finds these records, in this order.</summary>
    public void Finds(string ingredientName, params string[] recordIds)
    {
        lock (_gate)
        {
            _searches[ingredientName] = [.. recordIds];
        }
    }

    /// <summary>The record <paramref name="recordId"/> is an approved food with this name and version.</summary>
    public void Has(string recordId, string description, string sourceVersion = "sr-legacy/2019-04-01", params PortionEvidence[] portions) =>
        Has(recordId, description, sourceVersion, portions, []);

    /// <summary>The record is an approved food reporting these nutrients per 100 g.</summary>
    public void Has(
        string recordId, string description, string sourceVersion, IReadOnlyList<PortionEvidence> portions, IReadOnlyList<NutrientEvidence> nutrients)
    {
        var source = EvidenceSourceCatalog.UsdaFoodDataCentral;

        lock (_gate)
        {
            _foods[recordId] = new ReferenceFoodFetch(
                ReferenceEvidenceOutcome.Found,
                new ReferenceFoodEvidence(
                    new EvidenceProvenance(
                        source.Code, source.Name, source.Kind, sourceVersion, source.LicenseCode, source.LicenseUrl,
                        source.Citation, recordId, null, SqlServerNutritionFixture.Now),
                    description,
                    nutrients,
                    portions));
        }
    }

    /// <summary>The record <paramref name="recordId"/> answers <paramref name="outcome"/> — gone, or not approved.</summary>
    public void Answers(string recordId, ReferenceEvidenceOutcome outcome)
    {
        lock (_gate)
        {
            _foods[recordId] = ReferenceFoodFetch.Of(outcome);
        }
    }

    public Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(
                Down ? ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.Unavailable)
                : _foods.TryGetValue(externalRecordId, out var food) ? food
                : ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.NotFound));
        }
    }

    public Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Searches++;
            Terms.Add(search.Term);

            if (Down)
            {
                return Task.FromResult(ReferenceFoodSearchResult.Of(ReferenceEvidenceOutcome.Unavailable));
            }

            var ids = _searches.TryGetValue(search.Term, out var found) ? found : [];
            var candidates = ids
                .Select(id => _foods.TryGetValue(id, out var food) && food.Food is { } evidence
                    ? new ReferenceFoodCandidate(evidence.Provenance.SourceCode, id, evidence.Description, evidence.Provenance.SourceVersion, null)
                    : new ReferenceFoodCandidate(EvidenceSourceCatalog.UsdaFoodDataCentral.Code, id, $"Record {id}", "sr-legacy/2019-04-01", null))
                .ToList();

            return Task.FromResult(new ReferenceFoodSearchResult(ReferenceEvidenceOutcome.Found, candidates));
        }
    }
}
