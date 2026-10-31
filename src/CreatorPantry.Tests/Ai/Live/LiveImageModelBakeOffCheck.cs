using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Ai.Live;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// The image model bake-off (AP.3.3, PRICE-006): twenty fixed food prompts through every candidate model and
/// setting, through the shipped <see cref="VeniceImageGenerator"/>, with a report a person reads to choose what
/// each quality class sends. Opt-in twice over and never part of CI.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It spends real money</strong>, so it needs two things set, and is skipped with the estimate as its
/// reason until both are: <c>CP_LIVE_IMAGES</c>, an images connection string (<c>Endpoint=…;Key=…;Model=…</c>),
/// and <c>CP_LIVE_BAKEOFF_APPROVE</c> holding the estimated total in dollars exactly as the skip prints it
/// (<c>4.40</c>). The approval is the figure rather than a yes so that one given for this set is not spent on a
/// larger one: add a prompt or a candidate and the old figure no longer matches.
/// </para>
/// <para>
/// <strong>The estimate is the product's own price table</strong>, <c>Ai:Cost:Images</c> in the API's
/// <c>appsettings.json</c>, at the row each candidate would be charged at — so a price change there moves the
/// estimate here. The one exception is <c>flux-2-pro</c>, which no class sends to and so has neither a sizing
/// profile nor a price in the shipped tables; this check supplies both, from Venice's own
/// <c>GET /models?type=image</c> read 2026-10-27, in <see cref="FluxProfile"/>. Should it be chosen, those rows go
/// into both hosts' tables, and its 3,000-character prompt limit means AP.3.2's compaction will be offered for
/// any composed prompt longer than that.
/// </para>
/// <para>
/// <strong>Nothing goes to storage.</strong> Each picture is written to a local folder — <c>CP_LIVE_BAKEOFF_OUT</c>,
/// or a fresh one under the system temp folder — beside <c>report.md</c>, <c>index.html</c> (every prompt's
/// pictures side by side) and <c>calls.csv</c>. No workspace, database or creator content is involved.
/// </para>
/// <para>
/// <strong>What counts as what.</strong> A picture Venice flagged is <em>withheld</em>, as the generator reports it
/// to Media; any other 4xx is <em>refused</em>. Both are refusals in the report, and every prompt here is ordinary
/// food content, so each one is a false refusal a creator would have met. A 429 is retried twice before it is
/// counted, since nothing is spent on one. Latency is the generator call's wall time, for made pictures only, as
/// p50 and p95 by nearest rank. Candidates run side by side, each one's prompts in turn, so a slow model does not
/// hold up the others' timings. Spend counts withheld pictures too, as an upper bound: whether Venice bills a
/// flagged picture is not documented.
/// </para>
/// <para>
/// Each picture is read by <see cref="GeneratedImageInspector"/>, as staging reads it, and saved under the type its
/// bytes are: <c>seedream-v5-lite</c> and <c>flux-2-pro</c> answer the request's <c>png</c> with a JPEG (run of
/// 2026-10-27), which staging accepts. One staging would refuse is <em>unstageable</em> — paid for and never shown.
/// What it asserts is only that the run is complete — every call ended in one of those outcomes and every made
/// picture was saved. How each model does is the report's to say and the owner's to judge.
/// </para>
/// </remarks>
public sealed class LiveImageModelBakeOffCheck
{
    private const string ConnectionVariable = "CP_LIVE_IMAGES";
    private const string ApproveVariable = "CP_LIVE_BAKEOFF_APPROVE";
    private const string OutputVariable = "CP_LIVE_BAKEOFF_OUT";

    private static readonly TimeSpan[] RateLimitBackoff = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every model and setting a class might send, at each model's default 1:1 square.</summary>
    private static readonly Candidate[] Candidates =
    [
        new("z-image-turbo", Resolution: null, Quality: null),
        new("qwen-image-2-1-turbo", Resolution: "1K", Quality: null),
        new("seedream-v5-lite", Resolution: null, Quality: null),
        new("flux-2-pro", Resolution: null, Quality: null),
        new("gpt-image-2-5-flare", Resolution: "1K", Quality: "medium"),
        new("gpt-image-2-5-flare", Resolution: "1K", Quality: "high"),
    ];

    /// <summary>
    /// <c>flux-2-pro</c> as Venice describes it — an aspect ratio alone, $0.03 whatever the ratio, 3,000 prompt
    /// characters — with its credit rate at the pinned 1,250 a dollar, which <c>RequireImageRates</c> wants beside
    /// any profile. This check's only, until the owner chooses it.
    /// </summary>
    private static readonly Dictionary<string, string?> FluxProfile = new()
    {
        ["Venice:Models:flux-2-pro:Sizing"] = "AspectRatio",
        ["Venice:Models:flux-2-pro:AspectRatios:0"] = "1:1",
        ["Venice:Models:flux-2-pro:AspectRatios:1"] = "3:2",
        ["Venice:Models:flux-2-pro:AspectRatios:2"] = "16:9",
        ["Venice:Models:flux-2-pro:AspectRatios:3"] = "21:9",
        ["Venice:Models:flux-2-pro:AspectRatios:4"] = "9:16",
        ["Venice:Models:flux-2-pro:AspectRatios:5"] = "2:3",
        ["Venice:Models:flux-2-pro:AspectRatios:6"] = "3:4",
        ["Venice:Models:flux-2-pro:AspectRatios:7"] = "4:5",
        ["Venice:Models:flux-2-pro:DefaultAspectRatio"] = "1:1",
        ["Venice:Models:flux-2-pro:PromptCharacterLimit"] = "3000",
        ["Ai:Cost:Images:flux-2-pro"] = "0.03",
        ["Ai:Quota:ImageRates:flux-2-pro"] = "37.5",
    };

    /// <summary>
    /// What a food creator asks for, worded the way IMG-002's prompts are, each chosen to show one thing a class's
    /// model has to get right. All ordinary food content: raw meat, a knife and a whole fish are here because a
    /// recipe blog shows them, and a model that flags them is refusing a creator's real work.
    /// </summary>
    private static readonly BakeOffPrompt[] Prompts =
    [
        new("sourdough-overhead", "baseline: a plain overhead hero shot",
            "A rustic round sourdough loaf with a deep golden, blistered crust and one curved ear, on a worn oak bread "
            + "board. Shot directly overhead. Soft window light from the left, gentle shadows falling right. A folded "
            + "linen towel at one corner of the board and a light dusting of flour. Honest, unstyled home baking.",
            "text, logos, cutlery"),
        new("ramen-steam", "steam, a clear broth, chopsticks held above the bowl",
            "A deep bowl of shoyu ramen at a 45-degree angle: clear amber broth, wavy noodles lifted on a pair of wooden "
            + "chopsticks above the bowl, a halved soft-boiled egg with a jammy orange yolk, two slices of chashu, "
            + "sliced spring onion and a sheet of nori. Wisps of steam rising against a dark background. Warm, low light."),
        new("layer-cake-slice", "counting: exactly four sponge layers in the cut face",
            "A single slice of chocolate layer cake on a white plate, shot at eye level so the cut face shows exactly "
            + "four dark sponge layers separated by three thin layers of whipped vanilla cream, with a glossy ganache "
            + "drip down the back. Plain pale grey backdrop, soft diffused light, shallow depth of field."),
        new("raw-ribeye", "raw meat as a recipe shows it before cooking",
            "Two raw bone-in ribeye steaks on brown butcher paper, well marbled, deep red with creamy fat, seasoned with "
            + "flaky sea salt and cracked black pepper. Sprigs of rosemary, three unpeeled garlic cloves and a small "
            + "bowl of olive oil beside them. Overhead view, cool daylight, a dark slate surface around the paper."),
        new("hands-kneading", "hands: two of them, five fingers each, at work",
            "Two hands kneading a ball of pale bread dough on a floured wooden counter, the heels of the palms pressing "
            + "the dough forward. Flour on the fingers and wrists, sleeves rolled up. Seen from slightly above and in "
            + "front. Morning window light, warm tones, a bowl and a bag of flour softly out of focus behind."),
        new("syrup-pour", "motion: a liquid caught mid-pour",
            "A tall stack of five fluffy buttermilk pancakes on a speckled ceramic plate, a pat of butter melting on top, "
            + "with maple syrup caught mid-pour in a thin glossy ribbon from a small glass jug above. Fresh blueberries "
            + "scattered around the plate. Three-quarter view, bright natural light, a light linen tablecloth."),
        new("egg-count", "counting: three eggs, one cracked",
            "Exactly three brown eggs in a small matte white ceramic bowl, and beside the bowl one more egg cracked open "
            + "into a saucer with its bright yolk intact and the two shell halves next to it. A wooden whisk lying "
            + "across the frame. Overhead, soft even light, a pale oak table."),
        new("iced-coffee", "transparency: glass, ice and condensation, backlit",
            "A tall clear glass of iced coffee with milk swirling into the dark coffee in soft clouds, large clear ice "
            + "cubes, and beads of condensation running down the outside of the glass. Backlit by a bright window so "
            + "the glass glows. A paper straw. Side view at table height, a blurred cafe interior behind."),
        new("mezze-spread", "a busy table: many dishes, still legible",
            "An overhead mezze spread for six on a long wooden table: a bowl of hummus with a pool of olive oil and "
            + "paprika, baba ganoush, tabbouleh, stuffed vine leaves, a plate of falafel, pickled turnips, olives, warm "
            + "pita in a basket, and small bowls of za'atar and labneh. Plates and linen napkins around the edge. "
            + "Bright, even daylight, every dish clearly readable."),
        new("kimchi-label", "text: one short word written on a label",
            "A large glass jar of homemade kimchi on a kitchen shelf, red napa cabbage pressed against the glass, a "
            + "brown paper label tied around the neck with string, the word KIMCHI handwritten on it in black ink. "
            + "Side view, soft side light, two smaller jars and a ceramic crock out of focus beside it."),
        new("chalkboard-menu", "text: a short sentence, spelled right",
            "A small cafe chalkboard propped on a counter next to a pot of soup and a bread basket, with the words "
            + "\"Soup of the Day: Roasted Tomato\" written on it in neat white chalk lettering. Eye-level view, warm "
            + "afternoon light, a steaming bowl of tomato soup in the foreground."),
        new("figs-moody", "dark, low-key still life with deep shadows",
            "A dark, moody still life: halved purple figs with their pink centres showing, a wedge of blue cheese, a "
            + "honeycomb dripping onto a small plate, and a sprig of thyme, on a black wooden board against a black "
            + "background. A single soft light from the upper left, deep shadows, rich colour, painterly mood."),
        new("lemon-tart-airy", "bright, high-key and airy",
            "A lemon tart with a pale golden shortcrust and a smooth glossy yellow curd, one slice cut out and set beside "
            + "it, on white marble. Thin lemon slices and a small bowl of whipped cream. Bright, airy, high-key light "
            + "with soft shadows, a three-quarter view, a pale blue linen napkin."),
        new("burger-cross-section", "structure: a stacked food seen from the side",
            "A double cheeseburger at eye level on a wooden board: a toasted sesame brioche bun, two seared beef "
            + "patties with cheddar melting down their sides, crisp lettuce, a slice of tomato, pickles and red onion, "
            + "each layer clearly visible. A few fries and a small pot of ketchup beside it. Warm light, dark background."),
        new("nigiri-detail", "fine detail: rice grains and fish texture close up",
            "Six pieces of salmon nigiri in a neat row on a dark slate plate, close up: glistening orange salmon with "
            + "clear white fat lines over hand-pressed rice where the individual grains are visible. A small mound of "
            + "pickled ginger and a dab of wasabi at one end. Low side light, shallow depth of field."),
        new("lattice-pie", "geometry: a regular woven lattice",
            "A cherry pie seen directly from above, its golden lattice crust woven from eight even strips, four each "
            + "way, with dark red filling bubbling through the gaps and a crimped edge. Sanding sugar sparkling on top. "
            + "A blue-and-white checked cloth under the pie dish, soft daylight."),
        new("knife-onions", "a knife in use, as every method step shows one",
            "A chef's knife mid-cut through a halved yellow onion on a wooden cutting board, a neat pile of finely diced "
            + "onion beside it and a whole onion and its papery skins at the edge. The hand holding the onion has its "
            + "fingertips curled under. Three-quarter view from above, bright kitchen light."),
        new("whole-fish", "a whole animal: a fish with its head on",
            "A whole grilled sea bream on an oval platter, head and tail on, crisp charred skin with diagonal score "
            + "marks, stuffed with lemon slices and parsley, a lemon wedge and a sprig of dill beside it. Overhead view "
            + "on a weathered blue wooden table, bright midday light, a small bowl of olive oil and flaky salt."),
        new("buddha-bowl-avoid", "the avoid list: no meat, egg, cheese, cutlery or hands",
            "A vegan buddha bowl: brown rice, roasted sweet potato cubes, crispy chickpeas, shredded red cabbage, sliced "
            + "avocado, edamame and a drizzle of tahini dressing with toasted sesame seeds. Overhead view on a pale "
            + "terracotta plate, bright natural light, a few lime wedges beside the bowl.",
            "meat, fish, egg, cheese, cutlery, hands, text"),
        new("long-composed", "a long composed prompt with a brand's standing guidance: does detail survive",
            "A hero image for a recipe post about a summer peach and burrata salad, for a food blog whose look is "
            + "bright, natural and generous. The dish: a wide, shallow, hand-thrown stoneware platter in a matte "
            + "speckled oatmeal glaze, holding thick wedges of ripe yellow peach with their blush-red skins on, torn "
            + "pieces of burrata with the creamy centre spilling out, thin ribbons of prosciutto folded loosely between "
            + "them, a scattering of torn basil and mint leaves, a handful of toasted hazelnuts roughly chopped, flakes "
            + "of sea salt, cracked black pepper, and a thin drizzle of dark balsamic glaze in a loose zigzag across the "
            + "top. A small glass jug of golden olive oil and a half peach with its stone still in sit at the upper right "
            + "of the platter. The setting: a weathered pale wooden garden table outdoors in late afternoon, dappled "
            + "light filtering through vine leaves above so soft shadows of leaves fall across part of the table but "
            + "never across the burrata. A crumpled sage-green linen napkin at the lower left corner and two mismatched "
            + "vintage forks laid on it, their tines pointing towards the platter. A glass of chilled rosé with "
            + "condensation on the bowl at the top edge, cropped by the frame. The camera: a three-quarter view from "
            + "about 45 degrees above, the platter filling about two thirds of the frame, slightly off centre to the "
            + "left, with natural depth of field so the front edge of the platter and the burrata are sharp and the "
            + "wine glass falls softly out of focus. The style, from the blog's standing guidance: colours true to life "
            + "rather than heavily saturated, warm whites, no harsh flash, no dramatic filters, generous portions that "
            + "look abundant but not staged, real crumbs and drips left where they fall, and props that are old, "
            + "handmade or natural rather than new and glossy. The mood is relaxed and inviting, like the moment just "
            + "before guests sit down at a summer lunch.",
            "text, logos, watermarks, people, faces, plastic, harsh shadows across the cheese, a white background"),
    ];

    [Fact]
    public async Task Every_candidate_model_makes_the_fixed_prompt_set_and_the_report_compares_them()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddJsonFile(Path.Combine(RepositoryRoot(CallerPath()), "src/CreatorPantry.ApiService/appsettings.json"));
        builder.Configuration.AddInMemoryCollection(FluxProfile);

        var prices = Priced(builder.Configuration);
        RequireEveryPromptFits(builder.Configuration);

        var estimate = prices.Values.Sum() * Prompts.Length;
        var figure = estimate.ToString("0.00", CultureInfo.InvariantCulture);
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);

        if (string.IsNullOrWhiteSpace(connection)
            || !string.Equals(Environment.GetEnvironmentVariable(ApproveVariable)?.Trim(), figure, StringComparison.Ordinal))
        {
            Assert.Skip(
                $"Estimated total ${figure}: {Prompts.Length} prompts x {Candidates.Length} candidates = "
                + $"{Prompts.Length * Candidates.Length} pictures ("
                + string.Join(", ", Candidates.Select(candidate =>
                    $"{candidate.Label} ${prices[candidate].ToString("0.00", CultureInfo.InvariantCulture)}"))
                + $"). To spend it, set {ConnectionVariable} to an images connection string and "
                + $"{ApproveVariable}={figure}.");
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{AiModelConnections.Images}"] = connection,
        });
        builder.AddCreatorPantryAi();
        using var host = builder.Build();

        var folder = Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } named
            ? Path.GetFullPath(named)
            : Path.Combine(
                Path.GetTempPath(),
                "creatorpantry-image-bakeoff",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        foreach (var candidate in Candidates)
        {
            Directory.CreateDirectory(Path.Combine(folder, candidate.Slug));
        }

        var started = DateTimeOffset.UtcNow;
        var runs = await Task.WhenAll(Candidates.Select(candidate => RunAsync(host, candidate, folder)));
        var calls = runs.SelectMany(run => run).ToList();

        await File.WriteAllTextAsync(
            Path.Combine(folder, "report.md"), Report(calls, prices, Credits(builder.Configuration), started, figure), Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), SideBySide(calls), Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "calls.csv"), Csv(calls), Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"Bake-off report: {Path.Combine(folder, "report.md")}");

        Assert.Equal(Prompts.Length * Candidates.Length, calls.Count);

        foreach (var made in calls.Where(call => call.Outcome == Outcome.Made))
        {
            Assert.True(File.Exists(Path.Combine(folder, made.File!)), made.File);
        }
    }

    /// <summary>One candidate's twenty pictures, in prompt order.</summary>
    private static async Task<List<Call>> RunAsync(IHost host, Candidate candidate, string folder)
    {
        var generator = host.Services.GetRequiredService<IImageGenerator>();
        var calls = new List<Call>();

        foreach (var (prompt, index) in Prompts.Select((prompt, index) => (prompt, index + 1)))
        {
            var options = new ImageGenerationOptions { ModelId = candidate.Model, Count = 1, AdditionalProperties = [] };

            if (candidate.Quality is { } quality)
            {
                options.AdditionalProperties[ImageGenerationSettings.Quality] = quality;
            }

            if (candidate.Resolution is { } resolution)
            {
                options.AdditionalProperties[ImageGenerationSettings.Resolution] = resolution;
            }

            var request = new ImageGenerationRequest(GeneratedImagePromptComposition.Compose(prompt.Text, prompt.Avoid));
            Call call;

            for (var attempt = 0; ; attempt++)
            {
                call = await AttemptAsync(generator, request, options, candidate, prompt, index, folder);

                if (call.Outcome != Outcome.RateLimited || attempt >= RateLimitBackoff.Length)
                {
                    break;
                }

                await Task.Delay(RateLimitBackoff[attempt], Ct);
            }

            calls.Add(call);
        }

        return calls;
    }

    private static async Task<Call> AttemptAsync(
        IImageGenerator generator,
        ImageGenerationRequest request,
        ImageGenerationOptions options,
        Candidate candidate,
        BakeOffPrompt prompt,
        int index,
        string folder)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            var response = await generator.GenerateAsync(request, options, Ct);
            clock.Stop();

            if (response.Contents.OfType<DataContent>().FirstOrDefault() is not { } image)
            {
                return new Call(candidate, prompt, index, Outcome.Empty, null, clock.Elapsed, null, null);
            }

            // Read as staging reads it, from the bytes: Venice answers some models' "png" with a JPEG, which is
            // stored as what it is. A picture staging would refuse is one a creator paid for and never got.
            var inspection = GeneratedImageInspector.Inspect(image.Data.Span);
            var extension = GeneratedImageInspector.CanonicalExtension(inspection.MediaType) ?? "bin";
            var file = Path.Combine(candidate.Slug, $"{index:00}-{prompt.Id}.{extension}");
            await File.WriteAllBytesAsync(Path.Combine(folder, file), image.Data.ToArray(), Ct);

            return new Call(
                candidate,
                prompt,
                index,
                inspection.Outcome == GeneratedImageInspectionOutcome.Accepted ? Outcome.Made : Outcome.Unstageable,
                null,
                clock.Elapsed,
                file,
                ImageGenerationSettings.Read(image.AdditionalProperties, ImageGenerationSettings.SizeSent),
                inspection.MediaType,
                inspection.Width,
                inspection.Height);
        }
        catch (ImageWithheldException)
        {
            return new Call(candidate, prompt, index, Outcome.Withheld, null, clock.Elapsed, null, null);
        }
        catch (HttpRequestException failure)
        {
            var outcome = failure.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => Outcome.RateLimited,
                HttpStatusCode.RequestTimeout => Outcome.Unavailable,
                { } status when (int)status is >= 400 and < 500 => Outcome.Refused,
                _ => Outcome.Unavailable,
            };

            return new Call(candidate, prompt, index, outcome, (int?)failure.StatusCode, clock.Elapsed, null, null);
        }
        catch (TaskCanceledException) when (!Ct.IsCancellationRequested)
        {
            // The image client's own five-minute timeout, not the test being stopped.
            return new Call(candidate, prompt, index, Outcome.Unavailable, null, clock.Elapsed, null, null);
        }
    }

    /// <summary>Each candidate's list price a picture, from the row it would be charged at; fails when one has none.</summary>
    private static Dictionary<Candidate, decimal> Priced(IConfiguration configuration)
    {
        var cost = configuration.GetSection(AiCostOptions.SectionName).Get<AiCostOptions>() ?? new AiCostOptions();
        var quota = configuration.GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>() ?? new AiQuotaOptions();

        foreach (var candidate in Candidates)
        {
            Assert.True(
                AiImageRateKeys.Figure(quota.ImageRates, candidate.Model, candidate.Resolution, candidate.Quality) is not null,
                $"{candidate.Label} has no row in Ai:Quota:ImageRates.");
        }

        return Candidates.ToDictionary(
            candidate => candidate,
            candidate => AiImageRateKeys.Figure(cost.Images, candidate.Model, candidate.Resolution, candidate.Quality)
                ?? throw new InvalidOperationException($"{candidate.Label} has no price in Ai:Cost:Images, so it cannot be estimated."));
    }

    /// <summary>Each candidate's credits a picture, the row a class sending it would be held and charged at.</summary>
    private static Dictionary<Candidate, decimal> Credits(IConfiguration configuration)
    {
        var quota = configuration.GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>() ?? new AiQuotaOptions();

        return Candidates.ToDictionary(
            candidate => candidate,
            candidate => AiImageRateKeys.Figure(quota.ImageRates, candidate.Model, candidate.Resolution, candidate.Quality)!.Value);
    }

    /// <summary>
    /// Every prompt as sent, against every candidate's own limit, before anything is spent: a prompt the generator
    /// would refuse unsent would read in the report as the model's refusal.
    /// </summary>
    private static void RequireEveryPromptFits(IConfiguration configuration)
    {
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        configuration.GetSection(VeniceModelProfile.SectionName).Bind(profiles);

        foreach (var candidate in Candidates)
        {
            Assert.True(profiles.TryGetValue(candidate.Model, out var profile), $"{candidate.Model} has no sizing profile.");

            foreach (var prompt in Prompts)
            {
                var length = GeneratedImagePromptComposition.Compose(prompt.Text, prompt.Avoid).Length;
                Assert.True(
                    length <= profile.PromptCharacterLimit,
                    $"{prompt.Id} is {length} characters, over {candidate.Model}'s {profile.PromptCharacterLimit}.");
            }
        }
    }

    private static string Report(
        List<Call> calls,
        Dictionary<Candidate, decimal> prices,
        Dictionary<Candidate, decimal> credits,
        DateTimeOffset started,
        string estimate)
    {
        var report = new StringBuilder();
        var spent = 0m;

        report.AppendLine("# Image model bake-off (AP.3.3)");
        report.AppendLine();
        report.AppendLine($"Run {started:yyyy-MM-dd HH:mm} UTC, {Prompts.Length} prompts x {Candidates.Length} candidates, "
            + $"each at its default 1:1. Estimated ${estimate}. Prices are `Ai:Cost:Images` (flux-2-pro from Venice's catalogue).");
        report.AppendLine();
        report.AppendLine("| Candidate | Made | Withheld (flagged) | Refused (4xx) | Rate-limited | Unavailable / empty / unstageable | Format | p50 s | p95 s | $ / picture | Credits / picture | Spent (upper bound) |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|");

        foreach (var candidate in Candidates)
        {
            var mine = calls.Where(call => call.Candidate == candidate).ToList();
            var seconds = mine.Where(call => call.Outcome == Outcome.Made)
                .Select(call => call.Elapsed.TotalSeconds)
                .Order()
                .ToList();
            var paid = mine.Count(call =>
                call.Outcome is Outcome.Made or Outcome.Withheld or Outcome.Empty or Outcome.Unstageable);
            var cost = paid * prices[candidate];
            spent += cost;

            report.AppendLine(
                $"| {candidate.Label} | {Count(mine, Outcome.Made)} | {Count(mine, Outcome.Withheld)} | "
                + $"{Count(mine, Outcome.Refused)} | {Count(mine, Outcome.RateLimited)} | "
                + $"{Count(mine, Outcome.Unavailable) + Count(mine, Outcome.Empty) + Count(mine, Outcome.Unstageable)} | "
                + string.Join(", ", mine
                    .Where(call => call.MediaType is not null)
                    .Select(call => $"{call.MediaType} {call.Width}x{call.Height}")
                    .Distinct()) + " | "
                + $"{Percentile(seconds, 0.50)} | "
                + $"{Percentile(seconds, 0.95)} | {Money(prices[candidate])} | "
                + $"{credits[candidate].ToString("0.##", CultureInfo.InvariantCulture)} | {Money(cost)} |");
        }

        report.AppendLine();
        report.AppendLine($"Spent, at most: {Money(spent)}.");
        report.AppendLine();
        report.AppendLine("## Not made");
        report.AppendLine();

        var missing = calls.Where(call => call.Outcome != Outcome.Made).ToList();

        if (missing.Count == 0)
        {
            report.AppendLine("Every picture was made.");
        }
        else
        {
            report.AppendLine("| Candidate | Prompt | Outcome | Status | Seconds |");
            report.AppendLine("|---|---|---|---:|---:|");

            foreach (var call in missing)
            {
                report.AppendLine($"| {call.Candidate.Label} | {call.Index:00} {call.Prompt.Id} | {call.Outcome} | "
                    + $"{call.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {Seconds(call.Elapsed)} |");
            }
        }

        report.AppendLine();
        report.AppendLine("## Prompts");
        report.AppendLine();

        foreach (var (prompt, index) in Prompts.Select((prompt, index) => (prompt, index + 1)))
        {
            report.AppendLine($"{index:00}. **{prompt.Id}** — {prompt.Probes}");
        }

        report.AppendLine();
        report.AppendLine("Pictures side by side: `index.html` in this folder. Every call: `calls.csv`.");

        return report.ToString();

        static int Count(List<Call> calls, Outcome outcome) => calls.Count(call => call.Outcome == outcome);
    }

    /// <summary>One row per prompt, one column per candidate, the prompt as sent in the first column.</summary>
    private static string SideBySide(List<Call> calls)
    {
        var html = new StringBuilder();

        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Image model bake-off</title>");
        html.AppendLine("<style>body{font:14px/1.4 system-ui,sans-serif;margin:16px;background:#fff;color:#222}"
            + "table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:6px;vertical-align:top}"
            + "th{position:sticky;top:0;background:#f4f4f4}td.prompt{width:22rem;font-size:12px;white-space:pre-wrap}"
            + "img{width:256px;height:256px;object-fit:contain;display:block;cursor:zoom-in}"
            + ".none{width:256px;height:256px;display:grid;place-items:center;background:#f7f0f0;color:#844}"
            + "small{color:#666}</style></head><body>");
        html.AppendLine("<h1>Image model bake-off (AP.3.3)</h1><p>Select a picture to open it at full size.</p><table><thead><tr><th>Prompt</th>");

        foreach (var candidate in Candidates)
        {
            html.Append($"<th>{WebUtility.HtmlEncode(candidate.Label)}</th>");
        }

        html.AppendLine("</tr></thead><tbody>");

        foreach (var (prompt, index) in Prompts.Select((prompt, index) => (prompt, index + 1)))
        {
            var sent = GeneratedImagePromptComposition.Compose(prompt.Text, prompt.Avoid);
            html.Append($"<tr><td class=\"prompt\"><strong>{index:00} {WebUtility.HtmlEncode(prompt.Id)}</strong><br>"
                + $"<small>{WebUtility.HtmlEncode(prompt.Probes)}</small><br><br>{WebUtility.HtmlEncode(sent)}</td>");

            foreach (var candidate in Candidates)
            {
                var call = calls.Single(each => each.Candidate == candidate && each.Index == index);

                html.Append(call.File is { } file
                    ? $"<td><a href=\"{file.Replace('\\', '/')}\" target=\"_blank\"><img src=\"{file.Replace('\\', '/')}\" "
                        + $"alt=\"{WebUtility.HtmlEncode($"{candidate.Label}, prompt {index}")}\"></a>"
                        + $"<small>{Seconds(call.Elapsed)} s · {WebUtility.HtmlEncode(call.SizeSent ?? "")}</small></td>"
                    : $"<td><div class=\"none\">{call.Outcome}{(call.Status is { } status ? $" ({status})" : "")}</div></td>");
            }

            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table></body></html>");

        return html.ToString();
    }

    private static string Csv(List<Call> calls)
    {
        var csv = new StringBuilder("prompt,candidate,outcome,status,seconds,size_sent,media_type,width,height,file\n");

        foreach (var call in calls.OrderBy(call => call.Index).ThenBy(call => Array.IndexOf(Candidates, call.Candidate)))
        {
            csv.Append($"{call.Index:00}-{call.Prompt.Id},{call.Candidate.Label},{call.Outcome},")
                .Append($"{call.Status?.ToString(CultureInfo.InvariantCulture)},{Seconds(call.Elapsed)},")
                .Append($"{call.SizeSent},{call.MediaType},{call.Width},{call.Height},{call.File?.Replace('\\', '/')}\n");
        }

        return csv.ToString();
    }

    /// <summary>Nearest rank: the smallest value at least <paramref name="share"/> of the set is at or below.</summary>
    private static string Percentile(List<double> sorted, double share) =>
        sorted.Count == 0
            ? "-"
            : sorted[Math.Max(0, (int)Math.Ceiling(share * sorted.Count) - 1)].ToString("0.0", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Money(decimal dollars) => "$" + dollars.ToString("0.00", CultureInfo.InvariantCulture);

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Ai/Live/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", "..", ".."));

    private sealed record Candidate(string Model, string? Resolution, string? Quality)
    {
        public string Label => string.Join(" ", new[] { Model, Quality, Resolution }.Where(part => part is not null));

        public string Slug => AiImageRateKeys.Key(Model, Resolution, Quality).Replace(AiImageRateKeys.Separator, '-');
    }

    private enum Outcome
    {
        Made,
        Withheld,
        Refused,
        RateLimited,
        Unavailable,
        Empty,

        /// <summary>Bytes arrived that staging would refuse: paid for, never shown.</summary>
        Unstageable,
    }

    private sealed record Call(
        Candidate Candidate,
        BakeOffPrompt Prompt,
        int Index,
        Outcome Outcome,
        int? Status,
        TimeSpan Elapsed,
        string? File,
        string? SizeSent,
        string? MediaType = null,
        int? Width = null,
        int? Height = null);

    /// <summary>One fixed prompt: what it is there to show, the prompt, and the avoid list sent after it.</summary>
    private sealed record BakeOffPrompt(string Id, string Probes, string Text, string? Avoid = null);
}
