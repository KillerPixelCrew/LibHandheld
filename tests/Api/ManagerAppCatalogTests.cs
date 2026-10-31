namespace LibHandheld.Tests.Api;

public sealed class ManagerAppCatalogTests
{
    /// A manager app bound to a model must name a definition the catalog ships, or no machine ever matches it.
    [Fact]
    public void EveryManagerAppDefinitionNamesAShippedDefinition()
    {
        var definitions = HandheldDevice.Definitions.Select(definition => definition.Id)
            .ToHashSet(StringComparer.Ordinal);
        var bound = HandheldDevice.ManagerApps.Where(app => app.DefinitionId is not null).ToArray();
        Assert.NotEmpty(bound);
        Assert.All(bound, app => Assert.Contains(app.DefinitionId!, definitions));
    }
}
