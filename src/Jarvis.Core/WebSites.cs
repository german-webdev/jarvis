namespace Jarvis.Core;

public static class WebSites
{
    public static bool TryNormalizeAddress(string? address, out string normalized)
    {
        normalized = "";
        var value = address?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) return false;
        if (!value.Contains(':')) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0) return false;
        normalized = uri.AbsoluteUri;
        return normalized.Length <= 2048;
    }

    public static List<LaunchTarget> Defaults() =>
    [
        new("site:youtube", "YouTube", "url", "https://www.youtube.com/", "Сайт", ["ютуб", "ютюб", "ю туб", "you tube"], true),
        new("site:kinopoisk", "Кинопоиск", "url", "https://www.kinopoisk.ru/", "Сайт", ["кино поиск"], true),
        new("site:gmail", "Gmail", "url", "https://mail.google.com/", "Сайт", ["почта", "гугл почта", "почта гугл", "почта гугла", "почта google", "почту гугла", "почту гугл", "почту google", "джимейл", "джи мейл"], true),
        new("site:yandex-mail", "Яндекс Почта", "url", "https://mail.yandex.ru/", "Сайт", ["почта яндекс", "почта яндекса", "почту яндекс", "почту яндекса", "яндекс почта"], true),
        new("site:ozon", "Ozon", "url", "https://www.ozon.ru/", "Сайт", ["озон"], true),
        new("site:yandex-food", "Яндекс Еда", "url", "https://eda.yandex.ru/", "Сайт", ["еда яндекс", "еда яндекса"], true),
        new("site:plati", "ПлатиМаркет", "url", "https://plati.market/", "Сайт", ["плати маркет", "плати", "plati market"], true)
    ];
}
