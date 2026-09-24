using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Records the SQL of every command executed through the context it is attached to.
/// </summary>
/// <remarks>
/// For the assertions that are about the statement rather than its result — most importantly, that listing a
/// recipe's history never reads <c>RecipeVersionSnapshots</c>. Asserting on the returned rows cannot show
/// that: a projection that joined the archive and then discarded it would return exactly the same records,
/// having read every byte of every version to do so.
/// </remarks>
internal sealed class CaptureCommandText : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyCollection<string> Commands => _commands;

    public bool Mentions(string fragment) =>
        _commands.Any(command => command.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
