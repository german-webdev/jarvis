using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core;

namespace Jarvis.App;

public partial class AiConnectionsWindow : Window
{
    private readonly AiService _service;
    private readonly ObservableCollection<AiConnection> _connections;
    private AiConnection? _editing;
    private bool _loading;
    private CancellationTokenSource? _operation;
    private Uri? _browserUri;

    public AiConnectionsWindow(AiService service)
    {
        InitializeComponent(); _service = service;
        _connections = new(service.Configuration.Connections);
        ProviderBox.ItemsSource = new[] { new Choice("api", Ui.Text("API — совместимый с OpenAI")), new Choice("ollama", "Ollama"),
            new Choice("chatgpt", Ui.Text("ChatGPT — подписка")), new Choice("claude", Ui.Text("Claude — подписка")) };
        ConnectionsList.ItemsSource = _connections; ActiveBox.ItemsSource = _connections;
        ActiveBox.SelectedValue = service.Configuration.ActiveConnectionId;
        EnabledBox.IsChecked = service.Configuration.Enabled; FallbackBox.IsChecked = service.Configuration.UseForUnknownCommands;
        StatusText.Text = Ui.Text(service.LoadWarning ?? "Подключения выключены, пока ты сам их не настроишь и не включишь.");
        if (service.Configuration.Enabled) StatusText.Text = Ui.Text("Используется выбранное подключение. Изменения применяются после сохранения.");
        Closed += (_, _) => _operation?.Cancel();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        CaptureCurrent();
        Load(ConnectionsList.SelectedItem as AiConnection);
    }

    private void Load(AiConnection? connection)
    {
        _loading = true;
        try
        {
            _editing = connection; Editor.IsEnabled = connection != null;
            NameBox.Text = connection?.Name ?? ""; ProviderBox.SelectedValue = connection?.Provider;
            EndpointBox.Text = connection?.Endpoint ?? ""; ModelBox.Text = connection?.Model ?? "";
            ModelBox.ItemsSource = null;
            KeyBox.Clear(); RemoveKeyBox.IsChecked = false;
            KeyStatus.Text = Ui.Text(string.IsNullOrEmpty(connection?.ProtectedApiKey) ? "Ключ не сохранён." : "Ключ сохранён. Оставь поле пустым, чтобы сохранить его.");
            UpdatePanels();
        }
        finally { _loading = false; }
    }

    private void UpdatePanels()
    {
        var provider = (string?)ProviderBox.SelectedValue;
        var subscription = provider is "chatgpt" or "claude";
        HttpPanel.Visibility = subscription ? Visibility.Collapsed : Visibility.Visible;
        SubscriptionPanel.Visibility = subscription ? Visibility.Visible : Visibility.Collapsed;
        ModelsButton.Visibility = provider == "claude" ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.Content = provider == "chatgpt" ? Ui.Text("Продолжить с ChatGPT") : Ui.Text("Войти через браузер");
        SubscriptionHint.Text = Ui.Text(provider == "chatgpt"
            ? "Вход откроется в браузере. Подтверди доступ JARVIS к подписке ChatGPT и вернись сюда. Ключ и отдельная программа не нужны."
            : "Вход выполняет компонент Claude Code, который JARVIS находит автоматически. Подтверждение проходит на странице Claude в браузере.");
        AuthStatus.Text = Ui.Text("Вход ещё не проверен.");
        if (provider == "chatgpt" && _editing != null)
        {
            try
            {
                var account = _service.ChatGpt.Read(_editing);
                if (account?.SignedIn == true) AuthStatus.Text = Ui.Format("Аккаунт ChatGPT: {0}", account.Email.Length > 0 ? account.Email : _editing.Name);
            }
            catch (InvalidDataException error) { AuthStatus.Text = Ui.Text(error.Message); }
        }
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _editing == null) return;
        EndpointBox.Text = (string?)ProviderBox.SelectedValue == "ollama" ? "http://localhost:11434" : "";
        ModelBox.Text = ""; ModelBox.ItemsSource = null; KeyBox.Clear(); RemoveKeyBox.IsChecked = true;
        UpdatePanels();
    }

    private AiConnection? CaptureCurrent()
    {
        if (_editing == null) return null;
        var provider = (string?)ProviderBox.SelectedValue ?? "api";
        var endpoint = EndpointBox.Text.Trim().TrimEnd('/');
        var encrypted = RemoveKeyBox.IsChecked == true || provider != _editing.Provider
            || !string.Equals(endpoint, _editing.Endpoint.TrimEnd('/'), StringComparison.Ordinal) ? "" : _editing.ProtectedApiKey;
        if (!string.IsNullOrEmpty(KeyBox.Password)) encrypted = AiService.Protect(KeyBox.Password);
        var captured = _editing with { Name = NameBox.Text.Trim(), Provider = provider, Endpoint = endpoint,
            Model = ModelBox.Text.Trim(), CliPath = "", ProtectedApiKey = provider is "chatgpt" or "claude" ? "" : encrypted };
        var active = ActiveBox.SelectedValue;
        _loading = true;
        try
        {
            var index = _connections.IndexOf(_editing);
            var selected = Equals(ConnectionsList.SelectedItem, _editing);
            if (index >= 0) _connections[index] = captured;
            if (selected) ConnectionsList.SelectedItem = captured;
            ActiveBox.SelectedValue = active;
            _editing = captured;
        }
        finally { _loading = false; }
        KeyBox.Clear(); RemoveKeyBox.IsChecked = false;
        KeyStatus.Text = Ui.Text(string.IsNullOrEmpty(captured.ProtectedApiKey) ? "Ключ не сохранён." : "Ключ сохранён. Оставь поле пустым, чтобы сохранить его.");
        return captured;
    }

    private void AddClick(object sender, RoutedEventArgs e)
    {
        CaptureCurrent();
        var item = new AiConnection { Name = Ui.Format("Подключение {0}", _connections.Count + 1) };
        _connections.Add(item); ConnectionsList.SelectedItem = item;
    }
    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var selected = _editing; _editing = null; _connections.Remove(selected); Load(null);
        StatusText.Text = Ui.Text("Удаление применится после сохранения. Для выхода из аккаунта сначала используй «Выйти из аккаунта».");
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureCurrent();
            _service.Save(new AiConfiguration { Enabled = EnabledBox.IsChecked == true, UseForUnknownCommands = FallbackBox.IsChecked == true,
                ActiveConnectionId = ActiveBox.SelectedValue as string, Connections = _connections.ToList() });
            StatusText.Text = Ui.Text("Подключения сохранены. Вход и проверка запускаются только по твоей команде.");
        }
        catch (Exception error) { StatusText.Text = Ui.Text(error is InvalidDataException ? error.Message : "Не удалось сохранить подключения."); }
    }

    private async Task OperateAsync(Func<AiConnection, CancellationToken, Task<string>> operation, bool requireSaved = false)
    {
        if (_operation != null) return;
        try
        {
            var selected = CaptureCurrent() ?? throw new InvalidDataException("Сначала добавь или выбери подключение.");
            selected.Validate();
            if (requireSaved)
            {
                // Save this profile only; authorizing never enables AI or commits other drafts.
                var saved = _service.Configuration;
                _service.Save(saved with { Connections = saved.Connections.Where(c => c.Id != selected.Id).Append(selected).ToList() });
            }
            _operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Editor.IsEnabled = false; ConnectionsList.IsEnabled = false; ManageButtons.IsEnabled = false; GeneralOptions.IsEnabled = false; SaveButton.IsEnabled = false; CancelOperation.Visibility = Visibility.Visible;
            ReportStatus("Выполняю запрос. При входе заверши авторизацию в браузере.");
            ReportStatus(await operation(selected, _operation.Token));
        }
        catch (OperationCanceledException) { ReportStatus("Операция отменена или истекло время ожидания."); }
        catch (Exception error) { ReportStatus(error is InvalidDataException ? error.Message : "Подключение не удалось. Проверь интернет и доступность сервиса, затем повтори вход."); }
        finally
        {
            _operation?.Dispose(); _operation = null;
            _browserUri = null; ReopenBrowser.Visibility = Visibility.Collapsed;
            Editor.IsEnabled = _editing != null; ConnectionsList.IsEnabled = true; ManageButtons.IsEnabled = true; GeneralOptions.IsEnabled = true; SaveButton.IsEnabled = true; CancelOperation.Visibility = Visibility.Collapsed;
        }
    }
    private async void ModelsClick(object sender, RoutedEventArgs e) => await OperateAsync(async (connection, token) =>
    {
        var models = await _service.ModelsAsync(connection, token);
        var chosen = ModelBox.Text; ModelBox.ItemsSource = models; ModelBox.Text = chosen;
        return Ui.Format("Найдено моделей: {0}. Выбери нужную и сохрани.", models.Length);
    });
    private async void CheckClick(object sender, RoutedEventArgs e) => await OperateAsync(async (connection, token) =>
    {
        if (connection.Provider == "chatgpt")
        {
            var account = await _service.ChatGpt.ReadyAsync(connection, token);
            await _service.ModelsAsync(connection, token);
            return Ui.Format("Аккаунт ChatGPT: {0}", account.Email.Length > 0 ? account.Email : connection.Name);
        }
        if (connection.IsSubscription) return await _service.Subscriptions.IsSignedInAsync(connection, token)
            ? "Вход по подписке выполнен. Доступ к модели проверяется при отправке вопроса." : "Вход не выполнен. Сохрани подключение и нажми «Войти через браузер».";
        await _service.ModelsAsync(connection, token);
        return "Сервис доступен. Список моделей получен; генерация ответа не запускалась.";
    });
    private async void SignInClick(object sender, RoutedEventArgs e) => await OperateAsync(async (connection, token) =>
    {
        if (connection.Provider == "chatgpt")
        {
            await _service.ChatGpt.SignInAsync(connection, uri =>
            {
                SetBrowserLink(uri);
                try { ChatGptSignIn.OpenBrowser(uri); }
                catch { ReportStatus("Браузер не открылся. Нажми «Открыть браузер повторно»."); }
            }, new Progress<string>(ReportStatus), token);
            var account = _service.ChatGpt.Read(connection)!;
            return Ui.Format("Аккаунт ChatGPT: {0}", account.Email.Length > 0 ? account.Email : connection.Name);
        }
        await _service.Subscriptions.SignInAsync(connection, token, uri => Dispatcher.Invoke(() => SetBrowserLink(uri)));
        return "Вход по подписке выполнен. Доступ к модели проверяется при отправке вопроса.";
    }, requireSaved: true);
    private async void SignOutClick(object sender, RoutedEventArgs e) => await OperateAsync(async (connection, token) =>
    {
        if (connection.Provider == "chatgpt")
        {
            if (!await _service.ChatGpt.SignOutAsync(connection, token))
                return "Вход удалён из JARVIS. Выход на сервере не подтверждён; отключи приложение в настройках ChatGPT.";
        }
        else await _service.Subscriptions.SignOutAsync(connection, token);
        return "Выход из аккаунта подключения выполнен.";
    }, requireSaved: true);
    private void ReportStatus(string text) { StatusText.Text = Ui.Text(text); AuthStatus.Text = Ui.Text(text); }
    private void SetBrowserLink(Uri uri) { _browserUri = uri; ReopenBrowser.Visibility = Visibility.Visible; }
    private void ReopenBrowserClick(object sender, RoutedEventArgs e)
    {
        try { if (_browserUri != null) ChatGptSignIn.OpenBrowser(_browserUri); }
        catch { ReportStatus("Не удалось открыть браузер по умолчанию. Проверь его настройки в Windows."); }
    }
    private void CancelOperationClick(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
