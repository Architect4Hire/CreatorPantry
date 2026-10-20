using System.Net;
using System.Reflection;
using System.Text;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Tests.Auth;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.Ingredients;

/// <summary>
/// The FoodData Central adapter against recorded responses (14.1): the mapping into evidence, every way FDC can
/// decline, what is retried and what is not, the throttle, and what never reaches a log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No test here calls FDC.</strong> Every 200, the 403 and the empty 404 are FDC's own bytes, recorded on
/// 2026-10-20 (<c>Fixtures/UsdaFdc/README.md</c>) and replayed through a stub handler. The 429 body is authored,
/// not recorded — recording one would have cost a key its hour — and nothing reads it: a throttle is classified
/// by status alone.
/// </para>
/// <para>
/// The key is made up and recognisable, so a leak assertion can look for it by name.
/// </para>
/// </remarks>
public sealed class UsdaFdcEvidenceGatewayTests
{
    private const string ApiKey = "fdc_SECRET_KEY_do_not_log_93be";
    private static readonly DateTimeOffset Now = new(2037, 10, 20, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Recorded responses ----

    private static string Fixture(string name)
    {
        var assembly = typeof(UsdaFdcEvidenceGatewayTests).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(candidate => candidate.EndsWith($".{name}.fdc.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Scripted Ok(string fixture) => new(HttpStatusCode.OK, Fixture(fixture));

    private static Scripted Status(HttpStatusCode status, string body = "", int? retryAfterSeconds = null) =>
        new(status, body, retryAfterSeconds);

    // ---- Mapping ----

    [Fact]
    public async Task A_foundation_food_maps_to_evidence_with_full_provenance()
    {
        var fdc = new StubFdc(Ok("foundation-flour-789890"));

        var result = await fdc.Gateway().GetFoodAsync("789890", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Found, result.Outcome);
        var food = Assert.IsType<ReferenceFoodEvidence>(result.Food);
        Assert.Equal("Flour, wheat, all-purpose, enriched, bleached", food.Description);

        var approved = EvidenceSourceCatalog.UsdaFoodDataCentral;
        Assert.Equal(
            new EvidenceProvenance(
                "usda-fdc",
                "USDA FoodData Central",
                ReferenceSourceKind.OfficialDatabase,
                "foundation/2020-04-01",
                "CC0-1.0",
                approved.LicenseUrl,
                approved.Citation,
                "789890",
                new DateOnly(2020, 4, 1),
                Now),
            food.Provenance);
    }

    [Fact]
    public async Task Nutrients_are_the_known_set_per_100_g_in_their_own_units_and_say_how_they_were_derived()
    {
        var fdc = new StubFdc(Ok("foundation-flour-789890"));

        var food = (await fdc.Gateway().GetFoodAsync("789890", Ct)).Food!;

        Assert.Equal(
            [
                new NutrientEvidence(NutrientCode.Energy, 366.0m, NutrientUnit.Kilocalorie, "Energy", "Calculated"),
                new NutrientEvidence(NutrientCode.Protein, 10.9m, NutrientUnit.Gram, "Protein", "Calculated"),
                new NutrientEvidence(NutrientCode.TotalFat, 1.48m, NutrientUnit.Gram, "Total lipid (fat)", "Analytical"),
                new NutrientEvidence(NutrientCode.Carbohydrate, 77.3m, NutrientUnit.Gram, "Carbohydrate, by difference", "Calculated"),
                new NutrientEvidence(NutrientCode.Sodium, 2.0m, NutrientUnit.Milligram, "Sodium, Na", "Analytical"),
                new NutrientEvidence(NutrientCode.Potassium, 136.0m, NutrientUnit.Milligram, "Potassium, K", "Analytical"),
                new NutrientEvidence(NutrientCode.Calcium, 19.0m, NutrientUnit.Milligram, "Calcium, Ca", "Analytical"),
                new NutrientEvidence(NutrientCode.Iron, 5.62m, NutrientUnit.Milligram, "Iron, Fe", "Analytical"),
            ],
            food.Nutrients);
    }

    [Fact]
    public async Task Energy_falls_back_to_the_atwater_specific_row_and_sugars_to_the_summed_row_when_the_general_ones_are_absent()
    {
        var fdc = new StubFdc(Ok("foundation-yogurt-2259793"));

        var food = (await fdc.Gateway().GetFoodAsync("2259793", Ct)).Food!;

        Assert.Equal("foundation/2022-04-28", food.Provenance.SourceVersion);

        var energy = Assert.Single(food.Nutrients, nutrient => nutrient.Code == NutrientCode.Energy);
        Assert.Equal(77.3185458m, energy.AmountPer100Grams);
        Assert.Equal("Energy (Atwater Specific Factors)", energy.SourceLabel);

        var sugars = Assert.Single(food.Nutrients, nutrient => nutrient.Code == NutrientCode.TotalSugars);
        Assert.Equal(4.0936m, sugars.AmountPer100Grams);

        // Micrograms arrive as "µg"; the IU rows FDC also reports have no place here and are dropped.
        Assert.Equal(new NutrientEvidence(NutrientCode.VitaminD, 0.778m, NutrientUnit.Microgram, "Vitamin D (D2 + D3)", "Calculated"),
            Assert.Single(food.Nutrients, nutrient => nutrient.Code == NutrientCode.VitaminD));
        Assert.Equal(food.Nutrients.Count, food.Nutrients.Select(nutrient => nutrient.Code).Distinct().Count());
        Assert.All(food.Nutrients, nutrient => Assert.Equal(EvidencePolicy.UnitOf(nutrient.Code), nutrient.Unit));
    }

    [Fact]
    public async Task An_sr_legacy_cup_is_density_evidence_and_a_racc_serving_is_not()
    {
        var legacy = (await new StubFdc(Ok("sr-legacy-flour-169761")).Gateway().GetFoodAsync("169761", Ct)).Food!;
        var foundation = (await new StubFdc(Ok("foundation-flour-789890")).Gateway().GetFoodAsync("789890", Ct)).Food!;

        Assert.Equal("sr-legacy/2019-04-01", legacy.Provenance.SourceVersion);
        var cup = Assert.Single(legacy.Portions);
        Assert.Equal(new PortionEvidence(1.0m, 125.0m, "cup-us", null, "cup"), cup);
        Assert.True(cup.IsDensity);

        // A reference serving is a weight, not a measure: no volume is read into it.
        var racc = Assert.Single(foundation.Portions);
        Assert.Equal(new PortionEvidence(1.0m, 30.0m, null, null, "RACC"), racc);
        Assert.False(racc.IsDensity);
    }

    [Fact]
    public async Task A_value_the_source_assumed_zero_keeps_the_source_saying_so()
    {
        var food = (await new StubFdc(Ok("sr-legacy-flour-169761")).Gateway().GetFoodAsync("169761", Ct)).Food!;

        var cholesterol = Assert.Single(food.Nutrients, nutrient => nutrient.Code == NutrientCode.Cholesterol);
        Assert.Equal(0m, cholesterol.AmountPer100Grams);
        Assert.StartsWith("Assumed zero", cholesterol.Derivation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cup", "cup-us", null)]
    [InlineData("cup, sifted", "cup-us", "sifted")]
    [InlineData("cup, packed", "cup-us", "packed")]
    [InlineData("cup (8 fl oz)", "cup-us", null)]
    [InlineData("tbsp", "tbsp", null)]
    [InlineData("fl oz", "floz-us", null)]
    [InlineData("large", null, null)]
    [InlineData("oz", null, null)]
    [InlineData("", null, null)]
    public void A_modifier_yields_a_volume_only_for_a_word_in_the_table(string modifier, string? unit, string? condition)
    {
        Assert.Equal((unit, condition), UsdaFdcFoodReader.SplitModifier(modifier));
    }

    [Fact]
    public void Every_volume_the_adapter_names_is_a_seeded_volume_unit()
    {
        var volumes = MeasurementSeedData.Units()
            .Where(unit => unit.Dimension == MeasurementDimension.Volume)
            .Select(unit => unit.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(UsdaFdcFoodReader.VolumeUnits.Values, code => Assert.Contains(code, volumes));
    }

    [Fact]
    public async Task A_branded_food_is_not_approved_evidence()
    {
        var fdc = new StubFdc(Ok("branded-flour-2671058"));

        var result = await fdc.Gateway().GetFoodAsync("2671058", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.NoApprovedSource, result.Outcome);
        Assert.Null(result.Food);
    }

    // ---- Search ----

    [Fact]
    public async Task A_search_returns_candidates_from_approved_data_sets_with_their_versions()
    {
        var fdc = new StubFdc(Ok("search-flour"));

        var result = await fdc.Gateway().SearchFoodsAsync(Search("flour wheat all-purpose"), Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Found, result.Outcome);
        Assert.Equal(
            ["789890", "789951", "790018", "169761", "168894"],
            result.Candidates.Select(candidate => candidate.ExternalRecordId));
        Assert.Equal(
            new ReferenceFoodCandidate("usda-fdc", "789890", "Flour, wheat, all-purpose, enriched, bleached", "foundation/2020-04-01", new DateOnly(2020, 4, 1)),
            result.Candidates[0]);
        Assert.Equal("sr-legacy/2019-04-01", result.Candidates[3].SourceVersion);
    }

    [Fact]
    public async Task A_search_asks_only_for_approved_data_sets_and_carries_no_key_in_the_address()
    {
        var fdc = new StubFdc(Ok("search-flour"));

        await fdc.Gateway().SearchFoodsAsync(Search("Flour, wheat & rye"), Ct);

        var sent = Assert.Single(fdc.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(
            "https://api.fdc.example/fdc/v1/foods/search?query=Flour%2C%20wheat%20%26%20rye&dataType=Foundation&dataType=SR%20Legacy&pageSize=10",
            sent.Uri);
        Assert.Equal(ApiKey, sent.ApiKeyHeader);
        Assert.DoesNotContain(ApiKey, sent.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", sent.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_search_hit_from_an_unapproved_data_set_is_dropped_even_if_the_source_returns_one()
    {
        var body = """{ "foods": [ { "fdcId": 2671058, "dataType": "Branded", "description": "FLOUR", "publishedDate": "2023-11-16" }, { "fdcId": 169761, "dataType": "SR Legacy", "description": "Wheat flour", "publishedDate": "2019-04-01" } ] }""";
        var fdc = new StubFdc(Status(HttpStatusCode.OK, body));

        var result = await fdc.Gateway().SearchFoodsAsync(Search("flour"), Ct);

        Assert.Equal("169761", Assert.Single(result.Candidates).ExternalRecordId);
    }

    [Fact]
    public void A_search_can_only_be_made_from_a_platform_ingredient()
    {
        // A creator's own line is workspace content; there is no way to hand one to the source.
        Assert.Empty(typeof(ReferenceFoodSearch).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal("Butter", Search("  Butter ").Term);
        Assert.Throws<ArgumentException>(() => Search("   "));
    }

    // ---- The id never reaches a path unless it is one ----

    [Theory]
    [InlineData("")]
    [InlineData("../food/1")]
    [InlineData("789890?api_key=x")]
    [InlineData("78 9890")]
    [InlineData("123456789012345678901234567890123")]
    public async Task An_id_that_is_not_digits_is_not_found_and_nothing_is_sent(string id)
    {
        var fdc = new StubFdc();

        var result = await fdc.Gateway().GetFoodAsync(id, Ct);

        Assert.Equal(ReferenceEvidenceOutcome.NotFound, result.Outcome);
        Assert.Empty(fdc.Requests);
    }

    // ---- Every way it can decline ----

    [Fact]
    public async Task A_missing_food_is_not_found_and_is_asked_for_once()
    {
        // FDC's own 404 has no body at all.
        var fdc = new StubFdc(Status(HttpStatusCode.NotFound));

        var result = await fdc.Gateway().GetFoodAsync("1", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.NotFound, result.Outcome);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task A_refused_key_is_terminal_and_never_retried()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.Forbidden, Fixture("error-api-key-invalid")));

        var result = await fdc.Gateway().GetFoodAsync("789890", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.CredentialRejected, result.Outcome);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task A_throttle_with_no_stated_wait_blocks_every_read_for_an_hour_without_sending()
    {
        var clock = new ManualClock(Now);
        var throttle = new UsdaFdcThrottle(clock);
        var fdc = new StubFdc(Status(HttpStatusCode.TooManyRequests, Fixture("error-over-rate-limit")), Ok("foundation-flour-789890"));

        var first = await fdc.Gateway(throttle: throttle, clock: clock).GetFoodAsync("789890", Ct);
        Assert.Equal(ReferenceEvidenceOutcome.Throttled, first.Outcome);
        Assert.Equal(TimeSpan.FromHours(1), first.RetryAfter);
        Assert.Single(fdc.Requests);

        // A second gateway — another request scope — shares the block, and neither a fetch nor a search goes out.
        clock.Advance(TimeSpan.FromMinutes(59));
        var second = fdc.Gateway(throttle: throttle, clock: clock);
        Assert.Equal(ReferenceEvidenceOutcome.Throttled, (await second.GetFoodAsync("789890", Ct)).Outcome);
        var search = await second.SearchFoodsAsync(Search("flour"), Ct);
        Assert.Equal(ReferenceEvidenceOutcome.Throttled, search.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), search.RetryAfter);
        Assert.Single(fdc.Requests);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ReferenceEvidenceOutcome.Found, (await second.GetFoodAsync("789890", Ct)).Outcome);
        Assert.Equal(2, fdc.Requests.Count);
    }

    [Fact]
    public async Task A_throttle_that_states_its_wait_is_held_to_that_wait()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.TooManyRequests, Fixture("error-over-rate-limit"), retryAfterSeconds: 120));

        var result = await fdc.Gateway().GetFoodAsync("789890", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Throttled, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task A_retry_is_not_sent_into_a_block_another_request_started_while_it_waited()
    {
        var clock = new ManualClock(Now);
        var throttle = new UsdaFdcThrottle(clock);

        // The first attempt fails transiently; while this read waits to retry, another one is throttled.
        var fdc = new StubFdc(Status(HttpStatusCode.ServiceUnavailable) with { OnSend = () => throttle.Block(TimeSpan.FromMinutes(30)) });

        var result = await fdc.Gateway(throttle: throttle, clock: clock).GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Throttled, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(30), result.RetryAfter);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task An_unauthorized_answer_is_a_refused_key()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.Unauthorized));

        Assert.Equal(ReferenceEvidenceOutcome.CredentialRejected, (await fdc.Gateway().GetFoodAsync("1", Ct)).Outcome);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_is_unreadable()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.Found));

        Assert.Equal(ReferenceEvidenceOutcome.Unreadable, (await fdc.Gateway().GetFoodAsync("1", Ct)).Outcome);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task A_food_with_no_nutrient_table_is_unreadable_rather_than_a_food_with_no_nutrients()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.OK,
            """{ "fdcId": 169761, "dataType": "SR Legacy", "description": "Wheat flour", "publicationDate": "4/1/2019" }"""));

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unreadable, result.Outcome);
        Assert.Null(result.Food);
    }

    [Fact]
    public async Task A_nutrient_in_a_unit_other_than_its_own_is_dropped_not_converted()
    {
        var body = """
            { "fdcId": 169761, "dataType": "SR Legacy", "description": "Wheat flour", "publicationDate": "4/1/2019",
              "foodNutrients": [
                { "nutrient": { "number": "208", "name": "Energy", "unitName": "kJ" }, "amount": 1523.0 },
                { "nutrient": { "number": "307", "name": "Sodium, Na", "unitName": "g" }, "amount": 0.002 },
                { "nutrient": { "number": "203", "name": "Protein", "unitName": "G" }, "amount": 10.33 },
                { "nutrient": { "number": "951", "name": "Proximates", "unitName": "g" } }
              ] }
            """;
        var fdc = new StubFdc(Status(HttpStatusCode.OK, body));

        var food = (await fdc.Gateway().GetFoodAsync("169761", Ct)).Food!;

        Assert.Equal([new NutrientEvidence(NutrientCode.Protein, 10.33m, NutrientUnit.Gram, "Protein", null)], food.Nutrients);
    }

    [Fact]
    public async Task An_oversized_answer_with_no_stated_length_is_unreadable()
    {
        var fdc = new StubFdc(new Scripted(HttpStatusCode.OK, "{\"pad\":\"" + new string('x', 5 * 1024 * 1024) + "\"}", Unsized: true));

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unreadable, result.Outcome);
    }

    [Fact]
    public async Task A_server_failure_is_retried_and_a_recovery_is_found()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.ServiceUnavailable), Ok("sr-legacy-flour-169761"));

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Found, result.Outcome);
        Assert.Equal(2, fdc.Requests.Count);
    }

    [Fact]
    public async Task Three_server_failures_are_unavailable_and_no_fourth_is_sent()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.BadGateway), Scripted.Throws, Status(HttpStatusCode.InternalServerError));

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unavailable, result.Outcome);
        Assert.Equal(3, fdc.Requests.Count);
    }

    [Fact]
    public async Task A_source_that_never_answers_is_unavailable_within_its_attempt_bounds()
    {
        var fdc = new StubFdc(Scripted.Hangs, Scripted.Hangs, Scripted.Hangs);

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unavailable, result.Outcome);
        Assert.Equal(3, fdc.Requests.Count);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "description": "no id" }""")]
    [InlineData("""{ "fdcId": "169761", "dataType": "SR Legacy", "description": "id as text" }""")]
    public async Task An_answer_that_cannot_be_read_is_unreadable_and_not_retried(string body)
    {
        var fdc = new StubFdc(Status(HttpStatusCode.OK, body));

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unreadable, result.Outcome);
        Assert.Single(fdc.Requests);
    }

    [Fact]
    public async Task A_search_answer_with_no_foods_array_is_unreadable()
    {
        var fdc = new StubFdc(Status(HttpStatusCode.OK, """{ "totalHits": 0 }"""));

        var result = await fdc.Gateway().SearchFoodsAsync(Search("flour"), Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unreadable, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task An_oversized_answer_is_unreadable()
    {
        var huge = "{\"pad\":\"" + new string('x', 5 * 1024 * 1024) + "\"}";
        var fdc = new StubFdc(Status(HttpStatusCode.OK, huge));

        var result = await fdc.Gateway().GetFoodAsync("169761", Ct);

        Assert.Equal(ReferenceEvidenceOutcome.Unreadable, result.Outcome);
    }

    [Fact]
    public async Task A_caller_cancelling_is_the_one_exception_that_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var fdc = new StubFdc(Scripted.Hangs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fdc.Gateway().GetFoodAsync("169761", cancelled.Token));
    }

    // ---- Coverage and the missing source ----

    [Theory]
    [InlineData(ReferenceEvidenceKind.Nutrition, EvidenceCoverageStatus.Available)]
    [InlineData(ReferenceEvidenceKind.Density, EvidenceCoverageStatus.Available)]
    [InlineData(ReferenceEvidenceKind.Allergen, EvidenceCoverageStatus.NoApprovedSource)]
    [InlineData(ReferenceEvidenceKind.Dietary, EvidenceCoverageStatus.NoApprovedSource)]
    [InlineData(ReferenceEvidenceKind.FoodSafety, EvidenceCoverageStatus.NoApprovedSource)]
    public void A_configured_host_covers_only_what_fdc_is_approved_for(ReferenceEvidenceKind kind, EvidenceCoverageStatus status)
    {
        var coverage = new StubFdc().Gateway().Describe(kind);

        Assert.Equal(status, coverage.Status);
        Assert.Equal(status is EvidenceCoverageStatus.Available ? "usda-fdc" : null, coverage.SourceCode);
    }

    [Theory]
    [InlineData(ReferenceEvidenceKind.Nutrition, EvidenceCoverageStatus.NotConfigured)]
    [InlineData(ReferenceEvidenceKind.Density, EvidenceCoverageStatus.NotConfigured)]
    [InlineData(ReferenceEvidenceKind.Allergen, EvidenceCoverageStatus.NoApprovedSource)]
    [InlineData(ReferenceEvidenceKind.Dietary, EvidenceCoverageStatus.NoApprovedSource)]
    [InlineData(ReferenceEvidenceKind.FoodSafety, EvidenceCoverageStatus.NoApprovedSource)]
    public void An_unconfigured_host_tells_not_configured_apart_from_no_approved_source(ReferenceEvidenceKind kind, EvidenceCoverageStatus status)
    {
        Assert.Equal(status, new UnconfiguredReferenceEvidenceGateway().Describe(kind).Status);
    }

    [Fact]
    public async Task An_unconfigured_host_reads_not_configured_and_calls_nothing()
    {
        var gateway = new UnconfiguredReferenceEvidenceGateway();

        Assert.Equal(ReferenceEvidenceOutcome.NotConfigured, (await gateway.GetFoodAsync("789890", Ct)).Outcome);
        Assert.Equal(ReferenceEvidenceOutcome.NotConfigured, (await gateway.SearchFoodsAsync(Search("flour"), Ct)).Outcome);
    }

    [Fact]
    public void Every_approved_source_has_a_licence_a_citation_and_at_least_one_kind()
    {
        Assert.All(EvidenceSourceCatalog.All, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.LicenseCode));
            Assert.StartsWith("https://", source.LicenseUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(source.Citation));
            Assert.NotEmpty(source.Covers);
            Assert.NotEmpty(source.DataSets);
            Assert.NotEqual(ReferenceSourceKind.AiEstimated, source.Kind);
            Assert.NotEqual(ReferenceSourceKind.CommunityContributed, source.Kind);
        });
    }

    // ---- What reaches a log ----

    [Fact]
    public async Task No_key_search_term_or_provider_message_reaches_a_log()
    {
        var logs = new CapturingLoggerProvider();
        var fdc = new StubFdc(
            Ok("search-flour"),
            Status(HttpStatusCode.Forbidden, Fixture("error-api-key-invalid")),
            Status(HttpStatusCode.ServiceUnavailable, "PROVIDER_SAID_THIS_VERBATIM"),
            Status(HttpStatusCode.ServiceUnavailable, "PROVIDER_SAID_THIS_VERBATIM"),
            Status(HttpStatusCode.ServiceUnavailable, "PROVIDER_SAID_THIS_VERBATIM"),
            Status(HttpStatusCode.TooManyRequests, Fixture("error-over-rate-limit")));
        var gateway = fdc.Gateway(logs);

        await gateway.SearchFoodsAsync(Search("Secret Family Flour Blend"), Ct);
        await gateway.GetFoodAsync("789890", Ct);
        await gateway.GetFoodAsync("789890", Ct);
        await gateway.GetFoodAsync("789890", Ct);

        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(ApiKey, entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret Family", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PROVIDER_SAID", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("invalid api_key", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exceeded your rate limit", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api.fdc.example", entry, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- Options ----

    [Theory]
    [InlineData("Endpoint=https://api.nal.usda.gov/fdc/v1;Key=abc", "https://api.nal.usda.gov/fdc/v1/")]
    [InlineData("endpoint=https://api.nal.usda.gov/fdc/v1/ ; key = abc", "https://api.nal.usda.gov/fdc/v1/")]
    public void A_whole_connection_string_parses_with_its_endpoint_ending_in_a_slash(string connectionString, string endpoint)
    {
        var options = UsdaFdcEvidenceOptions.Parse(connectionString);

        Assert.NotNull(options);
        Assert.Equal(endpoint, options.Endpoint.ToString());
        Assert.Equal("abc", options.ApiKey);
        Assert.DoesNotContain("abc", options.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Endpoint=https://api.nal.usda.gov/fdc/v1")]
    [InlineData("Key=abc")]
    [InlineData("Endpoint=https://api.nal.usda.gov/fdc/v1;Key=")]
    [InlineData("Endpoint=http://api.nal.usda.gov/fdc/v1;Key=abc")]
    [InlineData("Endpoint=https://api.nal.usda.gov/fdc/v1?api_key=abc;Key=abc")]
    [InlineData("Endpoint=not a url;Key=abc")]
    public void A_missing_or_unusable_connection_string_is_no_provider(string? connectionString)
    {
        Assert.Null(UsdaFdcEvidenceOptions.Parse(connectionString));
    }

    // ---- Helpers ----

    private static ReferenceFoodSearch Search(string canonicalName) =>
        ReferenceFoodSearch.ForIngredient(new Ingredient { CanonicalName = canonicalName });

    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <param name="OnSend">Runs as the request arrives — what else happened in the process meanwhile.</param>
    /// <param name="Unsized">Sent with no Content-Length, as a chunked answer is.</param>
    private sealed record Scripted(
        HttpStatusCode Status,
        string Body,
        int? RetryAfterSeconds = null,
        bool Fails = false,
        bool Never = false,
        Action? OnSend = null,
        bool Unsized = false)
    {
        /// <summary>The connection fails before any answer.</summary>
        public static readonly Scripted Throws = new(0, string.Empty, Fails: true);

        /// <summary>No answer until the attempt gives up.</summary>
        public static readonly Scripted Hangs = new(0, string.Empty, Never: true);
    }

    private sealed record Sent(HttpMethod Method, string Uri, string? ApiKeyHeader);

    /// <summary>A body whose length is not known until it has all arrived.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>FoodData Central, as a queue of recorded responses.</summary>
    private sealed class StubFdc(params Scripted[] script) : HttpMessageHandler
    {
        private readonly Queue<Scripted> _script = new(script);

        public List<Sent> Requests { get; } = [];

        public UsdaFdcEvidenceGateway Gateway(
            CapturingLoggerProvider? logs = null, UsdaFdcThrottle? throttle = null, IClock? clock = null)
        {
            clock ??= new ManualClock(Now);

            // The header exactly as the host's registration sets it.
            var http = new HttpClient(this, disposeHandler: false);
            http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            return new UsdaFdcEvidenceGateway(
                http,
                new UsdaFdcEvidenceOptions(new Uri("https://api.fdc.example/fdc/v1/"), ApiKey)
                {
                    AttemptTimeout = TimeSpan.FromMilliseconds(150),
                    RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                },
                throttle ?? new UsdaFdcThrottle(clock),
                clock,
                (logs is null ? LoggerFactory.Create(_ => { }) : LoggerFactory.Create(builder => builder.AddProvider(logs)))
                    .CreateLogger<UsdaFdcEvidenceGateway>());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null));

            var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("The gateway asked for more than was scripted.");

            next.OnSend?.Invoke();

            if (next.Fails)
            {
                throw new HttpRequestException("No such host is known. (api.fdc.example:443)");
            }

            if (next.Never)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            HttpContent content = next.Unsized
                ? new UnsizedContent(Encoding.UTF8.GetBytes(next.Body))
                : new StringContent(next.Body, Encoding.UTF8, "application/json");
            var response = new HttpResponseMessage(next.Status) { Content = content };
            if (next.RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
