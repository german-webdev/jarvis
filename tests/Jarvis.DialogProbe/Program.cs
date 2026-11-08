using System.IO;
using System.Text.Json;
using System.Windows;
using Jarvis.App;
using Jarvis.Core;

namespace Jarvis.DialogProbe;

// Opt-in local integration probe. Real models and audio output; fake game executors, no microphone.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string root = Path.GetFullPath(args[0]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 1;
        app.Startup += async (_, _) =>
        {
            var result = new Dictionary<string, object?>();
            string? launched = null;
            var done = new TaskCompletionSource<bool>();
            var targets = new[]
            {
                new LaunchTarget("five", "Forza Horizon 5", "steam", "5", "Steam", [], true),
                new LaunchTarget("six", "Forza Horizon 6", "steam", "6", "Steam", [], true)
            };
            var skills = new SkillDispatcher([new DelegateSkill([Intent.LaunchTarget], (request, _) =>
            { launched = request.TargetId; return Task.FromResult(new SkillResult(true, "Проверка выбора завершена.")); })]);
            var vm = new MainViewModel(root, targets, skills);
            vm.Initialized += async () => await vm.RunTextAsync("Запусти форзу");
            vm.ClarificationReady += async question =>
            {
                result["question"] = question;
                result["microphone_off_during_text_dialog"] = !vm.MicrophoneActive;
                result["no_launch_before_answer"] = launched == null;
                await vm.RunTextAsync("Шестую");
            };
            vm.CycleCompleted += success => done.TrySetResult(success);
            try
            {
                await vm.RestartAsync();
                var success = await done.Task.WaitAsync(TimeSpan.FromSeconds(120));
                result["selected_target"] = launched;
                result["pending_cleared"] = !vm.AwaitingChoice;
                result["success"] = success && launched == "six" && !vm.AwaitingChoice
                    && Equals(result.GetValueOrDefault("no_launch_before_answer"), true)
                    && Equals(result.GetValueOrDefault("microphone_off_during_text_dialog"), true);
                exit = Equals(result["success"], true) ? 0 : 1;
            }
            catch (Exception error) { result["error"] = error.Message; }
            finally
            {
                Directory.CreateDirectory(Path.Combine(root, "work"));
                File.WriteAllText(Path.Combine(root, "work", "dialog-probe.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                await vm.DisposeAsync(); app.Shutdown(exit);
            }
        };
        app.Run(); return exit;
    }
}
