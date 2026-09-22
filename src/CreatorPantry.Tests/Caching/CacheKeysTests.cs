using System.Reflection;
using CreatorPantry.Domain.Caching;

namespace CreatorPantry.Tests.Caching;

public sealed class CacheKeysTests
{
    [Fact]
    public void Workspace_key_carries_the_workspace_id_category_and_id()
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var key = CacheKeys.Workspace(workspaceId, "recipe", "42");

        Assert.Equal("workspace:11111111-1111-1111-1111-111111111111:recipe:42", key);
    }

    [Fact]
    public void Global_key_never_carries_a_workspace_id()
    {
        var key = CacheKeys.Global("ingredient", "flour");

        Assert.Equal("global:ingredient:flour", key);
    }

    [Fact]
    public void Workspace_and_global_keys_for_the_same_category_and_id_never_collide()
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.NotEqual(CacheKeys.Global("recipe", "42"), CacheKeys.Workspace(workspaceId, "recipe", "42"));
    }

    [Fact]
    public void Two_different_workspaces_never_share_a_key_for_the_same_category_and_id()
    {
        var a = CacheKeys.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"), "recipe", "42");
        var b = CacheKeys.Workspace(Guid.Parse("22222222-2222-2222-2222-222222222222"), "recipe", "42");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void An_empty_workspace_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CacheKeys.Workspace(Guid.Empty, "recipe", "42"));
    }

    [Theory]
    [InlineData("", "42")]
    [InlineData("recipe", "")]
    [InlineData("   ", "42")]
    public void Blank_segments_are_rejected_for_a_workspace_key(string category, string id) =>
        Assert.Throws<ArgumentException>(() => CacheKeys.Workspace(Guid.NewGuid(), category, id));

    [Theory]
    [InlineData("", "flour")]
    [InlineData("ingredient", "")]
    public void Blank_segments_are_rejected_for_a_global_key(string category, string id) =>
        Assert.Throws<ArgumentException>(() => CacheKeys.Global(category, id));

    /// <summary>
    /// Structural proof of "a global key can never accept workspace scope": the only overload of
    /// <see cref="CacheKeys.Global"/> takes two strings — there is no way to add a workspace id to it
    /// without changing this signature, so this test breaks the moment anyone tries.
    /// </summary>
    [Fact]
    public void Global_has_exactly_one_overload_and_it_has_no_workspace_parameter()
    {
        var overloads = typeof(CacheKeys).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(CacheKeys.Global))
            .ToList();

        var only = Assert.Single(overloads);
        Assert.Equal([typeof(string), typeof(string)], only.GetParameters().Select(p => p.ParameterType));
    }
}
