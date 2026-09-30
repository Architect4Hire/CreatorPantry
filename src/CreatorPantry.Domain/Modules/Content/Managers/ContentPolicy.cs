namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Limits shared by the content module's configuration and, later, its validators.</summary>
public static class ContentPolicy
{
    public const int ReasonMaxLength = 500;

    public const int TemplateIdMaxLength = 200;

    public const int TemplateVersionMaxLength = 32;

    /// <summary><c>sha256:</c> plus 64 hex characters.</summary>
    public const int ChecksumMaxLength = 71;

    public const int MachineVersionMaxLength = 32;
}
