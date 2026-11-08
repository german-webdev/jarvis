using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Jarvis.App.Services;
using Jarvis.Core;
using Microsoft.Win32;

namespace Jarvis.App;

public partial class CatalogWindow : Window
{
    private readonly MainViewModel _runtime;
    private readonly ObservableCollection<CatalogEntry> _entries = [];
    private ICollectionView? _view;
    private bool _scanning;
    public CatalogWindow(MainViewModel runtime)
    {
        InitializeComponent(); _runtime = runtime;
        foreach (var target in runtime.Targets) _entries.Add(new(target));
        _view = CollectionViewSource.GetDefaultView(_entries); _view.Filter = Filter;
        Entries.ItemsSource = _view;
    }
    private bool Filter(object item)
    {
        var entry = (CatalogEntry)item; var search = SearchBox.Text.Trim();
        if (SourceBox.SelectedIndex > 0 && entry.Source != (string)((ComboBoxItem)SourceBox.SelectedItem).Content) return false;
        return search.Length == 0 || (entry.Name + " " + entry.Source + " " + entry.AliasesText + " " + entry.AutomaticText).Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();
    private void SourceChanged(object sender, SelectionChangedEventArgs e) => _view?.Refresh();
    private async void ScanClick(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        _scanning = true; ScanButton.IsEnabled = false; StatusText.Text = "Ищу приложения Windows и установленные игры Steam…";
        try
        {
            var result = await AppDiscovery.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (!IsLoaded) return;
            var known = _entries.Select(x => x.Target.Id).ToHashSet(); int added = 0;
            foreach (var target in result.Targets) if (known.Add(target.Id)) { _entries.Add(new(target)); added++; }
            StatusText.Text = $"Найдено {result.Targets.Count}. Добавлено {added}. Всего в каталоге {_entries.Count}. " + string.Join(" ", result.Warnings.Distinct());
        }
        catch (Exception) { StatusText.Text = "Поиск не завершился. Можно повторить или добавить нужное приложение вручную."; }
        finally { _scanning = false; ScanButton.IsEnabled = true; }
    }
    private void AddClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Приложение или ярлык|*.exe;*.lnk", CheckFileExists = true, DereferenceLinks = false };
        if (picker.ShowDialog(this) != true) return;
        var file = Path.GetFullPath(picker.FileName);
        var id = "file:" + file.ToLowerInvariant();
        if (_entries.Any(x => x.Target.Id == id)) { StatusText.Text = "Этот файл уже есть в каталоге."; return; }
        var target = new LaunchTarget(id, Path.GetFileNameWithoutExtension(file), Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "shortcut" : "exe", file, "Добавлено вручную", [], true);
        _entries.Add(new(target)); SearchBox.Clear(); StatusText.Text = "Добавлено. При желании укажи своё название и нажми «Сохранить».";
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        Entries.CommitEdit(DataGridEditingUnit.Cell, true); Entries.CommitEdit(DataGridEditingUnit.Row, true);
        try { _runtime.SaveCatalog(_entries.Select(x => x.Capture())); DialogResult = true; }
        catch (Exception ex) { StatusText.Text = "Не удалось сохранить: " + ex.Message; }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
public sealed class CatalogEntry(LaunchTarget target)
{
    public LaunchTarget Target { get; } = target;
    public string Name => Target.Name;
    public string Source => Target.Source;
    public string AutomaticText => string.Join("; ", Target.AutomaticAliases);
    public bool Enabled { get; set; } = target.Enabled;
    public string AliasesText { get; set; } = string.Join("; ", target.Aliases ?? []);
    public LaunchTarget Capture() => Target with { Enabled = Enabled, Aliases = AliasesText.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray() };
}
