namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>A run's row version as the opaque string a client quotes back on its next change.</summary>
/// <remarks>Base64 of SQL Server's eight-byte <c>rowversion</c>, as every other module's token is.</remarks>
public static class WorkflowRunConcurrencyToken
{
    private const int Width = 8;

    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>Whether a submitted token is well formed — not whether it is current.</summary>
    public static bool IsWellFormed(string? token) => TryParse(token, out _);

    public static bool TryParse(string? token, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var buffer = new byte[Width];
        if (!Convert.TryFromBase64String(token, buffer, out var written) || written != Width)
        {
            return false;
        }

        rowVersion = buffer;
        return true;
    }
}
