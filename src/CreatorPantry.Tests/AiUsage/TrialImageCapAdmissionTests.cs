using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// A trial's lifetime picture cap, judged where the account's picture hold is taken (AP.7.1, B-52): per account,
/// across every day, all of a request or none of it, before the daily cap and the balance — and counting what was
/// charged rather than what was asked once a generation is finished.
/// </summary>
/// <remarks>
/// <para>
/// SQLite, one admission at a time. Two admissions racing for a trial's last picture is
/// <see cref="TrialImageCapConcurrencyTests"/>, on SQL Server, where the period's row version is real.
/// </para>
/// <para>
/// The cap is passed in, as the Media module passes the plan's. That it is the plan's and the catalogue's one figure
/// is <c>TrialEndTests</c>'; that it holds across workspaces is <c>GeneratedImageWorkerTests.Trial</c>'.
/// </para>
/// </remarks>
public sealed class TrialImageCapAdmissionTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private const string Model = "test-image";

    private const decimal Rate = 50m;

    private const int Cap = 10;

    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly MovableClock _clock = new(Noon);

    private ServiceProvider? _provider;

    public TrialImageCapAdmissionTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The cap is the trial's, not the day's: pictures made on three different days add up, and the eleventh is
    /// refused with the account's own figures and nothing held.
    /// </summary>
    [Fact]
    public async Task Exactly_the_cap_is_admitted_across_days_and_one_more_is_refused()
    {
        Start();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4)).Outcome);
        NextDay();
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4)).Outcome);
        NextDay();
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 2)).Outcome);

        var refusal = await AdmitAsync(Sam, 1);

        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, refusal.Outcome);
        Assert.Equal(Cap, refusal.TrialImageCap);
        Assert.Null(refusal.ReservationId);
        Assert.Equal(0m, refusal.Reserved);
        Assert.Equal(0m, refusal.Remaining);

        // No reset: the cap is for the whole trial, so a day's turn is not offered as the remedy.
        Assert.Null(refusal.DailyImageCapResetsAt);
        Assert.Null(refusal.PeriodEndsAt);
        Assert.Equal(3, await HoldCountAsync());

        NextDay();
        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, (await AdmitAsync(Sam, 1)).Outcome);
    }

    /// <summary>Where the line falls, request by request: everything up to the cap, nothing past it.</summary>
    [Theory]
    [InlineData(9, 1, AiQuotaAdmissionOutcome.Admitted)]
    [InlineData(9, 2, AiQuotaAdmissionOutcome.TrialImageCap)]
    [InlineData(6, 4, AiQuotaAdmissionOutcome.Admitted)]
    [InlineData(7, 4, AiQuotaAdmissionOutcome.TrialImageCap)]
    [InlineData(10, 1, AiQuotaAdmissionOutcome.TrialImageCap)]
    [InlineData(0, 4, AiQuotaAdmissionOutcome.Admitted)]
    public async Task A_request_is_admitted_only_while_it_fits_the_cap_whole(
        int before, int asked, AiQuotaAdmissionOutcome expected)
    {
        Start();

        for (var made = 0; made < before; made += 4)
        {
            await AdmitAsync(Sam, Math.Min(4, before - made));
            NextDay();
        }

        Assert.Equal(expected, (await AdmitAsync(Sam, asked)).Outcome);
    }

    /// <summary>
    /// Both caps would refuse; the trial's is the answer, because "come back tomorrow" would not help a trial whose
    /// pictures are spent.
    /// </summary>
    [Fact]
    public async Task The_trial_cap_is_judged_before_the_daily_cap()
    {
        Start();
        await AdmitAsync(Sam, 4, daily: 5);
        NextDay();
        await AdmitAsync(Sam, 5, daily: 5);

        // Nine of ten made, and five of five today: one more is past both lines.
        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, (await AdmitAsync(Sam, 2, daily: 5)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, (await AdmitAsync(Sam, 1, daily: 5)).Outcome);
    }

    /// <summary>And before the balance: an account out of credits whose pictures are spent is told about the pictures.</summary>
    [Fact]
    public async Task The_trial_cap_is_judged_before_the_balance()
    {
        Start(allowance: 500m);
        await AdmitAsync(Sam, 4);
        NextDay();
        await AdmitAsync(Sam, 4);
        NextDay();
        await AdmitAsync(Sam, 2);

        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, (await AdmitAsync(Sam, 1)).Outcome);
    }

    /// <summary>The cap is the account's; another account's pictures are not counted against it.</summary>
    [Fact]
    public async Task Two_accounts_do_not_share_a_trial_cap()
    {
        Start();
        await AdmitAsync(Alex, 4);
        NextDay();
        await AdmitAsync(Alex, 4);
        NextDay();
        await AdmitAsync(Alex, 2);

        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, (await AdmitAsync(Alex, 1)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4)).Outcome);
    }

    /// <summary>
    /// Once a generation is finished, what was charged counts and nothing else: a run nothing was billed for, and a
    /// hold given back, leave their pictures to be asked for again.
    /// </summary>
    [Fact]
    public async Task A_picture_that_was_never_charged_gives_its_place_back()
    {
        Start();

        var partial = await AdmitAsync(Sam, 4);
        await SettleAsync(partial.ReservationId!.Value, staged: 1, unusable: 1);
        NextDay();

        var nothing = await AdmitAsync(Sam, 4);
        await SettleAsync(nothing.ReservationId!.Value, staged: 0, unusable: 0);
        NextDay();

        var released = await AdmitAsync(Sam, 4);
        await ReleaseAsync(released.ReservationId!.Value);

        // Two charged of ten, so eight are left: four and four fit, and one more does not.
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4)).Outcome);
        NextDay();
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, (await AdmitAsync(Sam, 1)).Outcome);
    }

    /// <summary>On a paid plan there is no lifetime cap, and nothing is counted against one.</summary>
    [Fact]
    public async Task With_no_trial_cap_pictures_are_not_limited_in_all()
    {
        Start();

        for (var day = 0; day < 4; day++)
        {
            Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4, trial: null)).Outcome);
            NextDay();
        }
    }

    /// <summary>
    /// Pictures made before a trial cap applied still count once it does: the cap is on the account's pictures in
    /// all, and every picture hold records how many it asked for whatever its plan.
    /// </summary>
    [Fact]
    public async Task Pictures_made_without_a_trial_cap_still_count_against_one()
    {
        Start();
        await AdmitAsync(Sam, 4, trial: null);
        NextDay();
        await AdmitAsync(Sam, 4, trial: null);
        NextDay();

        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, (await AdmitAsync(Sam, 3)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 2)).Outcome);
    }

    /// <summary>A replay finds the hold it already took, and is never judged against the cap again.</summary>
    [Fact]
    public async Task A_replayed_admission_is_not_refused_by_the_cap()
    {
        Start();
        var operation = Guid.NewGuid();
        await AdmitAsync(Sam, 4, operation: operation);
        NextDay();
        await AdmitAsync(Sam, 4);
        NextDay();
        await AdmitAsync(Sam, 2);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4, operation: operation)).Outcome);
    }

    // ---- the quote (AP.7.2) -------------------------------------------------------------------------

    /// <summary>
    /// The quote judges the trial's cap as admission does: eight made of ten, four is refused with how many are left,
    /// what fits bounded by them, and nothing quoted as left "after" a request that will not go; two is affordable.
    /// </summary>
    [Fact]
    public async Task The_quote_says_when_a_request_would_pass_the_trials_pictures()
    {
        Start();
        await AdmitAsync(Sam, 4);
        NextDay();
        await AdmitAsync(Sam, 4);

        var over = await QuoteAsync(Sam, 4, trial: Cap);

        Assert.Equal(AiImageQuoteOutcome.TrialImageCap, over.Outcome);
        Assert.Equal(Cap, over.PictureCap);
        Assert.Equal(2, over.PicturesLeft);
        Assert.Equal(2, over.AffordableVariants);
        Assert.Null(over.PictureCapResetsAt);
        Assert.Null(over.RemainingAfter);
        Assert.Equal(Rate, over.PerPicture);

        Assert.Equal(AiImageQuoteOutcome.Affordable, (await QuoteAsync(Sam, 2, trial: Cap)).Outcome);
    }

    /// <summary>The day's cap the same way, with when it turns over; and where both bind, the trial's is the answer.</summary>
    [Fact]
    public async Task The_quote_says_when_a_request_would_pass_the_days_pictures_and_names_the_trial_first()
    {
        Start();
        await AdmitAsync(Sam, 4, trial: null, daily: 5);

        var day = await QuoteAsync(Sam, 2, daily: 5);

        Assert.Equal(AiImageQuoteOutcome.DailyImageCap, day.Outcome);
        Assert.Equal(5, day.PictureCap);
        Assert.Equal(1, day.PicturesLeft);
        Assert.Equal(1, day.AffordableVariants);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), day.PictureCapResetsAt);

        Assert.Equal(AiImageQuoteOutcome.TrialImageCap, (await QuoteAsync(Sam, 2, daily: 5, trial: 5)).Outcome);
        Assert.Equal(AiImageQuoteOutcome.Affordable, (await QuoteAsync(Sam, 1, daily: 5, trial: Cap)).Outcome);
    }

    /// <summary>Asking writes nothing: no hold, and the count is unchanged.</summary>
    [Fact]
    public async Task A_quote_holds_nothing()
    {
        Start();
        await QuoteAsync(Sam, 4, daily: 5, trial: Cap);

        Assert.Equal(0, await HoldCountAsync());
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task<AiImageQuoteServiceModel> QuoteAsync(string accountId, int pictures, int? daily = null, int? trial = null)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().QuoteImageAsync(
            accountId, Model, null, null, pictures, Ct, daily, trial);
    }

    private void NextDay() => _clock.Set(_clock.UtcNow.AddDays(1));

    private void Start(decimal allowance = 1_000_000m)
    {
        _provider = new ServiceCollection()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = allowance;
                options.ImageRates[Model] = Rate;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        string accountId, int pictures, int? trial = Cap, int? daily = null, Guid? operation = null)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                accountId,
                operation ?? Guid.NewGuid(),
                pictures,
                Model,
                null,
                null,
                _clock.UtcNow + TimeSpan.FromDays(30),
                daily,
                trial),
            Ct);
    }

    /// <summary>Through the facade a finished generation calls, so the counts are the ones settlement writes.</summary>
    private async Task SettleAsync(Guid reservationId, int staged, int unusable)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().StageImageUsageAsync(
            new AiImageUsageServiceModel(
                reservationId, Guid.NewGuid(), Guid.NewGuid(), staged, unusable, 0, _clock.UtcNow, "venice", Model, null),
            Ct);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);
    }

    private async Task ReleaseAsync(Guid reservationId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().ReleaseImageAsync(reservationId, Ct);
    }

    private async Task<int> HoldCountAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountAiQuotaReservations
            .AsNoTracking()
            .CountAsync(Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset instant) => UtcNow = instant;
    }
}
