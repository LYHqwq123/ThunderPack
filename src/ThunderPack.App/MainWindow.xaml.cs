using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ThunderPack.Core;

namespace ThunderPack.App;

public partial class MainWindow : Window
{
    private readonly PackEngine _engine = new();
    private readonly List<ModPackage> _packages = [];
    private readonly ObservableCollection<ModRow> _rows = [];
    private readonly string[] _args;
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderPack");
    private CancellationTokenSource? _operation;
    private bool _busy = true;
    private bool _ready;
    private bool _dependenciesOnly;
    private Dictionary<string, bool> _enabledSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DependencyIssue> _issues = [];

    public MainWindow(string[] args)
    {
        _args = args;
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < args.Length) _dataDirectory = Path.GetFullPath(args[dataIndex + 1]);
        _engine.CacheDirectory = Path.Combine(_dataDirectory, "cache");
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/pack-icon.png"));
        ModListBox.ItemsSource = _rows;
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        PreviewKeyDown += Window_KeyDown;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在恢复工作区…";
            WorkProgress.Visibility = Visibility.Visible;
            Directory.CreateDirectory(_dataDirectory);
            if (!_args.Contains("--no-restore")) await RestoreStateAsync();
            if (StatusText.Text == "正在恢复工作区…") StatusText.Text = "就绪 · 拖入 ZIP 或点击「添加 Mod」开始";
            _ready = true;
            EndWork();
            var files = _args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a) && a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length > 0) await ImportPathsAsync(files);
            var captureIndex = Array.IndexOf(_args, "--capture");
            if (captureIndex >= 0 && captureIndex + 1 < _args.Length)
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(_args[captureIndex + 1]);
                if (_args.Contains("--exit-after-capture")) Close();
            }
        }
        catch (Exception ex)
        {
            _ready = true;
            EndWork();
            ShowError(ex.Message);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _operation?.Cancel();
        if (!_args.Contains("--no-restore")) SaveState();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Workspace_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();
    private void OpenThunderstore_Click(object sender, RoutedEventArgs e)
    {
        var community = GameBox.Text.Trim();
        OpenUrl(community.Length > 0 && !community.Equals("generic", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(community, "^[a-z0-9]+(?:-[a-z0-9]+)*$")
            ? $"https://thunderstore.io/c/{community}/" : "https://thunderstore.io/");
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "1. 从 Thunderstore 下载所需游戏的 Mod ZIP，保留官网文件名。\n\n" +
            "2. 将多个 ZIP 拖进窗口、拖到程序图标上，或点击「添加 Mod」。你也可以重新导入本程序生成的离线整合包。\n\n" +
            "3. 检查前置。点击「补齐前置」下载声明的对应版本；压缩包改过名字时，在详情中补充原作者 / 团队。\n\n" +
            "4. 可选填写游戏名称或社区标识，并选择导出：\n离线整合包：默认保留原 ZIP 与锁定清单，适用于各游戏。可拖回本程序离线重新编辑；具体安装方式以该游戏或 Mod 的说明为准。\nBepInEx 安装目录：仅在确认游戏使用 BepInEx 时选择，额外整理 INSTALL_FILES。\nThunderstore 发布包：输出 manifest、图标和说明，管理器会联网下载依赖。\n\n" +
            "官网 ZIP 清单通常不包含游戏信息，请确认导入的 Mod 属于同一个游戏。本程序检查依赖完整性，不判断运行兼容性。\n\n快捷键：Ctrl+O 导入 · Ctrl+E 导出 · Esc 取消当前任务。",
            "使用指南", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "选择 Thunderstore Mod 或离线整合包", Filter = "Mod 压缩包 (*.zip)|*.zip", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ImportPathsAsync(dialog.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await ImportPathsAsync(paths);
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (_busy) return;
        BeginWork("正在读取 Mod 压缩包…");
        try
        {
            var cachedPaths = new List<string>();
            var errors = new List<string>();
            foreach (var path in paths)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                if (!File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{Path.GetFileName(path)}：请选择 ZIP 文件。");
                    continue;
                }
                try { cachedPaths.Add(await CacheFileAsync(path, _operation.Token)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
            }
            var result = await Task.Run(() => _engine.ImportAsync(cachedPaths, _operation!.Token));
            errors.AddRange(result.Errors);
            var emptyWorkspace = _packages.Count == 0;
            var added = 0;
            var replaced = 0;
            foreach (var package in result.Packages)
            {
                var existing = _packages.FirstOrDefault(p => SamePackage(p, package));
                if (existing is not null)
                {
                    if (existing.Version == package.Version) continue;
                    _packages.Remove(existing);
                    replaced++;
                }
                _packages.Add(package);
                added++;
            }
            if (emptyWorkspace && added > 0)
            {
                if (result.Game is not null) GameBox.Text = DisplayGame(result.Game);
                if (result.InstallMode is not null) InstallModeBox.SelectedIndex = result.InstallMode == "bepinex" ? 1 : 0;
            }
            InferAuthors();
            StatusText.Text = $"已导入 {added} 个 Mod" + (replaced > 0 ? $"，替换 {replaced} 个旧版本" : "") + (errors.Count > 0 ? $" · {errors.Count} 个文件未导入" : "");
            LatestActivityText.Text = StatusText.Text;
            RefreshWorkspace();
            SaveState();
            if (errors.Count > 0) ShowError(string.Join("\n\n", errors.Take(8)) + (errors.Count > 8 ? $"\n\n另有 {errors.Count - 8} 个错误。" : ""));
        }
        catch (OperationCanceledException) { StatusText.Text = "导入已取消，已导入的 Mod 已保留。"; }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { EndWork(); }
    }

    private async Task<string> CacheFileAsync(string source, CancellationToken ct)
    {
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > 1024L * 1024 * 1024) throw new InvalidDataException("单个导入文件不能超过 1 GB。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var directory = Path.Combine(_dataDirectory, "cache", hash);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(source));
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return target;
        if (!File.Exists(target))
        {
            var temp = target + ".partial";
            try
            {
                stream.Position = 0;
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await stream.CopyToAsync(output, ct);
                File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return target;
    }

    private static bool SamePackage(ModPackage left, ModPackage right) =>
        left.SourcePath.Equals(right.SourcePath, StringComparison.OrdinalIgnoreCase) ||
        (left.HasNamespace && right.HasNamespace && left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase));

    private void InferAuthors()
    {
        foreach (var package in _packages.Where(p => !p.HasNamespace))
        {
            var owners = _packages.SelectMany(p => p.Dependencies)
                .Select(d => d.Split('-'))
                .Where(parts => parts.Length == 3 && parts[1] == package.Name && parts[2] == package.Version)
                .Select(parts => parts[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (owners.Length == 1) package.Namespace = owners[0];
        }
    }

    private void RefreshWorkspace()
    {
        if (!_ready) return;
        var selected = (ModListBox.SelectedItem as ModRow)?.Key;
        _issues = _engine.CheckDependencies(_packages);
        var enabled = _packages.Where(p => p.Enabled).ToArray();
        _enabledSnapshot = _packages.GroupBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Enabled, StringComparer.OrdinalIgnoreCase);
        var dependencyNames = enabled.SelectMany(p => p.Dependencies).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = SearchBox.Text.Trim();
        _rows.Clear();
        foreach (var package in _packages)
        {
            var isDependency = package.IsLoader || dependencyNames.Contains(package.DependencyString);
            if (_dependenciesOnly && !isDependency) continue;
            if (query.Length > 0 && !$"{package.Name} {package.Namespace} {package.Description}".Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var issue = _issues.FirstOrDefault(i => i.Kind == "identity"
                ? !package.HasNamespace && i.RequiredBy.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
                : i.RequiredBy.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            var status = !package.Enabled ? "已停用" : !package.HasNamespace ? "待补作者" : issue is not null ? "需检查" : "已就绪";
            var row = new ModRow(package, isDependency, status, QueueToggleRefresh);
            _rows.Add(row);
            if (row.Key == selected) ModListBox.SelectedItem = row;
        }
        ModCountText.Text = $"{_packages.Count} 个 Mod";
        MissingCountText.Text = _issues.Count == 0 ? "前置完整" : $"{_issues.Count} 项待处理";
        ExportCountText.Text = enabled.Length.ToString();
        ExportSizeText.Text = FormatSize(enabled.Sum(p => p.FileSize));
        ReadyText.Text = enabled.Length == 0 ? "导入 Mod 后即可打包" : _issues.Count == 0 ? "依赖检查通过，可以导出" : "请先处理前置与作者信息";
        DependencyBanner.Visibility = _issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IssueText.Text = string.Join("\n", _issues.Take(3).Select(i => i.Message)) + (_issues.Count > 3 ? $"\n还有 {_issues.Count - 3} 项，导出前需处理。" : "");
        ResolveButton.IsEnabled = !_busy && _issues.Any(i => i.CanDownload);
        ExportButton.IsEnabled = !_busy && enabled.Length > 0 && _issues.Count == 0;
        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitleText.Text = _packages.Count == 0 ? "整合包里还空空的" : "没有找到匹配的 Mod";
        EmptyHintText.Text = _packages.Count == 0 ? "从 Thunderstore 下载 Mod，直接拖到这里。" : "试试其他关键词，或切换到「全部」。";
        ModListBox.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelection();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshWorkspace();
    private void Game_Changed(object sender, TextChangedEventArgs e) { if (_ready && !_busy) SaveState(); }
    private void InstallMode_Changed(object sender, SelectionChangedEventArgs e) { if (_ready && !_busy) SaveState(); }
    private void AllFilter_Click(object sender, RoutedEventArgs e) { _dependenciesOnly = false; RefreshWorkspace(); }
    private void DependenciesFilter_Click(object sender, RoutedEventArgs e) { _dependenciesOnly = true; RefreshWorkspace(); }
    private void ModSelection_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSelection(); }

    private void UpdateSelection()
    {
        var package = (ModListBox.SelectedItem as ModRow)?.Package;
        SelectionNameText.Text = package is null ? "Mod 详情" : package.Name;
        SelectionDescriptionText.Text = package is null ? "选择一个 Mod，查看说明和前置依赖。" : package.Description;
        SelectionDependenciesText.Text = package is null ? "支持官网 ZIP 批量导入" : package.Dependencies.Length == 0 ? "无声明前置" : "前置：" + string.Join(" · ", package.Dependencies);
        FixAuthorButton.Visibility = package is { HasNamespace: false } ? Visibility.Visible : Visibility.Collapsed;
        RemoveSelectedButton.Visibility = package is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ModToggle_Changed(object sender, RoutedEventArgs e)
        => QueueToggleRefresh();

    private void QueueToggleRefresh()
    {
        if (!_ready || _busy) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_packages.Any(p => !_enabledSnapshot.TryGetValue(p.SourcePath, out var previous) || previous != p.Enabled))
            { RefreshWorkspace(); SaveState(); }
        });
    }

    private void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string key }) return;
        _packages.RemoveAll(p => p.SourcePath == key);
        RefreshWorkspace();
        SaveState();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ModListBox.SelectedItem is not ModRow row) return;
        _packages.Remove(row.Package);
        RefreshWorkspace();
        SaveState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _packages.Clear();
        SearchBox.Clear();
        RefreshWorkspace();
        SaveState();
        StatusText.Text = "列表已清空，可重新导入 Mod。";
    }

    private void FixAuthor_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ModListBox.SelectedItem is not ModRow row) return;
        var answer = PromptAuthor(row.Package.Name);
        if (answer is null) return;
        if (!Regex.IsMatch(answer, "^[A-Za-z0-9_][A-Za-z0-9_-]*$")) { ShowError("作者 / 团队只能包含英文字母、数字、下划线和连字符，请使用官网上的团队名。"); return; }
        if (_packages.Any(p => p != row.Package && p.Namespace.Equals(answer, StringComparison.OrdinalIgnoreCase) && p.Name.Equals(row.Package.Name, StringComparison.OrdinalIgnoreCase)))
        { ShowError("列表中已有该作者的同名 Mod，请先移除其中一个。"); return; }
        row.Package.Namespace = answer;
        RefreshWorkspace();
        SaveState();
    }

    private string? PromptAuthor(string name)
    {
        var input = new TextBox { Margin = new Thickness(0, 16, 0, 18), Padding = new Thickness(10), FontSize = 15 };
        var ok = new Button { Content = "保存作者", Padding = new Thickness(20, 9, 20, 9), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = $"为 {name} 补充作者 / 团队", FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "打开 Mod 官网页面，输入「by」后面的团队名。", Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(ok);
        var dialog = new Window { Owner = this, Title = "补充作者", Width = 450, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = Brushes.White, FontFamily = FontFamily };
        ok.Click += (_, _) => { if (input.Text.Trim().Length > 0) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private async void ResolveDependencies_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        BeginWork("正在从 Thunderstore 下载前置…（Esc 取消）");
        try
        {
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var downloaded = 0;
            while (true)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                var next = _engine.CheckDependencies(_packages).FirstOrDefault(i => i.CanDownload && !attempted.Contains(i.Dependency));
                if (next is null) break;
                if (attempted.Count >= 128) throw new InvalidDataException("前置依赖超过 128 个，请检查整合包内容。");
                attempted.Add(next.Dependency);
                var package = await _engine.DownloadDependencyAsync(next.Dependency, Path.Combine(_dataDirectory, "downloads"), Progress(), _operation.Token);
                var old = _packages.FirstOrDefault(p => SamePackage(p, package));
                if (old is not null) _packages.Remove(old);
                if (!_packages.Any(existing => SamePackage(existing, package))) _packages.Add(package);
                downloaded++;
                InferAuthors();
                RefreshWorkspace();
                SaveState();
            }
            StatusText.Text = $"已补齐 {downloaded} 个前置 Mod。";
            LatestActivityText.Text = StatusText.Text;
        }
        catch (OperationCanceledException) { StatusText.Text = "下载已取消，已完成的前置已保留。"; }
        catch (Exception ex) { ShowError("前置下载未完成：" + ex.Message + "\n\n也可以从官网下载对应版本的 ZIP 后手动导入。"); }
        finally { EndWork(); RefreshWorkspace(); SaveState(); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_packages.Any(p => p.Enabled)) return;
        RefreshWorkspace();
        if (_issues.Count > 0) { ShowError("请先处理列表中的前置或作者问题。"); return; }
        var name = PackNameBox.Text.Trim();
        var version = VersionBox.Text.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{1,128}$")) { ShowError("整合包名称请使用 1–128 个英文字母、数字或下划线。"); PackNameBox.Focus(); return; }
        if (!Regex.IsMatch(version, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")) { ShowError("版本号格式应为 1.0.0。"); VersionBox.Focus(); return; }
        if (DescriptionBox.Text.Trim().Length > 250) { ShowError("简介最多 250 个字符。"); return; }
        if (GameBox.Text.Trim().Length > 128) { ShowError("游戏名称或社区标识最多 128 个字符。"); GameBox.Focus(); return; }
        var offline = ExportFormatBox.SelectedIndex != 1;
        var dialog = new SaveFileDialog { Title = "保存整合包", Filter = "ZIP 整合包 (*.zip)|*.zip", FileName = $"{name}-{version}{(offline ? "-offline" : "-thunderstore")}.zip", AddExtension = true, DefaultExt = ".zip" };
        if (dialog.ShowDialog(this) != true) return;
        BeginWork("正在打包…（Esc 取消）");
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pack-icon.png"));
            byte[] icon;
            using (var stream = new MemoryStream()) { resource!.Stream.CopyTo(stream); resource.Stream.Dispose(); icon = stream.ToArray(); }
            var options = CreatePackOptions(icon);
            var progress = Progress();
            var output = offline
                ? await Task.Run(() => _engine.ExportOfflineAsync(_packages.ToArray(), options, dialog.FileName, progress, _operation!.Token))
                : await Task.Run(() => _engine.ExportThunderstoreAsync(_packages.ToArray(), options, dialog.FileName, progress, _operation!.Token));
            StatusText.Text = "导出完成 · " + output;
            LatestActivityText.Text = "刚刚导出 " + Path.GetFileName(output);
            SaveState();
            var instructions = offline
                ? options.InstallMode == "bepinex"
                    ? "已保留原始 ZIP，并整理常见 BepInEx 安装目录。请先阅读 README_安装说明.txt，确认该游戏的安装要求。"
                    : "已保留原始 Mod ZIP、锁定清单和 SHA256。可拖回本程序重新编辑，安装方式请参考相应游戏或 Mod 的说明。"
                : "包含发布清单、说明和图标，可在对应游戏的 Thunderstore 社区上传。管理器将联网下载依赖。";
            MessageBox.Show(this, $"整合包已保存：\n{output}\n\n{instructions}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { StatusText.Text = "导出已取消。"; }
        catch (Exception ex) { ShowError("导出未完成：" + ex.Message); }
        finally { EndWork(); }
    }

    private IProgress<string> Progress() => new Progress<string>(message => StatusText.Text = message);

    private PackOptions CreatePackOptions(byte[]? icon = null) => new()
    {
        Name = PackNameBox.Text.Trim(), Version = VersionBox.Text.Trim(),
        Description = DescriptionBox.Text.Trim(),
        Game = string.IsNullOrWhiteSpace(GameBox.Text) ? "generic" : GameBox.Text.Trim(),
        InstallMode = InstallModeBox.SelectedIndex == 1 ? "bepinex" : "archive",
        WebsiteUrl = "", IconBytes = icon
    };

    private static string DisplayGame(string game) => game.Equals("generic", StringComparison.OrdinalIgnoreCase) ? "" : game;

    private void BeginWork(string message)
    {
        _busy = true;
        _operation = new CancellationTokenSource();
        StatusText.Text = message;
        WorkProgress.Visibility = Visibility.Visible;
        ExportButton.IsEnabled = false;
        ResolveButton.IsEnabled = false;
        ModListBox.IsEnabled = false;
        GameBox.IsEnabled = false;
        InstallModeBox.IsEnabled = false;
    }

    private void EndWork()
    {
        _busy = false;
        _operation?.Dispose();
        _operation = null;
        WorkProgress.Visibility = Visibility.Collapsed;
        ModListBox.IsEnabled = true;
        GameBox.IsEnabled = true;
        InstallModeBox.IsEnabled = true;
        RefreshWorkspace();
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _busy) { _operation?.Cancel(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.O) { e.Handled = true; Import_Click(sender, e); }
        else if (e.Key == Key.E) { e.Handled = true; Export_Click(sender, e); }
        await Task.CompletedTask;
    }

    private void SaveState()
    {
        if (!_ready || _args.Contains("--no-restore")) return;
        try
        {
            var state = new WorkspaceState
            {
                SchemaVersion = 2,
                Name = PackNameBox.Text,
                Version = VersionBox.Text,
                Description = DescriptionBox.Text,
                Format = ExportFormatBox.SelectedIndex,
                Game = CreatePackOptions().Game,
                InstallMode = CreatePackOptions().InstallMode,
                Packages = _packages.Select(p => new SavedPackage { Path = p.SourcePath, Namespace = p.Namespace, Enabled = p.Enabled }).ToList()
            };
            var file = Path.Combine(_dataDirectory, "workspace.json");
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
        catch (Exception ex) { StatusText.Text = "工作区暂未保存：" + ex.Message; }
    }

    private async Task RestoreStateAsync()
    {
        var file = Path.Combine(_dataDirectory, "workspace.json");
        if (!File.Exists(file)) return;
        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceState>(await File.ReadAllTextAsync(file));
            if (state is null) return;
            // Migrate the previous example-only defaults while preserving user-created names and packages.
            PackNameBox.Text = state.SchemaVersion < 2 && state.Name == "PEAK_Friends" ? "My_Modpack" : state.Name;
            VersionBox.Text = state.Version;
            DescriptionBox.Text = state.SchemaVersion < 2 && state.Description == "和朋友一起登顶的 PEAK 整合包" ? "我的 Thunderstore Mod 整合包" : state.Description;
            ExportFormatBox.SelectedIndex = state.Format is 0 or 1 ? state.Format : 0;
            GameBox.Text = DisplayGame(state.Game);
            InstallModeBox.SelectedIndex = state.InstallMode == "bepinex" ? 1 : 0;
            var saved = state.Packages.Where(p => File.Exists(p.Path)).ToArray();
            var result = await Task.Run(() => _engine.ImportAsync(saved.Select(p => p.Path)));
            foreach (var package in result.Packages)
            {
                var settings = saved.FirstOrDefault(p => p.Path == package.SourcePath);
                if (settings is not null) { package.Namespace = settings.Namespace; package.Enabled = settings.Enabled; }
                if (!_packages.Any(existing => SamePackage(existing, package))) _packages.Add(package);
            }
            StatusText.Text = $"已恢复上次工作区 · {_packages.Count} 个 Mod";
        }
        catch (Exception ex) { StatusText.Text = "上次工作区未能恢复，可重新导入 ZIP：" + ex.Message; }
    }

    private void Capture(string output)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    private void ShowError(string message) { StatusText.Text = message.Split('\n')[0]; MessageBox.Show(this, message, "请检查", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public static string FormatSize(long bytes) => bytes == 0 ? "0 KB" : bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.1} MB" : $"{Math.Max(1, bytes / 1024d):0.#} KB";
}

public sealed class ModRow
{
    private readonly Action? _enabledChanged;
    public ModPackage Package { get; }
    public string Key => Package.SourcePath;
    public string Name => Package.Name.Replace('_', ' ');
    public string Author => Package.HasNamespace ? Package.Namespace : "作者待确认";
    public string VersionLabel => "v" + Package.Version;
    public string SizeLabel => MainWindow.FormatSize(Package.FileSize);
    public string RoleLabel { get; }
    public string StatusLabel { get; }
    public string StatusColor { get; }
    public string Initial => Package.Name.Length == 0 ? "M" : Package.Name[..1].ToUpperInvariant();
    public BitmapSource? Icon { get; }
    public bool Enabled
    {
        get => Package.Enabled;
        set
        {
            if (Package.Enabled == value) return;
            Package.Enabled = value;
            _enabledChanged?.Invoke();
        }
    }
    public ModRow(ModPackage package, bool dependency, string status, Action? enabledChanged = null)
    {
        Package = package;
        _enabledChanged = enabledChanged;
        RoleLabel = package.IsLoader ? "加载器" : dependency ? "前置" : "MOD";
        StatusLabel = status;
        StatusColor = status == "已就绪" ? "#38846D" : status == "已停用" ? "#8A8F9D" : "#B77B24";
        if (package.IconBytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(package.IconBytes);
                var icon = new BitmapImage();
                icon.BeginInit(); icon.CacheOption = BitmapCacheOption.OnLoad; icon.DecodePixelWidth = 64; icon.StreamSource = stream; icon.EndInit(); icon.Freeze();
                Icon = icon;
            }
            catch (Exception) { /* An optional malformed icon never prevents importing a valid mod. */ }
        }
    }
}

public sealed class WorkspaceState
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; } = "My_Modpack";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "我的 Thunderstore Mod 整合包";
    public string Game { get; set; } = "generic";
    public string InstallMode { get; set; } = "archive";
    public int Format { get; set; }
    public List<SavedPackage> Packages { get; set; } = [];
}

public sealed class SavedPackage
{
    public string Path { get; set; } = "";
    public string Namespace { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
