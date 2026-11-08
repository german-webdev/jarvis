using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Jarvis.App.Services;
using Jarvis.Core;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;
using MediaControl = Jarvis.App.Services.MediaControl;

namespace Jarvis.DesktopProbe;

// Opt-in Windows integration test. Only its own child windows receive window commands.
public static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [STAThread] public static int Main(string[] args)
    {
        if (args.Length < 1) return 2;
        string root = Path.GetFullPath(args[0]);
        if (args.Contains("--verify-yandex")) return VerifyYandex(root).GetAwaiter().GetResult();
        if (args.Contains("--inspect-media"))
        {
            var manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            var sessions = manager.GetSessions().Select(s => new { source = s.SourceAppUserModelId, status = s.GetPlaybackInfo().PlaybackStatus.ToString(),
                pause = s.GetPlaybackInfo().Controls.IsPauseEnabled, play = s.GetPlaybackInfo().Controls.IsPlayEnabled,
                next = s.GetPlaybackInfo().Controls.IsNextEnabled, previous = s.GetPlaybackInfo().Controls.IsPreviousEnabled }).ToArray();
            File.WriteAllText(Path.Combine(root, "work", "media-inspection.json"), JsonSerializer.Serialize(new { current = manager.GetCurrentSession()?.SourceAppUserModelId, sessions }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        SetCurrentProcessExplicitAppUserModelID("Jarvis.DesktopProbe");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--fixture"))
        {
            bool guard = args.Contains("--guard");
            var window = new Window { Title = guard ? "JARVIS test: unsaved document" : "JARVIS test: disposable window", Width = 450, Height = 180, ShowActivated = false,
                Content = "Автоматическая проверка Джарвиса. Тестовое окно закроется само." };
            window.Closing += (_, e) => { if (guard) e.Cancel = true; else app.Shutdown(); };
            MediaPlayer? player = null;
            if (!guard)
            {
                var wav = Path.Combine(root, "work", "desktop-probe-silence.wav");
                using (var writer = new NAudio.Wave.WaveFileWriter(wav, new NAudio.Wave.WaveFormat(16000, 16, 1))) writer.Write(new byte[320000], 0, 320000);
                player = new MediaPlayer { Volume = 0, IsLoopingEnabled = true, Source = MediaSource.CreateFromUri(new Uri(wav)) };
                player.CommandManager.IsEnabled = false;
                var controls = player.SystemMediaTransportControls;
                controls.IsEnabled = true; controls.IsPlayEnabled = true; controls.IsPauseEnabled = true; controls.IsNextEnabled = true; controls.IsPreviousEnabled = true;
                controls.DisplayUpdater.Type = MediaPlaybackType.Music;
                controls.DisplayUpdater.MusicProperties.Title = "JARVIS integration test"; controls.DisplayUpdater.Update();
                int next = 0, previous = 0;
                controls.ButtonPressed += (_, e) => app.Dispatcher.BeginInvoke(() =>
                {
                    if (e.Button == SystemMediaTransportControlsButton.Pause) { player.Pause(); controls.PlaybackStatus = MediaPlaybackStatus.Paused; }
                    if (e.Button == SystemMediaTransportControlsButton.Play) { player.Play(); controls.PlaybackStatus = MediaPlaybackStatus.Playing; }
                    if (e.Button == SystemMediaTransportControlsButton.Next) next++;
                    if (e.Button == SystemMediaTransportControlsButton.Previous) previous++;
                    File.WriteAllText(Path.Combine(root, "work", "desktop-probe-media.json"), JsonSerializer.Serialize(new { next, previous }));
                });
                player.Play(); controls.PlaybackStatus = MediaPlaybackStatus.Playing;
            }
            window.Show(); app.Run(); player?.Dispose(); return 0;
        }
        int exit = 1;
        app.Startup += async (_, _) =>
        {
            var report = new Dictionary<string, object?>();
            var children = new List<Process>();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "work"));
                var executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
                foreach (bool guard in new[] { false, true })
                {
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root };
                    start.ArgumentList.Add(root); start.ArgumentList.Add("--fixture"); if (guard) start.ArgumentList.Add("--guard");
                    children.Add(Process.Start(start)!);
                }
                var catalog = new[] { new LaunchTarget("probe", "Тестовая программа", "exe", executable, "Test", [], true) };
                var windows = new WindowControl();
                LaunchTarget[] targets = [];
                for (int i = 0; i < 40; i++)
                {
                    targets = windows.Capture(catalog).Where(t => children.Any(p => t.Id.StartsWith($"window:{p.Id}:"))).ToArray();
                    if (targets.Length == 2) break;
                    await Task.Delay(250);
                }
                if (targets.Length != 2) throw new Exception("Fixture windows not found");
                var normal = targets.Single(t => t.Source.Contains("disposable"));
                var guarded = targets.Single(t => t.Source.Contains("unsaved"));
                var focus = await windows.ExecuteAsync(new(Intent.FocusTarget, normal.Id), default);
                report["focus"] = focus;
                // Windows may refuse focus stealing. The reported result must match actual focus.
                bool focusAccurate = focus.Verified == (GetForegroundWindow().ToString() == normal.Target);
                report["focus_result_matches_windows"] = focusAccurate;
                var minimized = await windows.ExecuteAsync(new(Intent.MinimizeWindow, normal.Id), default);
                report["minimize"] = minimized;
                var restored = await windows.ExecuteAsync(new(Intent.FocusTarget, normal.Id), default);
                report["restore"] = restored;
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                GlobalSystemMediaTransportControlsSession? session = null;
                for (int i = 0; i < 40; i++)
                {
                    session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Jarvis.DesktopProbe", StringComparison.OrdinalIgnoreCase));
                    if (session != null) break;
                    await Task.Delay(250);
                }
                if (session == null) throw new Exception("Fixture media session not found");
                var pause = await MediaControl.ApplyAsync(session, Intent.MediaPause, default);
                var pauseAgain = await MediaControl.ApplyAsync(session, Intent.MediaPause, default);
                var play = await MediaControl.ApplyAsync(session, Intent.MediaPlay, default);
                report["pause"] = pause; report["pause_again"] = pauseAgain; report["play"] = play;
                await MediaControl.ApplyAsync(session, Intent.MediaNext, default);
                await MediaControl.ApplyAsync(session, Intent.MediaPrevious, default);
                await Task.Delay(500);
                var counts = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "work", "desktop-probe-media.json"))).RootElement;
                bool tracks = counts.GetProperty("next").GetInt32() > 0 && counts.GetProperty("previous").GetInt32() > 0;
                report["next_and_previous"] = tracks;
                report["exe_folder"] = await AppLocations.ResolveAsync(catalog[0], default);
                var installed = new LaunchCatalogStore(Path.Combine(root, "data", "apps.json")).Load().Targets;
                foreach (var target in installed.Where(t => t.Name is "Subnautica" or "Telegram" or "XBOX"))
                    report["folder_" + target.Name] = await AppLocations.ResolveAsync(target, default);
                var closeGuarded = await windows.ExecuteAsync(new(Intent.CloseTarget, guarded.Id), default);
                bool guardedStillOpen = windows.Capture(catalog).Any(t => t.Id == guarded.Id);
                report["unsaved_window_preserved"] = guardedStillOpen && !closeGuarded.Verified;
                var close = await windows.ExecuteAsync(new(Intent.CloseTarget, normal.Id), default);
                report["close"] = close;
                var stale = await windows.ExecuteAsync(new(Intent.CloseTarget, normal.Id), default);
                report["stale_target_rejected"] = !stale.Verified;
                bool success = focusAccurate && minimized.Verified && restored.Verified && pause.Verified && pauseAgain.Verified && play.Verified && tracks
                    && guardedStillOpen && !closeGuarded.Verified && close.Verified && !stale.Verified;
                report["success"] = success; exit = success ? 0 : 1;
            }
            catch (Exception e) { report["error"] = e.ToString(); }
            finally
            {
                foreach (var child in children) { try { if (!child.HasExited) child.Kill(); } catch { } child.Dispose(); }
                File.WriteAllText(Path.Combine(root, "work", "desktop-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(exit);
            }
        };
        app.Run(); return exit;
    }
    private static async Task<int> VerifyYandex(string root)
    {
        var report = new Dictionary<string, object?>();
        GlobalSystemMediaTransportControlsSession? yandex = null;
        bool wasPlaying = false;
        int exit = 1;
        try
        {
            var catalog = new LaunchCatalogStore(Path.Combine(root, "data", "apps.json")).Load().Targets;
            var index = await AppLocations.ExecutablesAsync(default);
            report["registered_executables"] = catalog.Where(t => t.Name is "Яндекс Музыка" or "Kaspersky Password Manager" or "Word" or "Excel")
                .Select(t => new { t.Name, executable = index.GetValueOrDefault(t.Target), custom = NameVariants.SplitCustom(t.Aliases).ToArray() }).ToArray();
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var before = manager.GetSessions().ToArray();
            yandex = before.Single(s => s.SourceAppUserModelId == "ru.yandex.desktop.music");
            wasPlaying = yandex.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var chrome = before.FirstOrDefault(s => s.SourceAppUserModelId == "Chrome");
            var chromeBefore = chrome?.GetPlaybackInfo().PlaybackStatus;
            string? preferred = null;
            var music = new MusicPlayback(() => catalog, () => preferred, id => preferred = id);
            var selected = MusicSelection.Choose(before.Select(s => MusicPlayback.Describe(s, catalog)).ToArray(), catalog, null);
            report["selected_source"] = selected.SourceId;
            if (selected.SourceId != "ru.yandex.desktop.music") throw new Exception("Yandex was not selected over browser");
            var paused = await music.ExecuteAsync(new(Intent.MediaPause), default);
            report["pause"] = paused;
            var dialog = new LaunchConversation();
            var turn = dialog.Resolve("Включи музыку", catalog, DateTimeOffset.UtcNow);
            report["music_question"] = turn.Question;
            var command = turn.NeedsAnswer ? dialog.Resolve("Яндекс Музыку", catalog, DateTimeOffset.UtcNow).Command! : turn.Command!;
            if (command.Intent != Intent.StartMusic) throw new Exception("Music choice did not resolve");
            var played = await music.StartAsync(command, default); report["play"] = played;
            bool chromeUnchanged = chrome?.GetPlaybackInfo().PlaybackStatus == chromeBefore;
            report["chrome_unchanged"] = chromeUnchanged;
            var windows = new WindowControl();
            var snapshot = await windows.CaptureAsync(catalog, default);
            var window = snapshot.Single(t => t.Name == "Яндекс Музыка");
            var focused = await windows.ExecuteAsync(new(Intent.FocusTarget, window.Id), default);
            report["focus_yandex"] = focused;
            report["remembered_music"] = preferred;
            report["success"] = paused.Verified && played.Verified && chromeUnchanged && focused.Verified;
            exit = Equals(report["success"], true) ? 0 : 1;
        }
        catch (Exception error) { report["error"] = error.ToString(); }
        finally
        {
            if (yandex != null) { try { await MediaControl.ApplyAsync(yandex, wasPlaying ? Intent.MediaPlay : Intent.MediaPause, default); } catch { } }
            File.WriteAllText(Path.Combine(root, "work", "yandex-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return exit;
    }
}
