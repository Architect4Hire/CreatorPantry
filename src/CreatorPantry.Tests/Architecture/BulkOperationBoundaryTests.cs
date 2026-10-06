using System.Text.RegularExpressions;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// Keeps the EF escape hatches out of domain code, because each of them silently disables a guarantee the
/// rest of the architecture is built on.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> never populate the change tracker and never call
/// <c>SaveChanges</c>, so every <c>SaveChanges</c> interceptor is bypassed:
/// <c>WorkspaceOwnershipInterceptor</c> stops rejecting an ownership change, and
/// <c>ImmutableRecordInterceptor</c> stops protecting audit rows and recipe versions. One
/// <c>ExecuteUpdateAsync</c> setting <c>WorkspaceId</c> moves creator intellectual property between
/// workspaces with nothing objecting; one <c>ExecuteDeleteAsync</c> erases a history that the entity
/// documentation promises is immutable.
/// </para>
/// <para>
/// <c>ExecuteSql</c> and <c>FromSql</c> go further and leave EF's model behind entirely, and
/// <c>IgnoreQueryFilters</c> removes workspace scoping by name — tenancy.md already prohibits it outside
/// documented maintenance paths, and this is where that prohibition becomes enforceable.
/// </para>
/// <para>
/// This is the only layer where the rule can be enforced at all: an interceptor cannot see a call that
/// never reaches it. Source scanning rather than reflection, for the same reason
/// <see cref="ModuleBoundaryTests"/> reads source — these are calls that must not be <em>written</em>, and
/// a <c>using</c>-level or IL-level view would miss the intent while catching the mechanics.
/// </para>
/// </remarks>
public sealed class BulkOperationBoundaryTests
{
    /// <summary>
    /// Every EF API that goes around the interceptors, the query filters, or the model.
    /// </summary>
    private static readonly string[] ForbiddenCalls =
    [
        "ExecuteUpdate", "ExecuteUpdateAsync",
        "ExecuteDelete", "ExecuteDeleteAsync",
        "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated",
        "FromSql", "FromSqlRaw", "FromSqlInterpolated",
        "IgnoreQueryFilters",
    ];

    /// <summary>
    /// Files permitted to name one of these, with the reason. Empty on purpose: nothing needs one today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the first documented erasure or platform-maintenance path arrives — the carve-out tenancy.md
    /// anticipates — it is added here with a comment, in a diff a reviewer can see. That is the whole value
    /// of an explicit list over a convention: granting the exception costs a visible, arguable line.
    /// </para>
    /// <para>
    /// The AI queue claim is the first. A worker looks for work <em>before</em> it knows which workspace it
    /// will serve, so there is no <c>IWorkspaceContext</c> to filter by and the global filter throws rather
    /// than returning nothing. tenancy.md names background queue claims as a fourth carve-out category, with
    /// the constraint that such a query returns identifiers only — and
    /// <c>AiOperationClaimTests.A_claim_carries_no_creator_content</c> holds it to that. The file is
    /// deliberately the smallest one that could carry it: the workspace-scoped repository beside it gets no
    /// exemption.
    /// </para>
    /// <para>
    /// The account-usage reconciliation is the second (9A.7), and it claims a <em>different</em> one of
    /// tenancy.md's four categories: <strong>platform maintenance</strong>, not the background queue claim.
    /// That distinction is load-bearing. A queue claim carries the extra condition that the worker resolve and
    /// validate the workspace through the ordinary tenancy path before reading anything else, and this pass
    /// deliberately never resolves one at all — it compares two tables against each other and writes to a
    /// third that has no filter by design. Saying which category is claimed is the point of an explicit list.
    /// </para>
    /// <para>
    /// "Which provider attempts did the ledger never receive?" is a question no single workspace can answer:
    /// the ledger it compares against is platform-scoped (USAGE-002), so a pass run per workspace would have to
    /// enumerate every workspace to answer a question about none of them. The identifiers-only constraint still
    /// applies and is held by
    /// <c>AiUsageReconciliationTests.The_reconciliation_carries_no_creator_content</c>: what crosses the
    /// boundary is identifiers, counts, instants and enums, with nowhere to put a title, a snapshot, a prompt
    /// or even the failure summary the row it reads does carry. Two further guarantees are enforced in code
    /// rather than asserted here — the membership join is paired on workspace as well as id, and the pass
    /// refuses to run in a scope that has a workspace resolved.
    /// </para>
    /// <para>
    /// The document-extraction claim is the third (11A.10), and it claims the same category as the first: a
    /// <strong>background queue claim</strong>. It is the same shape for the same reason — a worker looks for a
    /// document to read before it knows whose document it is — and it carries both of that category's conditions.
    /// <c>BrandSourceExtractionClaim</c> has fields for six identifiers and nothing else, with nowhere to put a
    /// title, a filename, a media type, an object key or a byte of the document; and the worker resolves and
    /// validates the workspace through <c>IWorkspaceResolutionFacade</c> before it reads even the operation row,
    /// which is itself workspace-owned and unreachable without a resolved context.
    /// </para>
    /// <para>
    /// The brand-source embedding claim (11A.13) is the fourth, in the same category and under the same two
    /// conditions: <c>BrandSourceEmbeddingClaim</c>, <c>BrandSourceRetirableSet</c> and
    /// <c>BrandSourceEmbeddingBackfill</c> carry identifiers and nothing else, and every write they lead to is
    /// made by a facade in a scope whose workspace the Worker has resolved and validated first.
    /// </para>
    /// </remarks>
    private static readonly string[] Exemptions =
    [
        "Modules/Ai/Data/AiOperationClaimRepository.cs",
        "Modules/Ai/Data/AiUsageReconciliationRepository.cs",
        "Modules/Brand/Data/BrandSourceEmbeddingClaimRepository.cs",
        "Modules/Brand/Data/BrandSourceExtractionClaimRepository.cs",
        "Modules/Media/Data/GeneratedImageClaimRepository.cs",
    ];

    [Fact]
    public void No_domain_code_bypasses_the_interceptors_or_the_query_filters()
    {
        var offenders = new List<string>();

        foreach (var file in DomainSourceFiles())
        {
            if (Exemptions.Contains(file.RelativePath))
            {
                continue;
            }

            var code = StripCommentsAndStrings(File.ReadAllText(file.FullPath));

            offenders.AddRange(ForbiddenCalls
                .Where(call => Regex.IsMatch(code, $@"\.{Regex.Escape(call)}\s*[(<]"))
                .Select(call => $"{file.RelativePath} calls {call}"));
        }

        Assert.True(
            offenders.Count == 0,
            "domain code used an EF escape hatch that bypasses ownership, immutability, or workspace filtering:\n"
                + string.Join("\n", offenders.Distinct()));
    }

    /// <summary>
    /// The scan sees the domain, so the rule above cannot pass by finding nothing.
    /// </summary>
    /// <remarks>
    /// Only the lower bound is load-bearing: a scan that found a handful of files would pass the rule above
    /// vacuously, and that is what this is for. The upper bound is a tripwire for a root resolved somewhere
    /// absurd — the repository, a drive — rather than a budget for how large the domain may grow, so it is set
    /// generously. It had quietly gone stale at 600 against a domain of over 700 files, which nobody saw because
    /// a redirected build output makes this class fail on root discovery first.
    /// </remarks>
    [Fact]
    public void The_scan_actually_sees_the_domain()
    {
        var files = DomainSourceFiles();

        Assert.InRange(files.Count, 150, 5000);

        // And it can actually detect an offender: the pattern matches text of the shape it is looking for.
        Assert.Matches(@"\.ExecuteDeleteAsync\s*[(<]", "await db.RecipeVersions.ExecuteDeleteAsync();");
    }

    [Fact]
    public void Every_exemption_still_earns_its_place()
    {
        Assert.All(Exemptions, path =>
        {
            var matches = DomainSourceFiles().Where(candidate => candidate.RelativePath == path).ToList();

            Assert.True(matches.Count == 1, $"exempted file no longer exists: {path}");

            var code = StripCommentsAndStrings(File.ReadAllText(matches[0].FullPath));
            Assert.Contains(ForbiddenCalls, call => Regex.IsMatch(code, $@"\.{Regex.Escape(call)}\s*[(<]"));
        });
    }

    /// <summary>
    /// Removes comments and string literals, so a call named only in documentation does not fail the rule.
    /// </summary>
    /// <remarks>
    /// This matters immediately: <c>IImmutableRecord</c> and <c>AuditLogImmutabilityInterceptor</c> both
    /// discuss these APIs by name in their remarks, precisely to record that the hole exists.
    /// </remarks>
    private static string StripCommentsAndStrings(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//[^\n]*", string.Empty);
        var withoutVerbatimStrings = Regex.Replace(withoutLineComments, "@\"(?:[^\"]|\"\")*\"", "\"\"", RegexOptions.Singleline);

        return Regex.Replace(withoutVerbatimStrings, @"""(?:\\.|[^""\\])*""", "\"\"");
    }

    private static List<(string RelativePath, string FullPath)> DomainSourceFiles()
    {
        var root = DomainRoot();

        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // Migrations are generated, and their generated code legitimately emits raw SQL.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), path))];
    }

    private static string DomainRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CreatorPantry.Domain")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "could not locate the repository root from the test output directory");

        return Path.Combine(directory!.FullName, "src", "CreatorPantry.Domain");
    }
}
