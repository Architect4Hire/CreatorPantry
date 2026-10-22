namespace CreatorPantry.Domain.Managers.Limits;

/// <summary>
/// Every kind of work a caller can be limited on (15.3). The name is what a refusal and a metric say, so it is
/// part of the contract once shipped.
/// </summary>
public enum LimitedOperation
{
    /// <summary>Any request that queues an AI operation; a section batch counts once per section.</summary>
    AiRequest = 1,

    /// <summary>A request for generated images.</summary>
    ImageGeneration = 2,

    /// <summary>A file or text arriving to be stored: DAM, brand sources, paste, extraction retries and corrections.</summary>
    Upload = 3,

    /// <summary>A PDF rendered on the server.</summary>
    PdfExport = 4,

    /// <summary>A Markdown, HTML, JSON-LD or prompt file built from stored content.</summary>
    TextExport = 5,

    /// <summary>A library search or list.</summary>
    Search = 6,

    /// <summary>A nutrition estimate or mapping that asks the evidence provider.</summary>
    Nutrition = 7,

    /// <summary>Creating a workspace, which is where a slug is chosen and checked for availability.</summary>
    WorkspaceCreate = 8,

    /// <summary>Confirming or cancelling a publication, each of which calls the publishing provider.</summary>
    PublishingCommand = 9,
}

/// <summary>Whose allowance a limit counts against.</summary>
public enum LimitScope
{
    /// <summary>The signed-in person, across every workspace they work in.</summary>
    User = 1,

    /// <summary>The workspace the route names, across every member working in it.</summary>
    Workspace = 2,
}
