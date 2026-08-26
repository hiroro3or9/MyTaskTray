using MyTaskTray.Models;
using MyTaskTray.Services;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class SettingsStructureCharacterizationTests
{
    [Fact]
    public void NormalizeMigratesLegacyCategoryAndIsIdempotent()
    {
        AppSettings settings = new()
        {
            Version = 1,
            Items =
            [
                new ClipItem { Name = "項目", Category = " Work " },
            ],
        };

        Assert.True(SettingsStructure.Normalize(settings));

        ClipItem item = Assert.Single(settings.Items);
        ClipCategory category = Assert.Single(settings.Categories);
        Assert.NotEmpty(item.Id);
        Assert.Equal("Work", category.Name);
        Assert.Equal(category.Id, item.CategoryId);
        Assert.Equal("Work", item.Category);
        MenuLayoutNode node = Assert.Single(settings.RegularMenu);
        Assert.Equal(MenuLayoutNodeKind.Category, node.Kind);
        Assert.Equal(category.Id, node.Id);
        Assert.Equal([item.Id], node.Children);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);

        Assert.False(SettingsStructure.Normalize(settings));
    }

    [Fact]
    public void NormalizeRepairsItemIdsAndSeparatesRegularAndContextualLayouts()
    {
        ClipItem regularA = new() { Id = " a ", Name = "A" };
        ClipItem regularB = new() { Id = "a", Name = "B" };
        ClipItem contextual = new()
        {
            Id = "ctx",
            Name = "C",
            ClipboardCondition = ClipboardMatchKind.Email,
        };
        AppSettings settings = new()
        {
            Items = [regularA, regularB, contextual],
            RegularMenu =
            [
                MenuLayoutNode.ForItem("missing"),
                MenuLayoutNode.ForItem("a"),
                MenuLayoutNode.ForItem("a"),
            ],
            ContextualMenu = [MenuLayoutNode.ForItem("a")],
        };

        Assert.True(SettingsStructure.Normalize(settings));

        Assert.Equal("a", regularA.Id);
        Assert.NotEqual(regularA.Id, regularB.Id);
        Assert.Equal(3, settings.Items.Select(item => item.Id).Distinct().Count());
        Assert.Equal(
            [regularA.Id, regularB.Id],
            settings.RegularMenu.SelectMany(NodeIds));
        Assert.Equal([contextual.Id], settings.ContextualMenu.SelectMany(NodeIds));
    }

    [Fact]
    public void NormalizeMergesDuplicateCategoryNamesAndRemapsReferences()
    {
        ClipItem item = new() { Id = "item", CategoryId = "category-2" };
        AppSettings settings = new()
        {
            Categories =
            [
                new ClipCategory { Id = "category-1", Name = " Team " },
                new ClipCategory { Id = "category-2", Name = "Team" },
            ],
            Items = [item],
            RegularMenu =
            [
                MenuLayoutNode.ForCategory("category-2", [item.Id]),
            ],
        };

        Assert.True(SettingsStructure.Normalize(settings));

        ClipCategory category = Assert.Single(settings.Categories);
        Assert.Equal("category-1", category.Id);
        Assert.Equal("Team", category.Name);
        Assert.Equal(category.Id, item.CategoryId);
        Assert.Equal("Team", item.Category);
        MenuLayoutNode node = Assert.Single(settings.RegularMenu);
        Assert.Equal(category.Id, node.Id);
        Assert.Equal([item.Id], node.Children);
    }

    [Fact]
    public void BuildLayoutGroupsCategoriesInFirstAppearanceOrder()
    {
        ClipItem regularTop = Item("regular-top");
        ClipItem contextualGrouped = Item(
            "contextual-grouped",
            "smart",
            ClipboardMatchKind.HasText);
        ClipItem regularGroupedA = Item("regular-grouped-a", "regular");
        ClipItem regularGroupedB = Item("regular-grouped-b", "regular");
        ClipItem contextualTop = Item(
            "contextual-top",
            clipboardCondition: ClipboardMatchKind.Email);
        ClipItem[] items =
        [
            regularTop,
            contextualGrouped,
            regularGroupedA,
            regularGroupedB,
            contextualTop,
        ];

        List<MenuLayoutNode> regular = SettingsStructure.BuildLayout(items, contextual: false);
        List<MenuLayoutNode> contextual = SettingsStructure.BuildLayout(items, contextual: true);

        Assert.Collection(
            regular,
            node =>
            {
                Assert.Equal(MenuLayoutNodeKind.Item, node.Kind);
                Assert.Equal(regularTop.Id, node.Id);
            },
            node =>
            {
                Assert.Equal(MenuLayoutNodeKind.Category, node.Kind);
                Assert.Equal("regular", node.Id);
                Assert.Equal([regularGroupedA.Id, regularGroupedB.Id], node.Children);
            });
        Assert.Collection(
            contextual,
            node =>
            {
                Assert.Equal(MenuLayoutNodeKind.Category, node.Kind);
                Assert.Equal("smart", node.Id);
                Assert.Equal([contextualGrouped.Id], node.Children);
            },
            node =>
            {
                Assert.Equal(MenuLayoutNodeKind.Item, node.Kind);
                Assert.Equal(contextualTop.Id, node.Id);
            });
    }

    [Fact]
    public void ApplyLayoutOrderPreservesSectionSlotsWhileReorderingEachSection()
    {
        ClipItem regularA = Item("regular-a");
        ClipItem contextualX = Item(
            "contextual-x",
            clipboardCondition: ClipboardMatchKind.HasText);
        ClipItem regularB = Item("regular-b");
        ClipItem contextualY = Item(
            "contextual-y",
            clipboardCondition: ClipboardMatchKind.Email);
        AppSettings settings = new()
        {
            Items = [regularA, contextualX, regularB, contextualY],
            RegularMenu =
            [
                MenuLayoutNode.ForItem(regularB.Id),
                MenuLayoutNode.ForItem(regularA.Id),
            ],
            ContextualMenu =
            [
                MenuLayoutNode.ForItem(contextualY.Id),
                MenuLayoutNode.ForItem(contextualX.Id),
            ],
        };

        SettingsStructure.ApplyLayoutOrder(settings);

        Assert.Equal(
            [regularB.Id, contextualY.Id, regularA.Id, contextualX.Id],
            settings.Items.Select(item => item.Id));
    }

    private static IEnumerable<string> NodeIds(MenuLayoutNode node)
        => node.Kind == MenuLayoutNodeKind.Item ? [node.Id] : node.Children;

    private static ClipItem Item(
        string id,
        string categoryId = "",
        ClipboardMatchKind clipboardCondition = ClipboardMatchKind.Always)
        => new()
        {
            Id = id,
            Name = id,
            CategoryId = categoryId,
            ClipboardCondition = clipboardCondition,
        };
}
