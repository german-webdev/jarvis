using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class WebSitesTests
{
    [Theory]
    [InlineData("Открой ютуб", "site:youtube")]
    [InlineData("Джарвис, открой сайт Кинопоиск", "site:kinopoisk")]
    [InlineData("Открой почту", "site:gmail")]
    [InlineData("Открой почту гугла", "site:gmail")]
    [InlineData("Открой почту Яндекса", "site:yandex-mail")]
    [InlineData("Открой Яндекс почту", "site:yandex-mail")]
    [InlineData("Открой озон", "site:ozon")]
    [InlineData("Открой Яндекс еду", "site:yandex-food")]
    [InlineData("Открой плати маркет", "site:plati")]
    public void RequestedWebsitesResolveLocally(string phrase, string expected)
    {
        var result = CommandRouter.Resolve(phrase, WebSites.Defaults());
        Assert.Equal(Intent.LaunchTarget, result.Intent);
        Assert.Equal(expected, result.TargetId);
    }
    [Theory]
    [InlineData("https://example.com/a?q=one&b=two", true)]
    [InlineData("example.com", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("steam://run/123", false)]
    [InlineData("https://name:password@example.com/", false)]
    [InlineData("https://example.com/\r\nrun", false)]
    [InlineData("https://", false)]
    public void OnlyWebAddressesAreAllowed(string input, bool expected) => Assert.Equal(expected, WebSites.TryNormalizeAddress(input, out _));

    [Fact] public void ExplicitWebsiteDisambiguatesApplicationAndGenericNameAsks()
    {
        var app = new LaunchTarget("app:youtube", "YouTube", "app", "youtube", "Windows", ["ютуб"], true);
        var targets = WebSites.Defaults().Append(app).ToArray();
        Assert.Equal("site:youtube", CommandRouter.Resolve("Открой сайт ютуб", targets).TargetId);
        Assert.Equal("app:youtube", CommandRouter.Resolve("Открой приложение ютуб", targets).TargetId);
        var conversation = new LaunchConversation(); var now = DateTimeOffset.UtcNow;
        Assert.True(conversation.Resolve("Открой ютуб", targets, now).NeedsAnswer);
        Assert.Equal("site:youtube", conversation.Resolve("вариант один", targets, now).Command?.TargetId);
    }
    [Fact] public void DisabledUnknownAndNonLaunchActionsNeverOpenSite()
    {
        var sites = WebSites.Defaults().Select(t => t with { Enabled = false }).ToArray();
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Открой ютуб", sites).Intent);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Открой сайт неизвестный", WebSites.Defaults()).Intent);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Открой папку ютуба", WebSites.Defaults()).Intent);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Закрой ютуб", WebSites.Defaults()).Intent);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Не открывай ютуб", WebSites.Defaults()).Intent);
    }
    [Fact] public void BareMailUsesConfiguredWebsiteEvenWithInstalledMailApp()
    {
        var app = new LaunchTarget("app:mail", "Почта", "app", "mail", "Windows", [], true);
        var targets = WebSites.Defaults().Append(app).ToArray();
        Assert.Equal("site:gmail", CommandRouter.Resolve("Открой почту", targets).TargetId);
        Assert.Equal("app:mail", CommandRouter.Resolve("Открой приложение почту", targets).TargetId);
    }
    [Fact] public void SiteCatalogPersistsCustomAddressAliasesAndEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), "jarvis-sites-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new LaunchCatalogStore(path);
            var site = WebSites.Defaults()[0] with { Target = "https://example.com/custom", Aliases = ["мой сайт, избранное"], Enabled = false };
            store.Save([site]);
            var loaded = Assert.Single(store.Load().Targets);
            Assert.Equal(site.Target, loaded.Target); Assert.Equal(site.Aliases, loaded.Aliases); Assert.False(loaded.Enabled);
            Assert.Contains("избранное", loaded.Names);
            store.Save([]); Assert.Empty(store.Load().Targets);
        }
        finally { File.Delete(path); }
    }
    [Fact] public void DefaultsAreValidAndMailIsUnambiguous()
    {
        Assert.Equal(7, WebSites.Defaults().Count);
        Assert.All(WebSites.Defaults(), t => Assert.True(t.IsValid));
        Assert.Equal("https://plati.market/", WebSites.Defaults().Single(t => t.Id == "site:plati").Target);
    }
}
