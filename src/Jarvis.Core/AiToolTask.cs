using System.Text.Json;

namespace Jarvis.Core;

public sealed record AiToolDefinition(string Name, string Description, JsonElement Parameters);
public sealed record AiToolObservation(bool Verified, string Data, string Display, string? SourceUrl = null);
public sealed record AiToolExchange(IReadOnlyList<JsonElement> ModelOutput, string CallId, AiToolObservation Observation);

public interface IAiToolSession : IAsyncDisposable
{
    IReadOnlyList<AiToolDefinition> Tools { get; }
    Task<AiToolObservation> InvokeAsync(string name, JsonElement arguments, CancellationToken token);
}

public static class AiToolTask
{
    private const int MaxActions = 4;

    public static async Task<SkillResult> RunAsync(IAiToolSession session, bool needsObservation,
        Func<IReadOnlyList<AiToolExchange>, CancellationToken, Task<AiActionTurn>> send, CancellationToken token)
    {
        List<AiToolExchange> context = [];
        HashSet<string> callIds = new(StringComparer.Ordinal);
        AiToolObservation? observation = null;
        for (var step = 0; step <= MaxActions; step++)
        {
            token.ThrowIfCancellationRequested();
            var turn = await send(context.ToArray(), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (turn.Calls.Count == 0)
            {
                if (observation != null)
                    return new SkillResult(observation.Verified, observation.Display) { SourceUrl = observation.SourceUrl };
                if (needsObservation)
                    return new(false, "Браузерное действие не подтверждено.");
                return new(true, HttpAiClient.CleanAnswer(turn.Text));
            }
            if (step == MaxActions) throw new InvalidDataException("Достигнут предел действий в браузере.");
            if (turn.Calls.Count != 1) throw new InvalidDataException("Браузерные действия выполняются по одному.");
            var call = turn.Calls[0];
            if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 200 || !callIds.Add(call.Id)
                || call.Arguments.Length > 4096 || !session.Tools.Any(tool => tool.Name == call.Name))
                throw new InvalidDataException("Недопустимый или повторный вызов инструмента.");
            using var arguments = JsonDocument.Parse(call.Arguments);
            observation = await session.InvokeAsync(call.Name, arguments.RootElement, token);
            token.ThrowIfCancellationRequested();
            context.Add(new(turn.Output, call.Id, observation));
            if (context.Sum(step => step.ModelOutput.Sum(item => item.GetRawText().Length) + step.Observation.Data.Length) > 160_000)
                throw new InvalidDataException("Контекст задачи слишком большой. Задача остановлена.");
        }
        throw new InvalidOperationException("Unreachable tool task state.");
    }
}
