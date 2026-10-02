using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// A setup session is progress plus an opaque draft. It must never grow a typed copy of brand style, which
/// belongs to <c>BrandStyleGuideVersion</c> alone (the same rule as <c>BrandProfile</c>).
/// </summary>
public sealed class BrandSetupSessionModelShapeTests
{
    private static readonly string[] Forbidden =
    [
        "voice", "tone", "tenor", "style", "example", "sample", "vocabulary", "persona", "guide", "visual", "palette",
    ];

    [Fact]
    public void The_entity_has_no_typed_style_field()
    {
        var names = typeof(BrandSetupSession).GetProperties().Select(property => property.Name).ToList();

        foreach (var name in names)
        {
            Assert.DoesNotContain(Forbidden, word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_service_model_and_view_model_carry_no_workspace_or_user_identifier()
    {
        foreach (var type in new[] { typeof(BrandSetupSessionServiceModel), typeof(SaveBrandSetupSessionViewModel) })
        {
            var names = type.GetProperties().Select(property => property.Name).ToList();
            Assert.DoesNotContain("WorkspaceId", names);
            Assert.DoesNotContain("UserId", names);
            Assert.DoesNotContain("Id", names);
        }
    }
}
