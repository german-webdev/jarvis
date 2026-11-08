using System.Text.RegularExpressions;

namespace Jarvis.Core;

public sealed record ConversationTurn(CommandRequest? Command, string? Question = null)
{
    public bool NeedsAnswer => Question != null;
}

/// <summary>A bounded clarification over a snapshot of IDs, never a new free-form launch.</summary>
public sealed class LaunchConversation
{
    private string[] _ids = [];
    private DateTimeOffset _expires;
    private int _retries;
    public bool HasPending => _ids.Length > 0;
    public IReadOnlyList<string> CandidateIds => _ids;
    public void Clear() { _ids = []; _retries = 0; }
    public void Renew(DateTimeOffset now) { if (HasPending) _expires = now.AddSeconds(30); }

    public ConversationTurn Resolve(string text, IReadOnlyList<LaunchTarget> targets, DateTimeOffset now)
    {
        var request = CommandRouter.Resolve(text, targets);
        var answer = CommandRouter.Normalize(text);
        answer = Regex.Replace(answer, @"^(?:джарвис|jarvis) | пожалуйста$", "");
        if (request.Intent == Intent.Cancel || answer is "нет" or "не надо" or "ничего") { Clear(); return new(new(Intent.Cancel)); }
        // Explicit new commands replace the old question. A bare answer never starts a new action.
        if (request.Intent != Intent.Unknown || request.CandidateIds != null)
        {
            Clear();
            if (request.CandidateIds == null) return new(request);
            var candidates = request.CandidateIds.Select(id => targets.Single(t => t.Id == id)).ToArray();
            if (candidates.Length > 6) return new(new(Intent.Unknown, Explanation: $"Нашёл {candidates.Length} совпадений. Назови игру точнее, например с номером части."));
            _ids = candidates.Select(t => t.Id).ToArray(); Renew(now);
            return new(null, Question(candidates));
        }
        if (!HasPending) return new(request);
        if (now >= _expires) { Clear(); return new(new(Intent.Unknown, Explanation: "Время выбора истекло. Повтори команду запуска.")); }
        var options = _ids.Select(id => targets.FirstOrDefault(t => t.Enabled && t.Id == id)).ToArray();
        // Do not shift positional choices if an entry disappeared or was disabled.
        if (options.Any(t => t == null)) { Clear(); return new(new(Intent.Unknown, Explanation: "Каталог изменился. Повтори команду запуска.")); }
        var available = options.Select(t => t!).ToArray();
        LaunchTarget[] matches = [];
        if (!text.Contains('?') && !Regex.IsMatch(text, @"[-−]\s*\d"))
        {
            var position = Regex.Match(answer, @"^(?:вариант (.+)|(.+) вариант)$");
            if (position.Success)
            {
                var n = NameVariants.SelectionNumber(position.Groups[1].Success ? position.Groups[1].Value : position.Groups[2].Value);
                if (n >= 1 && n <= available.Length) matches = [available[n.Value - 1]];
            }
            else if (NameVariants.SelectionNumber(answer) is { } version)
                matches = available.Where(t => NameVariants.VersionNumbers(t.Name).Contains(version)).ToArray();
            else matches = available.Where(t => t.Names.Any(n => CommandRouter.Normalize(n) == answer)).ToArray();
        }
        if (matches.Length == 1) { Clear(); return new(new(Intent.LaunchTarget, matches[0].Id)); }
        if (++_retries >= 2) { Clear(); return new(new(Intent.Unknown, Explanation: "Выбор не понял. Запуск отменён; повтори команду с полным названием.")); }
        Renew(now);
        return new(null, "Уточни выбор. " + Question(available));
    }
    private static string Question(IReadOnlyList<LaunchTarget> targets)
    {
        var choices = targets.Select((t, i) =>
        {
            bool duplicate = targets.Count(other => other.Name == t.Name) > 1;
            int limit = duplicate ? 28 : 40;
            var label = t.Name.Length > limit ? t.Name[..limit] : t.Name;
            var source = t.Source ?? "";
            if (duplicate) label += " (" + source[..Math.Min(source.Length, 12)] + ")";
            return $"Вариант {i + 1}: {label}.";
        });
        return "Что запустить? " + string.Join(" ", choices) + " Назови часть, полное название или номер варианта. Для отмены скажи: отмена.";
    }
}
