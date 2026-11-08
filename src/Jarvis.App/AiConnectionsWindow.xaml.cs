using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core;
using Microsoft.Win32;

namespace Jarvis.App;

public partial class AiConnectionsWindow : Window
{
    private readonly AiService _service;
    private readonly ObservableCollection<AiConnection> _connections;
    private AiConnection? _editing;
    private bool _loading;
    private CancellationTokenSource? _operation;

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
            ModelBox.ItemsSource = null; CliBox.Text = connection?.CliPath ?? "";
            KeyBox.Clear(); RemoveKeyBox.IsChecked = false;
            KeyStatus.Text = Ui.Text(string.IsNullOrEmpty(connection?.ProtectedApiKey) ? "Ключ не сохранён." : "Ключ сохранён. Оставь поле пустым, чтобы сохранить его.");
            UpdatePanels();
        }
        finally { _loading = false; }
    }

    private void UpdatePanels()
    {
        var subscription = (string?)ProviderBox.SelectedValue is "chatgpt" or "claude";
        HttpPanel.Visibility = subscription ? Visibility.Collapsed : Visibility.Visible;
        SubscriptionPanel.Visibility = subscription ? Visibility.Visible : Visibility.Collapsed;
        ModelsButton.Visibility = subscription ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _editing == null) return;
        EndpointBox.Text = (string?)ProviderBox.SelectedValue == "ollama" ? "http://localhost:11434" : "";
        ModelBox.Text = ""; ModelBox.ItemsSource = null; CliBox.Text = ""; KeyBox.Clear(); RemoveKeyBox.IsChecked = true;
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
            Model = ModelBox.Text.Trim(), CliPath = CliBox.Text.Trim(), ProtectedApiKey = provider is "chatgpt" or "claude" ? "" : encrypted };
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
    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Executable|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) CliBox.Text = picker.FileName;
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
            if (requireSaved && !_service.Configuration.Connections.Contains(selected))
                throw new InvalidDataException("Сначала сохрани подключение, затем выполни вход или выход.");
            _operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            Editor.IsEnabled = false; ConnectionsList.IsEnabled = false; ManageButtons.IsEnabled = false; GeneralOptions.IsEnabled = false; SaveButton.IsEnabled = false; CancelOperation.Visibility = Visibility.Visible;
            StatusText.Text = Ui.Text("Выполняю запрос. При входе заверши авторизацию в браузере.");
            StatusText.Text = Ui.Text(await operation(selected, _operation.Token));
        }
        catch (OperationCanceledException) { StatusText.Text = Ui.Text("Операция отменена или истекло время ожидания."); }
        catch (Exception error) { StatusText.Text = Ui.Text(error is InvalidDataException ? error.Message : "Подключение не удалось. Проверь адрес, официальный клиент и параметры доступа."); }
        finally
        {
            _operation?.Dispose(); _operation = null;
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
        if (connection.IsSubscription) return await _service.Subscriptions.IsSignedInAsync(connection, token)
            ? "Вход по подписке выполнен. Доступ к модели проверяется при отправке вопроса." : "Вход не выполнен. Сохрани подключение и нажми «Войти через браузер».";
        await _service.ModelsAsync(connection, token);
        return "Сервис доступен. Список моделей получен; генерация ответа не запускалась.";
    });
    private async void SignInClick(object sender, RoutedEventArgs e) => await OperateAsync(async (connection, token) =>
    { await _service.Subscriptions.SignInAsync(connection, token); return "Вход по подписке выполнен. Доступ к модели проверяется при отправке вопроса."; }, requireSaved: true);
    private async void SignOutClick(object sender, RoutedEventArgs e) => await OperateAsync(async (connection, token) =>
    { await _service.Subscriptions.SignOutAsync(connection, token); return "Выход из аккаунта подключения выполнен."; }, requireSaved: true);
    private void CancelOperationClick(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
