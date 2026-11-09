using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

internal static class LocalActionViewModelChecks
{
    internal static async Task VerifyAsync(string parent, Window ownWindow)
    {
        var root = Path.Combine(parent, "local-actions");
        TaskCompletionSource<HttpResponseMessage>? late = null; int requests = 0; int actions = 0;
        using var http = new HttpClient(new AiLocalActionServiceTests.Handler(_ =>
        {
            requests++;
            return late?.Task ?? Task.FromResult(requests < 3 ? AiLocalActionServiceTests.Tool(requests) : AiLocalActionServiceTests.TextAnswer());
        }));
        var ai = new AiService(root, http);
        var skills = new SkillDispatcher([new DelegateSkill([Intent.LaunchTarget, Intent.FocusTarget, Intent.Time], (request, _) =>
        { actions++; return Task.FromResult(new SkillResult(request.Intent != Intent.LaunchTarget, "fixture", Silent: request.Intent == Intent.Time)); })]);
        await using var runtime = new MainViewModel(root, [AiLocalActionServiceTests.FixtureTarget], skills: skills, ai: ai, actionWindows: new AiLocalActionServiceTests.Windows());
        AiLocalActionServiceTests.Configure(ai, root);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Ready))!.SetValue(runtime, true);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Phase))!.SetValue(runtime, AssistantPhase.Idle);
        await runtime.RunTextAsync("AI task: open Fixture then focus it");
        Assert.Equal(2, actions); Assert.Equal(3, requests); Assert.Contains(Ui.Text("Переключение на окно подтверждено."), runtime.Reply);
        Assert.False(runtime.MicrophoneActive);
        await runtime.RunTextAsync("сколько времени");
        Assert.Equal(3, actions); Assert.Equal(3, requests); // Local command keeps priority.

        late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = runtime.RunTextAsync("Задача ИИ: открой Fixture");
        Assert.True(runtime.Busy); Assert.True(runtime.CanEnterText); Assert.False(runtime.CanSpeak); Assert.True(runtime.CanStop);
        await runtime.RunTextAsync("stop task");
        Assert.False(runtime.Busy); Assert.False(runtime.MicrophoneActive);
        await pending;
        var reply = runtime.Reply; var logs = runtime.History.Count;
        late.SetResult(AiLocalActionServiceTests.Tool()); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(reply, runtime.Reply); Assert.Equal(logs, runtime.History.Count); Assert.Equal(3, actions);


        late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = runtime.RunTextAsync("Задача ИИ: открой Fixture");
        ai.Save(ai.Configuration with { AllowLocalActions = false });
        Assert.False(runtime.Busy); await pending;
        reply = runtime.Reply;
        late.SetResult(AiLocalActionServiceTests.Tool()); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(reply, runtime.Reply); Assert.Equal(3, actions);
        Assert.False(runtime.MicrophoneActive);

        // Only this test process's own off-screen WPF window is observed. No title/name matching.
        var fixtureWindow = new Window { Title = "JARVIS own observation fixture", Width = 160, Height = 100,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        fixtureWindow.Show();
        try
        {
            var windows = new WindowControl(allowOwnWindow: true);
            var target = new LaunchTarget("own-window", "Own test window", "exe", Environment.ProcessPath!, "fixture", [], true);
            var observed = await windows.ObserveAsync(target, CancellationToken.None);
            Assert.NotNull(observed); Assert.True(windows.IsCurrent(target, observed));
            Assert.False(windows.IsCurrent(target with { Target = @"C:\different.exe" }, observed));
            fixtureWindow.Hide(); Assert.False(windows.IsCurrent(target, observed));
            Assert.False(new WindowControl().IsCurrent(target, observed));
        }
        finally { fixtureWindow.Close(); }
    }
}
