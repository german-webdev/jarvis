using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public sealed class MultimediaTests
{
    private static readonly LaunchTarget Music = new("yandex", "Яндекс Музыка", "app", "yandex", "Windows", [], true);
    [Theory]
    [InlineData("пауза", MediaScope.Current)] [InlineData("продолжи", MediaScope.Current)]
    [InlineData("Джарвис, останови музыку", MediaScope.Music)] [InlineData("продолжи музыку", MediaScope.Music)]
    [InlineData("поставь видео на паузу", MediaScope.Video)] [InlineData("продолжи видео", MediaScope.Video)]
    [InlineData("сделай тише", MediaScope.Current)] [InlineData("музыку тише", MediaScope.Music)]
    public void ScopeFollowsWords(string phrase, MediaScope scope) => Assert.Equal(scope, CommandRouter.Resolve(phrase).MediaScope);
    [Fact] public void CurrentPlaybackWorksWhenPreferredMusicIsNotRunning()
    {
        Assert.Equal("Chrome", MusicSelection.Choose([new("Chrome", null, true)], [Music], Music.Id).SourceId);
        Assert.Null(MusicSelection.Choose([new("Chrome", null, true)], [Music], Music.Id, MediaScope.Music).SourceId);
    }
    [Fact] public void WindowsCurrentSessionResolvesSimultaneousSources()
    {
        MediaCandidate[] sessions = [new("Chrome", null, true, true), new("Yandex", Music.Id, true)];
        Assert.Equal("Chrome", MusicSelection.Choose(sessions, [Music], Music.Id).SourceId);
        Assert.Equal("Yandex", MusicSelection.Choose(sessions, [Music], Music.Id, MediaScope.Music).SourceId);
        Assert.Equal("Chrome", MusicSelection.Choose(sessions, [Music], Music.Id, MediaScope.Video).SourceId);
        Assert.Null(MusicSelection.Choose(sessions.Select(s => s with { IsCurrent = false }).ToArray(), [Music], Music.Id).SourceId);
    }
    [Fact] public void ResumeRemembersPausedVideoWhenWindowsHasNoCurrentSource()
    {
        MediaCandidate[] sessions = [new("Chrome", null, false), new("Yandex", Music.Id, false)];
        Assert.Equal("Chrome", MusicSelection.Choose(sessions, [Music], Music.Id, lastControlled: "Chrome").SourceId);
        Assert.Null(MusicSelection.Choose(sessions, [Music], Music.Id, lastControlled: "gone").SourceId);
    }
    [Fact] public void DuplicateBrowserSourcesDoNotSelectArbitraryTab()
    {
        Assert.Null(MusicSelection.Choose([new("Chrome", null, true, true), new("Chrome", null, true)], [], null).SourceId);
    }
    [Theory]
    [InlineData("Блокнот", false)] [InlineData("Notepad", false)] [InlineData("Word", false)]
    [InlineData("Яндекс Музыка", true)] [InlineData("VLC media player", true)] [InlineData("PotPlayer", true)]
    [InlineData("MPC-HC", true)] [InlineData("Windows Media Player", true)] [InlineData("AIMP", true)]
    public void DiscoveryFiltersApplications(string name, bool expected)
        => Assert.Equal(expected, MusicSelection.IsMediaApp(new("id", name, "app", "id", "Windows", [], true)));
    [Fact] public void SavedNonPlayerCannotBeLaunchedByMusicCommand()
    {
        var notepad = new LaunchTarget("note", "Блокнот", "app", "Notepad", "Windows", [], true);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Включи музыку", [notepad], musicTargetId: notepad.Id).Intent);
        Assert.False(MusicSelection.IsMediaApp(Music with { Kind = "steam", Target = "123" }));
        Assert.False(MusicSelection.IsMediaApp(Music with { Kind = "url", Target = "https://example.com" }));
    }
    [Fact] public void FileCommandsSeparateSongsFilmsAndApplicationsAndClarifyDuplicates()
    {
        var movie = MediaLibrary.FileEntry(Path.GetFullPath("Movie.mp4")) with { Name = "Океан" };
        var song = MediaLibrary.FileEntry(Path.GetFullPath("Song.mp3")) with { Name = "Океан" };
        LaunchTarget[] entries = [movie, song, Music];
        Assert.Equal(movie.Id, CommandRouter.Resolve("Включи фильм Океан", entries).TargetId);
        Assert.Equal(song.Id, CommandRouter.Resolve("Включи песню Океан", entries).TargetId);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Запусти приложение Океан", entries).Intent);
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve("Не включай фильм Океан", entries).Intent);
        var dialog = new LaunchConversation(); var now = DateTimeOffset.UtcNow;
        Assert.True(dialog.Resolve("Включи Океан", entries, now).NeedsAnswer);
        Assert.Equal(song.Id, dialog.Resolve("вариант два", entries, now).Command?.TargetId);
    }
    [Fact] public void ScanOnlyIndexesAllowedMediaAndPreservesFoldersAndAliasesOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-media-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Диск 1"));
        try
        {
            foreach (var name in new[] { "song.MP3", "movie.mkv", "movie.mp4.exe", "readme.txt", "link.lnk" }) File.WriteAllText(Path.Combine(root, "Диск 1", name), "fixture");
            var folder = MediaLibrary.Folder(root);
            var scan = MediaLibrary.Scan([folder, folder], default);
            Assert.Equal(2, scan.Files.Count); Assert.Empty(scan.Warnings);
            Assert.Contains(scan.Files, t => t.Kind == "audio"); Assert.Contains(scan.Files, t => t.Kind == "video");
            var store = new LaunchCatalogStore(Path.Combine(root, "catalog.json"));
            store.Save(new[] { folder }.Concat(scan.Files.Select(t => t with { Aliases = ["моё название"] })));
            var loaded = store.Load(); Assert.Null(loaded.Warning); Assert.Equal(3, loaded.Targets.Count);
            Assert.All(loaded.Targets.Where(t => t.Kind != "media-folder"), t => Assert.Contains("моё название", t.Aliases));
            Assert.NotEmpty(MediaLibrary.Scan([folder], default, limit: 1).Warnings);
            Assert.Empty(MediaLibrary.Scan([folder with { Enabled = false }], default).Files);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => MediaLibrary.Scan([folder], cancel.Token));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void MissingFolderReportsWarningAndExecutableCannotBecomeMedia()
    {
        var missing = MediaLibrary.Folder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.NotEmpty(MediaLibrary.Scan([missing], default).Warnings);
        Assert.False(new LaunchTarget("evil", "Film", "video", Path.GetFullPath("film.exe"), "", [], true).IsValid);
    }
}
