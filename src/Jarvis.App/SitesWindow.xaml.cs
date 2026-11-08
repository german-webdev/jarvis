using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Core;

namespace Jarvis.App;

public partial class SitesWindow : Window
{
    private readonly MainViewModel _runtime;
    private readonly ObservableCollection<SiteEntry> _entries;
    public SitesWindow(MainViewModel runtime)
    {
        InitializeComponent(); _runtime = runtime;
        _entries = new(runtime.Sites.Select(t => new SiteEntry(t)));
        Entries.ItemsSource = _entries;
    }
    private void AddClick(object sender, RoutedEventArgs e)
    {
        var entry = new SiteEntry(new("site:" + Guid.NewGuid().ToString("N"), "Новый сайт", "url", "", "Сайт", [], true));
        _entries.Add(entry); Entries.SelectedItem = entry; Entries.ScrollIntoView(entry);
        StatusText.Text = "Укажи название и адрес, например https://example.com. Дополнительные названия необязательны.";
    }
    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (Entries.SelectedItem is SiteEntry entry) _entries.Remove(entry);
        StatusText.Text = "Удаление применится после сохранения. «Отмена» сохранит прежний список.";
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!Entries.CommitEdit(DataGridEditingUnit.Cell, true) || !Entries.CommitEdit(DataGridEditingUnit.Row, true)) return;
        try { _runtime.SaveSites(_entries.Select(e => e.Capture())); DialogResult = true; }
        catch (Exception error) { StatusText.Text = "Не удалось сохранить: " + error.Message; }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class SiteEntry(LaunchTarget target)
{
    public string Name { get; set; } = target.Name;
    public string Address { get; set; } = target.Target;
    public string AliasesText { get; set; } = string.Join("; ", target.Aliases ?? []);
    public bool Enabled { get; set; } = target.Enabled;
    public LaunchTarget Capture()
    {
        if (!WebSites.TryNormalizeAddress(Address, out var address))
            throw new InvalidDataException($"Сайт «{Name}»: нужен адрес HTTP или HTTPS без пробелов и логина в адресе.");
        var value = target with { Name = Name.Trim(), Target = address, Enabled = Enabled, Aliases = NameVariants.SplitCustom([AliasesText]).ToArray() };
        if (!value.IsValid) throw new InvalidDataException("Проверь название сайта (до 150 символов) и дополнительные названия (до 20, по 100 символов).");
        return value;
    }
}
