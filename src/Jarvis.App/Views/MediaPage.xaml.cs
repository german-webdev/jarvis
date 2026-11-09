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

public partial class MediaPage : WorkspacePage
{
    private readonly MainViewModel _runtime;
    private readonly ObservableCollection<LaunchTarget> _players = [];
    private readonly ObservableCollection<MediaEntry> _folders = [];
    private readonly ObservableCollection<MediaEntry> _files = [];
    private readonly HashSet<string> _existingPlayers;
    private readonly ICollectionView _fileView;
    private CancellationTokenSource? _scan;
    public MediaPage(MainViewModel runtime, bool monitorPlayback = true)
    {
        InitializeComponent(); _runtime = runtime;
        _existingPlayers = runtime.Targets.Select(t => t.Id).ToHashSet();
        foreach (var player in runtime.MediaApps.Where(t => t.Enabled)) _players.Add(player);
        foreach (var entry in runtime.Media.Where(MediaLibrary.IsLibraryEntry))
            if (entry.Kind == "media-folder") _folders.Add(new(entry)); else _files.Add(new(entry));
        PlayersGrid.ItemsSource = _players; FoldersGrid.ItemsSource = _folders;
        _fileView = CollectionViewSource.GetDefaultView(_files);
        _fileView.Filter = item => item is MediaEntry entry && (entry.Name + " " + entry.Target + " " + entry.AliasesText).Contains(Search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        FilesGrid.ItemsSource = _fileView;
        Title = Ui.Text("Мультимедиа · JARVIS"); Heading.Text = Ui.Text("Мультимедиа");

        PlayersTab.Header = Ui.Text("Плееры"); FoldersTab.Header = Ui.Text("Папки"); FilesTab.Header = Ui.Text("Музыка и видео");
        PreferredLabel.Text = Ui.Text("Музыкальный плеер");

        FindPlayers.Content = Ui.Text("Найти плееры"); AddPlayer.Content = Ui.Text("Добавить плеер…"); RemovePlayer.Content = Ui.Text("Убрать из списка");
        FoldersHint.Text = Ui.Text("Добавьте папки и нажмите «Найти файлы».");
        AddFolder.Content = Ui.Text("Добавить папку…"); StandardFolders.Content = Ui.Text("Папки Windows"); ScanFolders.Content = Ui.Text("Найти файлы"); RemoveFolder.Content = Ui.Text("Убрать папку");

        AddFile.Content = Ui.Text("Добавить файл…"); RemoveFile.Content = Ui.Text("Убрать запись"); Search.ToolTip = Ui.Text("Поиск по названию или пути");
        SearchLabel.Text = Ui.Text("Поиск");
        PlayerNameColumn.Header = FolderNameColumn.Header = FileNameColumn.Header = Ui.Text("Название");
        PlayerPathColumn.Header = FolderPathColumn.Header = FilePathColumn.Header = Ui.Text("Расположение");
        FileKindColumn.Header = Ui.Text("Тип"); FileAliasesColumn.Header = Ui.Text("Свои названия");
        StopScan.Content = Ui.Text("Остановить поиск"); CancelButton.Content = Ui.Text("Отмена"); SaveButton.Content = Ui.Text("Сохранить");
        RemoveFile.ToolTip = RemoveFolder.ToolTip = Ui.Text("Файлы останутся на диске.");
        RefreshPlayers(runtime.Settings.MusicTargetId);
        TrackChanges(() => System.Text.Json.JsonSerializer.Serialize(new {
            Preferred = PreferredBox.SelectedValue as string,
            Players = _players.ToArray(), Folders = _folders.Select(f => f.Capture()).ToArray(), Files = _files.Select(f => f.Capture()).ToArray()
        }));
        Tabs.SelectedItem = FilesTab;
        InitializePlayback(monitorPlayback);
        Unloaded += (_, _) => _scan?.Cancel();
    }
    private void RefreshPlayers(string? preferred = null)
    {
        preferred ??= PreferredBox.SelectedValue as string;
        var choices = new[] { new Choice("", Ui.Text("Автоматически")) }.Concat(_players.Select(p => new Choice(p.Id, p.Name))).ToList();
        if (!string.IsNullOrEmpty(preferred) && choices.All(c => c.Id != preferred)) choices.Add(new(preferred, Ui.Text("Сохранённый плеер недоступен")));
        PreferredBox.ItemsSource = choices; PreferredBox.SelectedValue = preferred ?? "";
    }
    private void AddPlayerTarget(LaunchTarget target)
    { if (_players.All(t => t.Id != target.Id && !string.Equals(t.Target, target.Target, StringComparison.OrdinalIgnoreCase))) _players.Add(target); }
    private void SetBusy(bool busy)
    {
        PlayerActions.IsEnabled = FolderActions.IsEnabled = FileActions.IsEnabled = SaveButton.IsEnabled = !busy;
        FoldersGrid.IsReadOnly = FilesGrid.IsReadOnly = busy;
        StopScan.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void FindPlayersClick(object sender, RoutedEventArgs e)
    {
        if (_scan != null) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _scan = cancel; SetBusy(true);
        try
        {
            Status.Text = Ui.Text("Ищу мультимедийные приложения…");
            var result = await AppDiscovery.ScanAsync().WaitAsync(cancel.Token);
            if (!IsLoaded) return;
            foreach (var target in result.Targets.Where(MusicSelection.IsMediaApp))
                if (!_runtime.Targets.Any(t => t.Id == target.Id && !t.Enabled)) AddPlayerTarget(target);
            RefreshPlayers(); Status.Text = Ui.Format("Плееров в списке: {0}.", _players.Count);
            if (result.Warnings.Count > 0) Status.Text += " " + Ui.Text("Часть приложений недоступна для поиска.");
        }
        catch (OperationCanceledException) { Status.Text = Ui.Text("Поиск остановлен. Каталог сохранится только по кнопке «Сохранить»."); }
        catch (Exception ex) { Status.Text = Ui.Text("Не удалось выполнить поиск: ") + ex.Message; }
        finally { _scan = null; SetBusy(false); }
    }
    private void AddPlayerClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Ui.Text("Приложения (*.exe;*.lnk)|*.exe;*.lnk"), Title = Ui.Text("Выбери мультимедийный плеер") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var file = dialog.FileName;
        AddPlayerTarget(new("player:" + Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(file), Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "shortcut" : "exe", file, "Добавлено вручную", [], true, MediaPlayer: true));
        RefreshPlayers();
    }
    private void RemovePlayerClick(object sender, RoutedEventArgs e)
    {
        if (PlayersGrid.SelectedItem is not LaunchTarget player) return;
        if (_existingPlayers.Contains(player.Id)) { Status.Text = Ui.Text("Этот плеер добавлен через «Приложения и игры». Отключить его можно в том каталоге."); return; }
        _players.Remove(player); RefreshPlayers();
    }
    private void AddRoot(string path)
    {
        if (Directory.Exists(path) && _folders.All(f => !string.Equals(f.Target, path, StringComparison.OrdinalIgnoreCase))) _folders.Add(new(MediaLibrary.Folder(path)));
    }
    private void AddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Ui.Text("Выбери папку с музыкой или видео"), Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) foreach (var path in dialog.FolderNames) AddRoot(path);
    }
    private void StandardFoldersClick(object sender, RoutedEventArgs e)
    {
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)); AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Status.Text = Ui.Text("Добавлены существующие папки «Музыка» и «Видео» Windows. Нажми «Найти файлы».");
    }
    private static bool IsWithin(string file, string folder) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void RemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (FoldersGrid.SelectedItem is not MediaEntry folder) return;
        _folders.Remove(folder);
        foreach (var file in _files.Where(f => IsWithin(f.Target, folder.Target)).ToArray()) _files.Remove(file);
        Status.Text = Ui.Text("Папка и её файлы убраны из каталога. Файлы на диске остаются.");
    }
    private async void ScanFoldersClick(object sender, RoutedEventArgs e)
    {
        if (_scan != null || !Commit()) return;
        using var cancel = new CancellationTokenSource(); _scan = cancel; SetBusy(true);
        try
        {
            var folders = _folders.Select(f => f.Capture()).ToArray();
            Status.Text = Ui.Text("Ищу аудио и видео в добавленных папках…");
            var result = await Task.Run(() => MediaLibrary.Scan(folders, cancel.Token), cancel.Token);
            if (!IsLoaded) return;
            var known = _files.Select(f => f.Id).ToHashSet();
            foreach (var file in result.Files) if (known.Add(file.Id)) _files.Add(new(file));
            Status.Text = Ui.Format("Файлов в каталоге: {0}. Недоступные записи можно убрать вручную.", _files.Count);
            if (result.Warnings.Count > 0) Status.Text += " " + Ui.Format("Предупреждений: {0}. Проверь доступность папок или выбери папку меньшего размера.", result.Warnings.Count);
        }
        catch (OperationCanceledException) { Status.Text = Ui.Text("Поиск остановлен. Каталог сохранится только по кнопке «Сохранить»."); }
        catch (Exception ex) { Status.Text = Ui.Text("Не удалось выполнить поиск: ") + ex.Message; }
        finally { _scan = null; SetBusy(false); }
    }
    private void AddFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = Ui.Text("Аудио и видео|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.aiff;*.ape;*.mp4;*.mkv;*.avi;*.webm;*.mov;*.wmv;*.m4v;*.mpeg;*.mpg;*.ts") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var path in dialog.FileNames) { var file = MediaLibrary.FileEntry(path); if (_files.All(f => f.Id != file.Id)) _files.Add(new(file)); }
    }
    private void RemoveFileClick(object sender, RoutedEventArgs e) { if (FilesGrid.SelectedItem is MediaEntry entry) _files.Remove(entry); }
    private void SearchChanged(object sender, TextChangedEventArgs e) => _fileView?.Refresh();
    private void StopScanClick(object sender, RoutedEventArgs e) => _scan?.Cancel();
    private bool Commit() => FoldersGrid.CommitEdit(DataGridEditingUnit.Cell, true) && FoldersGrid.CommitEdit(DataGridEditingUnit.Row, true)
        && FilesGrid.CommitEdit(DataGridEditingUnit.Cell, true) && FilesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_scan != null || !Commit()) return;
        try
        {
            var updated = _folders.Concat(_files).Select(f => f.Capture()).Concat(_players.Where(p => !_existingPlayers.Contains(p.Id))).ToArray();
            // Preserve disabled records; discovery never re-enables them implicitly.
            updated = updated.Concat(_runtime.Media.Where(t => !t.Enabled && !MediaLibrary.IsLibraryEntry(t) && updated.All(u => u.Id != t.Id))).ToArray();
            var previous = _runtime.Media.ToArray();
            _runtime.SaveMedia(updated);
            try { _runtime.ApplySettings(_runtime.Settings with { MusicTargetId = PreferredBox.SelectedValue as string }); }
            catch { _runtime.SaveMedia(previous); throw; }
            Finish(true);
        }
        catch (Exception ex) { Status.Text = Ui.Text("Не удалось сохранить: ") + Ui.Text(ex.Message); }
    }
    private void CancelClick(object sender, RoutedEventArgs e) { _scan?.Cancel(); Finish(false); }
}

public sealed class MediaEntry(LaunchTarget target)
{
    public string Id => target.Id;
    public string Target => target.Target;
    public string Name { get; set; } = target.Name;
    public string AliasesText { get; set; } = string.Join("; ", target.Aliases ?? []);
    public string KindLabel => Ui.Text(target.Kind == "audio" ? "Музыка" : "Видео");
    public LaunchTarget Capture() => target with { Name = Name.Trim(), Aliases = NameVariants.SplitCustom([AliasesText]).ToArray() };
}
