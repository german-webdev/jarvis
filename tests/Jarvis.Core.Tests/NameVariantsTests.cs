using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class NameVariantsTests
{
    public static LaunchTarget Game(string name, string id = "1", bool enabled = true) => new(id, name, "steam", id, "Steam", [], enabled);
    [Theory]
    [InlineData("Subnautica", "сабнавтику")]
    [InlineData("Subnautica", "субнавтика")]
    [InlineData("Forza Horizon 6", "форзу")]
    [InlineData("Forza Horizon 6", "форза шесть")]
    [InlineData("Forza Horizon 6", "форзу 6")]
    [InlineData("Forza Horizon 6", "forza 6")]
    [InlineData("Counter-Strike 2", "контру")]
    [InlineData("Counter-Strike 2", "ка эс два")]
    [InlineData("Call of Duty®", "колду")]
    [InlineData("Remnant II", "ремнант два")]
    [InlineData("PUBG: BATTLEGROUNDS", "пабджи")]
    [InlineData("WARDOGS", "вардогс")]
    [InlineData("GRAIN ROT", "грейн рот")]
    [InlineData("Telegram", "телегу")]
    public void AutomaticNamesNeedNoManualSetup(string title, string phrase)
    {
        var game = Game(title);
        Assert.Empty(game.Aliases);
        Assert.Equal(game.Id, CommandRouter.Resolve("Запусти " + phrase, [game]).TargetId);
    }
    [Fact] public void NoBlindFirstWordOrFuzzyLaunch()
    {
        var titles = new[] { Game("Microsoft Store"), Game("Subnautica", "2") };
        foreach (var command in new[] { "запусти microsoft", "запусти сабнакатику", "не запускай сабнавтику", "запусти сабнавтику и стим" })
            Assert.Equal(Intent.Unknown, CommandRouter.Resolve(command, titles).Intent);
    }
    [Fact] public void FamilyVersionsAreExplicitCandidates()
    {
        var games = new[] { Game("Forza Horizon 5", "5"), Game("Forza Horizon 6", "6"), Game("Forza Motorsport", "8", false) };
        Assert.Equal(new[] { "5", "6" }, CommandRouter.Resolve("Запусти форзу", games).CandidateIds);
        Assert.Equal("6", CommandRouter.Resolve("Запусти форзу шесть", games).TargetId);
    }
    [Fact] public void SpeechHintsKeepGameVariantsAheadOfHundredsOfWindowsApps()
    {
        var apps = Enumerable.Range(1, 312).Select(i => new LaunchTarget("app" + i, "Windows application " + i, "app", "app" + i, "Windows", [], true));
        var hints = LaunchVocabulary.Build(apps.Append(Game("Subnautica")).Append(Game("Forza Horizon 6", "2")));
        Assert.InRange(hints.Length, 1, 1500);
        Assert.Contains("сабнавтику", hints); Assert.Contains("форзу", hints);
        var choice = LaunchVocabulary.Build([Game("Forza Horizon 5"), Game("Forza Horizon 6", "2")], true);
        Assert.Contains("шестую", choice); Assert.Contains("вариант два", choice);
    }
    [Fact] public void AutomaticVariantsDoNotOverwriteSavedCustomNames()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var target = Game("Subnautica") with { Aliases = ["подводная игра"] };
            var store = new LaunchCatalogStore(path); store.Save([target]);
            var loaded = Assert.Single(store.Load().Targets);
            Assert.Equal(new[] { "подводная игра" }, loaded.Aliases);
            Assert.Contains("сабнавтику", loaded.AutomaticAliases);
            Assert.DoesNotContain("AutomaticAliases", File.ReadAllText(path));
            Assert.Equal("1", CommandRouter.Resolve("Запусти подводная игра", [loaded]).TargetId);
        }
        finally { File.Delete(path); }
    }
}
