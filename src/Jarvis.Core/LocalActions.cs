using System.Text.Json;

namespace Jarvis.Core;

public enum LocalActionKind { LaunchApplication, FocusApplication }
public sealed record LocalAction(LocalActionKind Kind, string TargetId);
public sealed record LocalActionTarget(string Id, string Name);
public sealed record LocalActionProgress(int Step, LocalAction Action, string Message, bool? Verified);
public sealed record ObservedWindow(string Id);
public interface ILocalActionWindows
{
    Task<ObservedWindow?> ObserveAsync(LaunchTarget target, CancellationToken token);
    bool IsCurrent(LaunchTarget target, ObservedWindow window);
}

/// <summary>Shared, typed executor. A new instance is one bounded run, usable without a model.</summary>
public sealed class LocalActionExecutor(Func<IReadOnlyList<LaunchTarget>> catalog, SkillDispatcher skills,
    ILocalActionWindows windows, Func<bool> permitted)
{
    public const int MaxActions = 3;
    private readonly Dictionary<LocalAction, SkillResult> _completed = [];
    private readonly Dictionary<string, (LaunchTarget Target, ObservedWindow Window)> _observed = [];
    private int _count;
    private bool _stopped;
    private int _executing;
    private readonly LaunchTarget[] _initial = catalog().Where(IsAllowed).GroupBy(t => t.Id)
        .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    public static bool IsAllowed(LaunchTarget target) => target.Enabled && target.IsValid && target.Kind == "exe";
    // Real catalog IDs can contain full paths. Model IDs are private to this run.
    public IReadOnlyList<LocalActionTarget> Available => _initial.Take(100).Select((t, i) => new LocalActionTarget("app_" + (i + 1), t.Name)).ToArray();
    public LocalAction ResolveModelAction(LocalAction action)
    {
        var index = Available.ToList().FindIndex(t => t.Id == action.TargetId);
        if (index < 0) throw new InvalidDataException("Цель недоступна. Выберите включённое приложение из каталога.");
        return action with { TargetId = _initial[index].Id };
    }

    public async Task<SkillResult> ExecuteAsync(LocalAction action, Action<LocalActionProgress>? progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _executing, 1) != 0) throw new InvalidOperationException("Concurrent local actions are not allowed.");
        try
        {
            void Check() { token.ThrowIfCancellationRequested(); if (!permitted()) throw new OperationCanceledException(token); }
            Check();
            if (_stopped) return new(false, "Задача остановлена после неподтверждённого шага.");
            var matches = catalog().Where(t => t.Id == action.TargetId && IsAllowed(t)).ToArray();
            if (matches.Length != 1 || !Enum.IsDefined(action.Kind)) return Stop("Цель недоступна. Выберите включённое приложение из каталога.");
            var target = matches[0];
            if (!_initial.Any(t => t == target)) return Stop("Запись каталога изменилась. Задача остановлена.");
            if (catalog().Count(t => IsAllowed(t) && CommandRouter.Normalize(t.Name) == CommandRouter.Normalize(target.Name)) != 1)
                return Stop("Название неоднозначно. Уточните название приложения в каталоге и повторите задачу.");
            if (_completed.ContainsKey(action)) return Stop("Повторный вызов действия. Задача остановлена без повторного выполнения.");
            if (_count >= MaxActions) return Stop("Достигнут предел: три действия на задачу.");
            if (_observed.TryGetValue(target.Id, out var known) && (known.Target != target || !windows.IsCurrent(target, known.Window)))
                return Stop("Окно приложения изменилось. Задача остановлена.");
            if (action.Kind == LocalActionKind.FocusApplication && !_observed.ContainsKey(target.Id))
                return Stop("Сначала нужно открыть и подтвердить окно приложения.");
            var step = ++_count;
            progress?.Invoke(new(step, action, action.Kind == LocalActionKind.LaunchApplication ? "Открываю приложение…" : "Переключаюсь на приложение…", null));
            // The progress callback may itself cancel or invalidate the run.
            Check();
            if (!catalog().Any(t => t == target && IsAllowed(t))) return Stop("Запись каталога изменилась. Задача остановлена.");
            SkillResult result;
            if (action.Kind == LocalActionKind.LaunchApplication)
            {
                // The launch skill reports request submission, never window readiness.
                await skills.ExecuteAsync(new(Intent.LaunchTarget, target.Id), token).WaitAsync(token);
                Check();
                var window = await windows.ObserveAsync(target, token).WaitAsync(token);
                Check();
                if (!catalog().Any(t => t == target && IsAllowed(t))) return Stop("Запись каталога изменилась. Задача остановлена.");
                if (window == null || !windows.IsCurrent(target, window)) result = new(false, "Окно приложения не подтверждено. Дальнейшие действия остановлены.");
                else { _observed[target.Id] = (target, window); result = new(true, "Окно приложения подтверждено."); }
            }
            else
            {
                Check();
                result = await skills.ExecuteAsync(new(Intent.FocusTarget, known.Window.Id), token).WaitAsync(token);
                Check();
                if (!catalog().Any(t => t == target && IsAllowed(t)) || !windows.IsCurrent(target, known.Window))
                    result = new(false, "Окно приложения изменилось. Задача остановлена.");
                else result = result.Verified ? new(true, "Переключение на окно подтверждено.") : new(false, "Переключение на окно не подтверждено. Задача остановлена.");
            }
            _completed[action] = result;
            if (!result.Verified) _stopped = true;
            progress?.Invoke(new(step, action, result.Message, result.Verified));
            Check();
            return result;
        }
        finally { Volatile.Write(ref _executing, 0); }
    }
    private SkillResult Stop(string message) { _stopped = true; return new(false, message); }
}

public sealed record AiToolCall(string Id, string Name, string Arguments);
public sealed record AiActionTurn(IReadOnlyList<JsonElement> Output, IReadOnlyList<AiToolCall> Calls, string Text);
public sealed record AiActionRequest(IReadOnlyList<AiMessage> Conversation, IReadOnlyList<LocalActionTarget> Targets, IReadOnlyList<JsonElement> Continuation);

public static class AiLocalActions
{
    public const int MaxModelCalls = 4;
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(90);

    public static LocalAction Parse(AiToolCall call, IReadOnlyList<LocalActionTarget> targets)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 200 || call.Arguments.Length > 1024)
            throw new InvalidDataException("Некорректный вызов действия. Задача остановлена.");
        var kind = call.Name switch { "launch_application" => LocalActionKind.LaunchApplication, "focus_application" => LocalActionKind.FocusApplication,
            _ => throw new InvalidDataException("Это действие не разрешено. Задача остановлена.") };
        using var json = JsonDocument.Parse(call.Arguments);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Некорректные аргументы действия.");
        var properties = json.RootElement.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != "target_id" || properties[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Некорректные аргументы действия.");
        var id = properties[0].Value.GetString()!;
        if (!targets.Any(t => t.Id == id)) throw new InvalidDataException("Цель недоступна. Выберите включённое приложение из каталога.");
        return new(kind, id);
    }

    public static async Task<string> RunAsync(IReadOnlyList<AiMessage> conversation, LocalActionExecutor executor,
        Func<AiActionRequest, CancellationToken, Task<AiActionTurn>> send, Action<LocalActionProgress>? progress, CancellationToken token)
    {
        AiRequest.ValidateInput(conversation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeLimit);
        var cancellation = deadline.Token;
        var targets = executor.Available;
        if (targets.Count == 0) throw new InvalidDataException("Нет доступных приложений. Для задач ИИ включите запись EXE в каталоге.");
        List<JsonElement> continuation = [];
        Dictionary<string, LocalAction> callIds = [];
        List<string> observations = [];
        for (var round = 0; round < MaxModelCalls; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            var turn = await send(new(conversation, targets, continuation.ToArray()), cancellation).WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (turn.Calls.Count == 0)
            {
                if (observations.Count == 0)
                    throw new InvalidDataException("Действия не выполнены. " + HttpAiClient.CleanAnswer(turn.Text));
                // Outcome comes from observations, never an unverified model claim.
                return string.Join("\n", observations);
            }
            if (turn.Calls.Count != 1) throw new InvalidDataException("Получено несколько действий одновременно. Задача остановлена.");
            var call = turn.Calls[0];
            var action = executor.ResolveModelAction(Parse(call, targets));
            if (callIds.TryGetValue(call.Id, out var previous))
            {
                // Replayed response must not be resent with duplicated protocol IDs or executed again.
                throw new InvalidDataException(previous == action ? "Повторный вызов действия. Задача остановлена без повторного выполнения." : "Идентификатор действия изменился. Задача остановлена.");
            }
            callIds.Add(call.Id, action);
            var result = await executor.ExecuteAsync(action, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!result.Verified) throw new InvalidDataException(result.Message);
            observations.Add(result.Message);
            continuation.AddRange(turn.Output);
            continuation.Add(JsonSerializer.SerializeToElement(new { type = "function_call_output", call_id = call.Id,
                output = JsonSerializer.Serialize(new { verified = result.Verified, message = result.Message }) }));
            if (continuation.Sum(x => x.GetRawText().Length) > 64_000) throw new InvalidDataException("Контекст задачи слишком большой. Задача остановлена.");
        }
        throw new InvalidDataException("Достигнут предел обращений к модели. Задача остановлена.");
    }
}
