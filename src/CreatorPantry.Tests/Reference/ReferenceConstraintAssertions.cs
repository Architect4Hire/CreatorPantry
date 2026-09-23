using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Reference;

internal static class ReferenceConstraintAssertions
{
    /// <summary>SQLite's wording for any foreign-key violation; it does not name the constraint.</summary>
    public const string ForeignKeyViolation = "FOREIGN KEY constraint failed";

    /// <summary>
    /// Asserts the save was refused, and refused by one <em>identified</em> rule. Naming it keeps a negative
    /// test honest: a row rejected for some unrelated reason would otherwise still look like a pass.
    /// </summary>
    /// <param name="rule">
    /// The fragment the provider reports — a check constraint's own name, <c>Table.Column</c> for a unique
    /// index (SQLite identifies those by the columns they cover, not by the index name), or
    /// <see cref="ForeignKeyViolation"/>.
    /// </param>
    public static async Task AssertRejectedByAsync(DbContext db, string rule)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains(rule, exception.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
    }
}
