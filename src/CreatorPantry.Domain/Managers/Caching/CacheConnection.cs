namespace CreatorPantry.Domain.Managers.Caching;

/// <summary>The Aspire resource name the shared cache arrives under, as the AppHost declares it.</summary>
/// <remarks>
/// Here rather than in a host, because more than one host consumes that resource and a restated literal is
/// the drift this project avoids elsewhere — the same reason <c>CreatorPantryDbContext.ConnectionName</c>
/// lives beside the context instead of in each host that opens it. Which <em>store</em> a host registers
/// behind <see cref="IApplicationCache"/> stays the host's decision; only the name is shared.
/// </remarks>
public static class CacheConnection
{
    /// <inheritdoc cref="CacheConnection"/>
    public const string Name = "cache";
}
