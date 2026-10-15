namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>
/// A task's row version as the opaque token a client quotes back on a later edit. The same encoding the
/// content board's token uses, so one client reads them the same way.
/// </summary>
public static class WorkTaskConcurrencyToken
{
    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);
}
