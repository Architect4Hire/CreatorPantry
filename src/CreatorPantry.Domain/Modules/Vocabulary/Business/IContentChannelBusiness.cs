using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Domain.Modules.Vocabulary.Business;

public interface IContentChannelBusiness
{
    /// <summary>Every content channel, active and retired, in display order.</summary>
    IReadOnlyList<ContentChannelServiceModel> List();
}

/// <remarks>
/// No DataLayer beneath: the catalogue is configuration in code, so there is no persistence to compose. If it
/// ever moves into a table, this is where a DataLayer call replaces the catalogue read.
/// </remarks>
internal sealed class ContentChannelBusiness(IContentChannelCatalog catalog) : IContentChannelBusiness
{
    public IReadOnlyList<ContentChannelServiceModel> List() =>
        [.. catalog.All.Select(channel => new ContentChannelServiceModel(channel.Key, channel.DisplayName, channel.IsActive))];
}
