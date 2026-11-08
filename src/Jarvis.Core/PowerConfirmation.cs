namespace Jarvis.Core;

/// <summary>One-use local confirmation. No OS calls, fuzzy matching or model decisions.</summary>
public sealed class PowerConfirmation
{
    private Intent? _pending;
    private DateTimeOffset _expires;
    public bool HasPending => _pending.HasValue;
    public static bool IsPower(Intent intent) => intent is Intent.ShutdownComputer or Intent.RestartComputer or Intent.SleepComputer or Intent.HibernateComputer;
    public static string ActionName(Intent intent) => intent switch
    {
        Intent.ShutdownComputer => "выключение компьютера",
        Intent.RestartComputer => "перезагрузку компьютера",
        Intent.SleepComputer => "переход компьютера в режим сна",
        Intent.HibernateComputer => "гибернацию компьютера",
        _ => throw new ArgumentOutOfRangeException(nameof(intent))
    };
    public string Begin(Intent intent, DateTimeOffset now)
    {
        var action = ActionName(intent);
        _pending = intent;
        Renew(now);
        return $"Подтверди {action}. Скажи «Да, подтверждаю» или «Отмена».";
    }
    public void Renew(DateTimeOffset now) { if (HasPending) _expires = now.AddSeconds(30); }
    public void Clear() => _pending = null;
    public Intent? Consume(string answer, DateTimeOffset now)
    {
        var pending = _pending;
        Clear();
        if (now >= _expires || answer.Length > 100 || answer.Contains('?')) return null;
        var normalized = CommandRouter.Normalize(answer);
        return normalized is "да подтверждаю" or "подтверждаю" ? pending : null;
    }
}
