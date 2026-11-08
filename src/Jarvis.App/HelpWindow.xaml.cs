using System.Windows;
using System.Windows.Controls;

namespace Jarvis.App;

public partial class HelpWindow : Window
{
    private sealed record Category(string Id, string Label);
    private sealed record Card(string Id, string Title, string Description, string[] Commands, string Note);
    private readonly Card[] _cards;
    public HelpWindow()
    {
        InitializeComponent();
        Title = Ui.Text("Команды JARVIS"); Heading.Text = Ui.Text("Что сказать JARVIS");
        Intro.Text = Ui.Text("В режиме ожидания сначала скажи «Джарвис», дождись готовности и произнеси команду. Примеры ниже — на русском языке.");
        SearchLabel.Text = Ui.Text("Найти команду");
        SearchBox.ToolTip = Ui.Text("Например: музыка, браузер, папка");
        EmptyState.Text = Ui.Text("Ничего не найдено. Попробуй другое слово или выбери все категории.");
        Footer.Text = Ui.Text("Кнопка «Стоп» и Esc на главном экране останавливают прослушивание. Здесь Esc закрывает справку.");
        CloseButton.Content = Ui.Text("Закрыть");
        _cards =
        [
            Make("apps", "Приложения и игры", "Запускай программы и установленные игры по названию из каталога.",
                ["Открой браузер", "Запусти Телеграм / Steam / Xbox", "Запусти Сабнавтику / Форзу / Word / Excel"],
                "Каталог: JARVIS → Приложения и игры. Свои названия необязательны; разделяй их запятой или точкой с запятой."),
            Make("web", "Сайты и почта", "Открывай сохранённые сайты в браузере по умолчанию.",
                ["Открой YouTube / Кинопоиск / Ozon", "Открой почту / Открой почту Яндекса", "Открой Яндекс Еду / ПлатиМаркет"],
                "Адреса и названия: JARVIS → Сайты. Поиск роликов и действия внутри страниц пока не реализованы."),
            Make("music", "Музыка и видео", "Управляй выбранным плеером и переключай треки короткими фразами.",
                ["Включи музыку", "Пауза / Стоп / Останови / Выключи музыку", "Продолжи", "Дальше / Следующий / Следующий трек", "Назад / Предыдущий / Предыдущий трек"],
                "Плеер: Настройки → Музыка. «Выключи музыку» ставит воспроизведение на паузу и оставляет приложение открытым."),
            Make("volume", "Громкость", "Меняй громкость плеера или всей системы.",
                ["Громче / Сделай громче / Сделай погромче", "Тише / Сделай тише / Сделай потише", "Систему громче / Систему тише", "Громкость 30 процентов", "Выключи звук / Включи звук"],
                "«Тише» и «громче» меняют громкость плеера на 10 процентных пунктов. «Систему…» и точный уровень относятся к Windows."),
            Make("windows", "Окна и папки", "Переключайся между программами и открывай каталоги установки.",
                ["Переключись на браузер / Телеграм", "Закрой Телеграм / Закрой игру", "Закрой окно / Сверни окно", "Открой папку с Сабнавтикой", "Открой каталог Форзы / Открой папку Телеграма"],
                "Закрытие обычное: приложение может предложить сохранить файл или уйти в трей. «Закрой игру» работает при одной найденной запущенной игре Steam."),
            Make("daily", "Скриншоты и информация", "Сохраняй изображение экранов и узнавай время и дату.",
                ["Сделай скриншот", "Открой скриншоты", "Сколько времени / Какое сегодня число", "Покажи команды"],
                "Скриншот всех мониторов сохраняется одним PNG в data/screenshots. «Покажи команды» озвучивает краткую справку."),
            Make("ai", "Вопросы нейросети", "Задавай свободные вопросы через выбранное подключение.",
                ["Спроси нейросеть, почему небо голубое", "Вопрос к нейросети: объясни простыми словами"],
                "Сначала настрой и включи подключение: JARVIS → Нейросети. Сейчас ИИ отвечает на вопросы; управление компьютером через ИИ — следующий этап."),
            Make("choice", "Уточнения и отмена", "Если подходят несколько приложений или игр, ответь после вопроса JARVIS.",
                ["Вариант один / Вариант два", "Название нужной игры или номер её части", "Отмена / Стоп / Останови"],
                "При уточнении повторять имя «Джарвис» не нужно. «Стоп» и «останови» отменяют выбор; вне уточнения они ставят плеер на паузу.")
        ];
        Categories.ItemsSource = new[] { new Category("all", Ui.Text("Все команды")) }.Concat(_cards.Select(c => new Category(c.Id, c.Title))).ToArray();
        Categories.SelectedIndex = 0;
    }
    private static Card Make(string id, string title, string description, string[] commands, string note) =>
        new(id, Ui.Text(title), Ui.Text(description), commands, Ui.Text(note));
    private void SearchChanged(object sender, TextChangedEventArgs e) => Refresh();
    private void CategoryChanged(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (_cards == null) return;
        var category = (Categories.SelectedItem as Category)?.Id ?? "all";
        var query = SearchBox.Text.Trim();
        var found = _cards.Where(c => (category == "all" || c.Id == category)
            && (c.Title + " " + c.Description + " " + c.Note + " " + string.Join(" ", c.Commands)).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        Cards.ItemsSource = found;
        EmptyState.Visibility = found.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.ScrollToTop();
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
