using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class SkillsTests
{
    private static LaunchTarget Game(string id = "steam:1", bool enabled = true) => new(id, "Call of Duty", "steam", "1", "Steam", ["колда", "кал оф дьюти"], enabled);
    [Theory]
    [InlineData("Запусти Call of Duty")]
    [InlineData("Джарвис, запусти игру кал оф дьюти, пожалуйста.")]
    [InlineData("Запусти колду")]
    public void GameNamesAndOptionalAliasesWork(string text) => Assert.Equal("steam:1", CommandRouter.Resolve(text, [Game()]).TargetId);
    [Fact] public void OriginalNameNeedsNoAliases() => Assert.Equal(Intent.LaunchTarget, CommandRouter.Resolve("Запусти Call of Duty", [Game() with { Aliases = [] }]).Intent);
    [Theory]
    [InlineData("Не запускай колду")]
    [InlineData("Запусти колду и выключи компьютер")]
    [InlineData("Может запусти колду")]
    [InlineData("Запусти неизвестную игру")]
    public void NoGuessingOrExtraActions(string text) => Assert.Equal(Intent.Unknown, CommandRouter.Resolve(text, [Game()]).Intent);
    [Fact] public void DisabledOrDuplicateNamesCannotLaunch()
    {
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Запусти колду", [Game(enabled: false)]).Intent);
        var result = CommandRouter.Resolve("Запусти колду", [Game(), Game("steam:2")]);
        Assert.Equal(Intent.Unknown, result.Intent); Assert.NotNull(result.Explanation);
    }
    [Theory]
    [InlineData("громкость 30 процентов", 30)]
    [InlineData("установи громкость на тридцать пять процентов", 35)]
    [InlineData("громкость ноль", 0)]
    [InlineData("громкость сто процентов", 100)]
    public void AbsoluteVolume(string text, int expected) => Assert.Equal(expected, CommandRouter.Resolve(text).Value);
    [Theory]
    [InlineData("громкость -30")]
    [InlineData("громкость 101")]
    [InlineData("громкость тридцать десять")]
    [InlineData("громкость 30 и запусти колду")]
    public void InvalidVolumeCannotBecomeAction(string text) => Assert.Equal(Intent.Unknown, CommandRouter.Parse(text));
    [Theory]
    [InlineData("сделай скриншот", Intent.Screenshot)]
    [InlineData("открой папку скриншотов", Intent.OpenScreenshots)]
    [InlineData("сделай громче", Intent.VolumeUp)]
    [InlineData("тише", Intent.VolumeDown)]
    [InlineData("выключи звук", Intent.Mute)]
    [InlineData("включи звук", Intent.Unmute)]
    [InlineData("сколько времени", Intent.Time)]
    [InlineData("Джарвис, сколько времени?", Intent.Time)]
    [InlineData("какое сегодня число", Intent.Date)]
    public void BuiltIns(string text, Intent expected) => Assert.Equal(expected, CommandRouter.Parse(text));
    [Fact] public async Task CancellationPreventsExecutorAndFailureDoesNotBreakNextCommand()
    {
        int calls = 0;
        var dispatcher = new SkillDispatcher([new DelegateSkill([Intent.Screenshot], (_, _) =>
        { calls++; if (calls == 1) throw new IOException(); return Task.FromResult(new SkillResult(true, "ok")); })]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.ExecuteAsync(new(Intent.Screenshot), new CancellationToken(true)));
        Assert.Equal(0, calls);
        Assert.False((await dispatcher.ExecuteAsync(new(Intent.Screenshot), CancellationToken.None)).Verified);
        Assert.True((await dispatcher.ExecuteAsync(new(Intent.Screenshot), CancellationToken.None)).Verified);
    }
    [Fact] public void CatalogRoundTripPreservesOptionalNamesAndEnabledState()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new LaunchCatalogStore(path); store.Save([Game(enabled: false)]);
            var loaded = Assert.Single(store.Load().Targets);
            Assert.False(loaded.Enabled); Assert.Equal(Game().Aliases, loaded.Aliases);
            File.WriteAllText(path, "{broken"); Assert.NotNull(store.Load().Warning); Assert.Equal("{broken", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("steam", "1/../../evil")]
    [InlineData("app", "SomeApp & whoami")]
    [InlineData("exe", "cmd /c calc")]
    [InlineData("shell", "anything")]
    public void InvalidTargetsRejected(string kind, string value) => Assert.False((Game() with { Kind = kind, Target = value }).IsValid);
    [Fact] public void SteamLibraryEscapesAndInstalledState()
    {
        Assert.Equal(@"F:\SteamLibrary", Assert.Single(SteamManifest.LibraryPaths("\"path\" \"F:\\\\SteamLibrary\"")));
        var folder = Path.Combine(Path.GetTempPath(), "jarvis-manifest-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(folder, "common", "Example"));
        try
        {
            var data = "\"AppState\" { \"appid\" \"123\" \"name\" \"Example Game\" \"StateFlags\" \"4\" \"installdir\" \"Example\" }";
            var game = SteamManifest.ReadApp(data, folder); Assert.NotNull(game); Assert.Equal("123", game.Target); Assert.True(game.Enabled);
            Assert.Null(SteamManifest.ReadApp(data.Replace("\"4\"", "\"2\""), folder));
            Assert.Null(SteamManifest.ReadApp(data.Replace("\"Example\"", "\"Missing\""), folder));
        }
        finally { Directory.Delete(Path.Combine(folder, "common", "Example")); Directory.Delete(Path.Combine(folder, "common")); Directory.Delete(folder); }
    }
}
