namespace Jarvis.Core;

public record SkillResult(bool Verified, string Message, string? ArtifactPath = null, bool Silent = false);
public interface ISkill
{
    bool Handles(Intent intent);
    Task<SkillResult> ExecuteAsync(CommandRequest request, CancellationToken token);
}
public sealed class SkillDispatcher(IEnumerable<ISkill> skills)
{
    private readonly ISkill[] _skills = skills.ToArray();
    public async Task<SkillResult> ExecuteAsync(CommandRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Intent == Intent.Cancel) return new(true, "Отменено.");
        var skill = _skills.SingleOrDefault(s => s.Handles(request.Intent));
        if (skill == null) return new(false, request.Explanation ?? "Команду не понял. Список доступных фраз находится в меню «Справка».");
        try { return await skill.ExecuteAsync(request, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, "Не удалось выполнить действие. Проверь устройство или запись приложения в каталоге и попробуй снова."); }
    }
}
public sealed class DelegateSkill(Intent[] intents, Func<CommandRequest, CancellationToken, Task<SkillResult>> execute) : ISkill
{
    public bool Handles(Intent intent) => intents.Contains(intent);
    public Task<SkillResult> ExecuteAsync(CommandRequest request, CancellationToken token) => execute(request, token);
}
