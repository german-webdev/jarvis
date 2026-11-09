using System.Windows;
using System.Windows.Controls;

namespace Jarvis.App.Controls;

public partial class WorkspaceShell : UserControl
{
    public static readonly DependencyProperty IsSidebarCollapsedProperty = DependencyProperty.Register(
        nameof(IsSidebarCollapsed), typeof(bool), typeof(WorkspaceShell),
        new PropertyMetadata(false, (owner, _) => ((WorkspaceShell)owner).ApplySidebarLayout()));

    public bool IsSidebarCollapsed
    {
        get => (bool)GetValue(IsSidebarCollapsedProperty);
        set => SetValue(IsSidebarCollapsedProperty, value);
    }

    public record Section(string Id, string Icon, string Label);
    public Func<string, bool>? Navigate { get; set; }
    public object? Page { get => PageContent.Content; set => PageContent.Content = value; }
    private string _selected = "home";
    private bool _updating;

    public WorkspaceShell()
    {
        InitializeComponent(); RefreshLanguage(); ApplySidebarLayout();
        Loaded += (_, _) => { Ui.Changed += RefreshLanguage; RefreshLanguage(); };
        Unloaded += (_, _) => Ui.Changed -= RefreshLanguage;
    }

    private void RefreshLanguage()
    {
        _updating = true;
        Navigation.ItemsSource = new[] {
            new Section("home", "\uE80F", Ui.Text("Главная")),
            new Section("catalog", "\uE71D", Ui.Text("Приложения и игры")),
            new Section("sites", "\uE774", Ui.Text("Сайты")),
            new Section("media", "\uE714", Ui.Text("Мультимедиа")),
            new Section("ai", "\uE8F2", Ui.Text("Подключения ИИ")),
            new Section("settings", "\uE713", Ui.Text("Настройки")),
            new Section("help", "\uE897", Ui.Text("Помощь")) };
        Navigation.SelectedItem = Navigation.Items.Cast<Section>().First(s => s.Id == _selected);
        BrandCaption.Text = Ui.Text("ГОЛОСОВОЙ ПОМОЩНИК");
        LocalCaption.Text = Ui.Text("Ваш персональный помощник");
        RefreshSidebarToggleLabel();
        _updating = false;
    }

    private void ToggleSidebar(object sender, RoutedEventArgs e) =>
        SetCurrentValue(IsSidebarCollapsedProperty, !IsSidebarCollapsed);

    private void ApplySidebarLayout()
    {
        if (SidebarColumn is null) return;

        // This changes only layout; the selected section and its live page stay intact.
        var collapsed = IsSidebarCollapsed;
        SidebarColumn.Width = new GridLength(collapsed ? 72 : 220);
        // The right separator occupies one DIP; compensate so the 48-DIP targets center at x=36.
        SidebarLayout.Margin = new Thickness(12, 26, collapsed ? 11 : 12, 16);
        BrandLayout.Margin = new Thickness(collapsed ? 0 : 12, 4, 0, 34);
        BrandIconColumn.Width = collapsed ? new GridLength(1, GridUnitType.Star) : new GridLength(44);
        BrandLogo.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        Grid.SetColumnSpan(BrandLogo, collapsed ? 2 : 1);
        BrandText.Visibility = FooterText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(SidebarToggle, collapsed ? 0 : 1);
        Grid.SetColumnSpan(SidebarToggle, collapsed ? 2 : 1);
        SidebarToggle.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SidebarToggleArrow.Data = System.Windows.Media.Geometry.Parse(collapsed
            ? "M 11,11 L 17,11 M 14,8 L 17,11 L 14,14"
            : "M 17,11 L 11,11 M 14,8 L 11,11 L 14,14");
        RefreshSidebarToggleLabel();
    }

    private void RefreshSidebarToggleLabel()
    {
        var label = Ui.Text(IsSidebarCollapsed ? "Развернуть боковую панель" : "Свернуть боковую панель");
        SidebarToggle.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle, label);
    }

    public void Select(string id)
    {
        _selected = id; _updating = true;
        Navigation.SelectedItem = Navigation.Items.Cast<Section>().First(s => s.Id == id);
        _updating = false;
    }

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Navigation.SelectedItem is not Section section) return;
        if (Navigate?.Invoke(section.Id) == false) Select(_selected);
        else _selected = section.Id;
    }
}
