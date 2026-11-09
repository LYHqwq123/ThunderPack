using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ThunderPack.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var workspace = args.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(args[0]);
        var testDirectory = Path.Combine(workspace, "artifacts", "ui-check-state", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(testDirectory);

        using var watchdog = new Timer(_ =>
        {
            Console.Error.WriteLine("FAIL UI smoke exceeded its 30-second watchdog; terminating the isolated process.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

        var result = 1;
        MainWindow? window = null;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("FAIL Unexpected dispatcher exception: " + e.Exception);
            e.Handled = true;
            application.Shutdown(1);
        };
        application.Startup += async (_, _) =>
        {
            try
            {
                var files = CreateFixtures(Path.Combine(testDirectory, "fixtures"));
                window = new MainWindow(["--no-restore", "--data-dir", Path.Combine(testDirectory, "workspace"), .. files]);
                application.MainWindow = window;
                var suite = new UiSuite(window, testDirectory);
                await suite.CheckStartupGuardAsync(files);
                window.Show();
                await suite.RunAsync();
                Console.WriteLine("\n13/13 WPF UI checks passed. Artifacts: " + testDirectory);
                result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
            }
            finally
            {
                window?.Close();
                application.Shutdown(result);
            }
        };
        application.Run();
        return result;
    }

    private static string[] CreateFixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        return
        [
            CreatePackage(directory, "BepInEx", "BepInExPack_Common", "5.4.75301", "UI smoke loader", [], new()
            {
                ["BepInExPack_Common/BepInEx/core/BepInEx.dll"] = "UI fixture only; never executed",
                ["BepInExPack_Common/winhttp.dll"] = "UI fixture only; never executed"
            }),
            CreatePackage(directory, "FixtureTeam", "Climb_Library", "1.0.0", "UI smoke library", ["BepInEx-BepInExPack_Common-5.4.75301"], new()
            {
                ["plugins/ClimbLibrary.dll"] = "UI fixture only; never executed"
            }),
            CreatePackage(directory, "FixtureTeam", "Helpful_Mod", "1.0.0", "UI smoke plugin", ["FixtureTeam-Climb_Library-1.0.0", "BepInEx-BepInExPack_Common-5.4.75301"], new()
            {
                ["plugins/HelpfulMod.dll"] = "UI fixture only; never executed"
            })
        ];
    }

    internal static string CreatePackage(string directory, string owner, string name, string version, string description, string[] dependencies, Dictionary<string, string> payload)
    {
        var path = Path.Combine(directory, $"{owner}-{name}-{version}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write("manifest.json", JsonSerializer.Serialize(new { name, version_number = version, website_url = "", description, dependencies }));
        Write("README.md", "# UI fixture\n\nThis deterministic test package is never executed.");
        foreach (var entry in payload) Write(entry.Key, entry.Value);
        return path;

        void Write(string entry, string value)
        {
            using var stream = archive.CreateEntry(entry).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(value);
        }
    }
}

internal sealed class UiSuite(MainWindow window, string testDirectory)
{
    private ListBox Mods => Find<ListBox>("ModListBox");
    private Button Export => Find<Button>("ExportButton");
    private Border Banner => Find<Border>("DependencyBanner");

    public async Task CheckStartupGuardAsync(string[] files)
    {
        await ImportPathsAsync(files);
        var packageField = typeof(MainWindow).GetField("_packages", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing package backing list for the startup guard assertion.");
        var packages = (IReadOnlyCollection<ThunderPack.Core.ModPackage>)packageField.GetValue(window)!;
        Check(packages.Count == 0 && Mods.Items.Count == 0, "Import mutated the package list before startup restore completed.");
        Pass("Startup guard rejects imports before the window finishes restoring");
    }

    public async Task RunAsync()
    {
        await WaitAsync(() => Mods.Items.Count == 3 && Find<ProgressBar>("WorkProgress").Visibility == Visibility.Collapsed, "Initial local ZIP import did not finish.");
        await DrainAsync();
        Check(Mods.Items.Cast<ModRow>().Any(r => r.Package.IsLoader), "Imported loader row is missing.");
        Check(Mods.Items.Cast<ModRow>().Any(r => r.Package.Name == "Climb_Library"), "Imported prerequisite library row is missing.");
        Check(Export.IsEnabled && Banner.Visibility == Visibility.Collapsed, "Complete local dependencies must allow export.");
        Check(Find<Border>("EmptyPanel").Visibility == Visibility.Collapsed, "Empty state must disappear after import.");
        Pass("Startup ZIP arguments populate real controls and enable export");

        var nameBox = Find<TextBox>("PackNameBox");
        var versionBox = Find<TextBox>("VersionBox");
        Check(nameBox.Text == "My_Modpack", "Default pack name was lost.");
        Check(versionBox.Text == "1.0.0", "Default version was lost.");
        foreach (var input in new[] { nameBox, versionBox })
        {
            input.ApplyTemplate();
            var host = input.Template.FindName("PART_ContentHost", input) as ScrollViewer;
            Check(host is { ActualHeight: > 20 }, "Single-line input content host has no usable height.");
            Check(host!.Margin.Top == 0 && host.Margin.Bottom == 0, "TextBox content host duplicates Padding as Margin.");
        }
        Pass("Default name/version values have visible input content hosts");

        var game = Find<TextBox>("GameBox");
        var installMode = Find<ComboBox>("InstallModeBox");
        Check(string.IsNullOrWhiteSpace(game.Text), "A fresh workspace must not force a particular game.");
        Check(installMode.SelectedIndex == 0, "Universal original-archive mode must be the safe default.");
        Check(installMode.Items.Count == 2, "Both universal and optional BepInEx layouts must be offered.");
        Check(Find<TextBlock>("InstallModeHintText").Text.Contains("原始 ZIP"), "Universal mode does not explain preserved original archives.");
        game.Text = "riskofrain2";
        installMode.SelectedIndex = 1;
        await DrainAsync();
        Check(Find<TextBlock>("InstallModeHintText").Text.Contains("BepInEx"), "Optional BepInEx layout did not update its explanation.");
        var optionsMethod = typeof(MainWindow).GetMethod("CreatePackOptions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing actual export-option mapping.");
        var options = (ThunderPack.Core.PackOptions)optionsMethod.Invoke(window, [null])!;
        Check(options.Game == "riskofrain2" && options.InstallMode == "bepinex", "Game and layout controls do not reach exported metadata.");
        Check(Export.IsEnabled, "Changing the game label or optional layout should not alter complete dependencies.");
        installMode.SelectedIndex = 0;
        game.Clear();
        await DrainAsync();
        options = (ThunderPack.Core.PackOptions)optionsMethod.Invoke(window, [null])!;
        Check(options.Game == "generic" && options.InstallMode == "archive", "Clearing game and resetting layout did not restore universal export metadata.");
        Pass("Any game label and explicit install layout replace a hardcoded game");

        var search = Find<TextBox>("SearchBox");
        search.Text = "Helpful";
        await WaitAsync(() => Mods.Items.Count == 1, "Name search did not filter rows.");
        Check(((ModRow)Mods.Items[0]).Package.Name == "Helpful_Mod", "Name search returned the wrong row.");
        search.Text = "FixtureTeam";
        await WaitAsync(() => Mods.Items.Count == 2, "Author search did not match both local packages.");
        search.Text = "does_not_exist_89731";
        await WaitAsync(() => Mods.Items.Count == 0, "Unmatched search did not clear visible rows.");
        Check(Export.IsEnabled, "Search must not alter the selected export packages.");
        search.Clear();
        await WaitAsync(() => Mods.Items.Count == 3, "Clearing search did not restore the list.");
        Pass("Search filters names/authors without changing export selection");

        ClickRadio("DependenciesFilterButton");
        await WaitAsync(() => Mods.Items.Count == 2, "Prerequisite filter must show loader and library.");
        Check(Mods.Items.Cast<ModRow>().All(r => r.Package.Name != "Helpful_Mod"), "Prerequisite filter included the ordinary mod.");
        ClickRadio("AllFilterButton");
        await WaitAsync(() => Mods.Items.Count == 3, "All filter did not restore ordinary mod.");
        Pass("All/prerequisite filter controls are wired to the actual package list");

        var selected = Mods.Items.Cast<ModRow>().Single(r => r.Package.Name == "Helpful_Mod");
        Mods.SelectedItem = selected;
        await DrainAsync();
        Check(Find<TextBlock>("SelectionNameText").Text == "Helpful_Mod", "Selection did not update name details.");
        Check(Find<TextBlock>("SelectionDescriptionText").Text == "UI smoke plugin", "Selection did not update description.");
        Check(Find<TextBlock>("SelectionDependenciesText").Text.Contains("FixtureTeam-Climb_Library-1.0.0"), "Selection did not expose the declared prerequisite.");
        Check(Find<Button>("RemoveSelectedButton").Visibility == Visibility.Visible, "Selected-row remove action must be visible.");
        Pass("Selecting an imported row updates its description and prerequisites");

        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await DrainAsync();
        var settings = Find<ScrollViewer>("ExportSettingsScroll");
        Check(settings.ExtentHeight > settings.ViewportHeight, "Minimum-height export settings do not expose their necessary scroll range.");
        settings.ScrollToEnd();
        await DrainAsync();
        var sizeBounds = Bounds(Find<TextBlock>("ExportSizeText"));
        var readyBounds = Bounds(Find<TextBlock>("ReadyText"));
        Check(sizeBounds.Bottom + 12 <= readyBounds.Top,
            $"At minimum window size export preview overlaps status: preview bottom {sizeBounds.Bottom:0.0}, status top {readyBounds.Top:0.0}.");
        Check(Bounds(settings).Bottom + 8 <= Bounds(Find<Border>("ExportReadyPanel")).Top, "The settings viewport intrudes into the fixed export actions.");
        Check(sizeBounds.Bottom <= Bounds(settings).Bottom, "Scrolling cannot fully reveal the export preview.");
        Check(Bounds(Export).Bottom < window.ActualHeight - 33, "At minimum size export button overlaps the status bar.");
        settings.ScrollToTop();
        await DrainAsync();
        Check(Bounds(nameBox).Top >= Bounds(settings).Top && Bounds(nameBox).Bottom <= Bounds(settings).Bottom,
            "Scrolling cannot reveal the editable pack name at minimum size.");
        window.Width = 1280;
        window.Height = 860;
        await DrainAsync();
        Pass("Minimum 1120x760 size keeps export preview, status, and actions apart");

        await TogglePrerequisiteAsync(false);
        await WaitAsync(() => !Export.IsEnabled && Banner.Visibility == Visibility.Visible, "Disabling the prerequisite did not block export.");
        Check(Find<TextBlock>("IssueText").Text.Contains("Climb_Library"), "Dependency banner does not identify the missing prerequisite.");
        Check(Find<TextBlock>("ExportCountText").Text == "2", "Disabled prerequisite was still counted in the export.");
        await TogglePrerequisiteAsync(true);
        await WaitAsync(() => Export.IsEnabled && Banner.Visibility == Visibility.Collapsed, "Re-enabling the prerequisite did not restore export.");
        Check(Find<TextBlock>("ExportCountText").Text == "3", "Re-enabled prerequisite was not restored to the export.");
        Pass("Actual checkbox toggles update dependencies, counts, and export eligibility");

        var format = Find<ComboBox>("ExportFormatBox");
        format.SelectedIndex = 1;
        format.IsDropDownOpen = true;
        await DrainAsync();
        var popup = format.Template.FindName("PART_Popup", format) as Popup;
        Check(popup is { IsOpen: true }, "Styled export-format popup did not open.");
        Check(Find<TextBlock>("ExportHintText").Text.Contains("联网"), "Publication format did not change its explanation.");
        format.IsDropDownOpen = false;
        format.SelectedIndex = 0;
        await DrainAsync();
        Check(Find<TextBlock>("ExportHintText").Text.Contains("本体"), "Offline format did not restore its explanation.");
        Pass("Styled export-format dropdown opens and updates the format explanation");

        var mod = Mods.Items.Cast<ModRow>().Single(r => r.Package.Name == "Helpful_Mod");
        var container = await ContainerAsync(mod);
        var remove = Visuals<Button>(container).Single(b => b.Tag is string key && key == mod.Key);
        remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, remove));
        await WaitAsync(() => Mods.Items.Count == 2, "Row remove button did not remove the ordinary mod.");
        Check(Export.IsEnabled, "Removing the ordinary mod must leave loader/library dependencies valid.");
        Check(Mods.Items.Cast<ModRow>().All(r => r.Package.Name != "Helpful_Mod"), "Removed mod is still visible.");
        Pass("Per-row remove action updates the package list and preserves valid export state");

        var clear = Visuals<Button>(window).Single(b => b.Content is string text && text == "清空");
        clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clear));
        await WaitAsync(() => Mods.Items.Count == 0 && !Export.IsEnabled, "Clear action did not reset the workspace.");
        Check(Find<Border>("EmptyPanel").Visibility == Visibility.Visible, "Clear action did not restore the empty state.");
        Check(Banner.Visibility == Visibility.Collapsed, "Empty workspace has a stale dependency warning.");
        Check(Find<TextBlock>("ExportCountText").Text == "0", "Clear action did not reset the export count.");
        Pass("Clear resets the empty state, warning banner, and export controls");

        var regressionDirectory = Path.Combine(testDirectory, "same-name-regression");
        Directory.CreateDirectory(regressionDirectory);
        var alice = Program.CreatePackage(regressionDirectory, "Alice", "SharedMod", "1.0.0", "Alice's named package", [], new()
        {
            ["plugins/SharedMod.dll"] = "Alice UI fixture payload; never executed"
        });
        var bob = Program.CreatePackage(regressionDirectory, "Bob", "SharedMod", "1.0.0", "Bob's renamed package", [], new()
        {
            ["plugins/SharedMod.dll"] = "Bob UI fixture payload; never executed"
        });
        var renamedBob = Path.Combine(regressionDirectory, "download.zip");
        File.Move(bob, renamedBob);
        await ImportPathsAsync([alice, renamedBob]);
        await WaitAsync(() => Mods.Items.Count == 2, "A renamed same-name mod was silently skipped or replaced the known author's mod.");
        var sharedRows = Mods.Items.Cast<ModRow>().ToArray();
        Check(sharedRows.All(r => r.Package.Name == "SharedMod" && r.Package.Version == "1.0.0"), "Same-name fixtures were not imported intact.");
        Check(sharedRows.Count(r => r.Package.Namespace == "Alice") == 1, "The known author's package was overwritten by an unknown author.");
        var unknown = sharedRows.Single(r => !r.Package.HasNamespace);
        Check(unknown.Package.Description == "Bob's renamed package", "The unknown author's distinct package was not preserved.");
        Check(unknown.Author == "作者待确认", "The UI invented an author for the renamed package.");
        Check(Banner.Visibility == Visibility.Visible && !Export.IsEnabled, "An unknown author must remain visible and block ambiguous export.");
        Mods.SelectedItem = unknown;
        await DrainAsync();
        Check(Find<Button>("FixAuthorButton").Visibility == Visibility.Visible, "The renamed package has no author-fix action.");
        await ImportPathsAsync([renamedBob]);
        await DrainAsync();
        Check(Mods.Items.Count == 2 && Mods.Items.Cast<ModRow>().Count(r => !r.Package.HasNamespace) == 1,
            "Reimporting the identical renamed source created a duplicate package.");
        Pass("Known/unknown same-name packages stay distinct while identical source imports deduplicate");
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var method = typeof(MainWindow).GetMethod("ImportPathsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing actual application import method.");
        var task = method.Invoke(window, [paths]) as Task
            ?? throw new InvalidOperationException("Application import method did not return a Task.");
        await task;
    }

    private async Task TogglePrerequisiteAsync(bool expected)
    {
        var row = Mods.Items.Cast<ModRow>().Single(r => r.Package.Name == "Climb_Library");
        var container = await ContainerAsync(row);
        var checkBox = Visuals<CheckBox>(container).Single();
        Check(checkBox.IsChecked != expected, "Prerequisite checkbox was already in the requested state.");
        var pattern = new CheckBoxAutomationPeer(checkBox).GetPattern(PatternInterface.Toggle) as IToggleProvider;
        Check(pattern is not null, "Prerequisite checkbox does not expose its toggle action.");
        pattern!.Toggle();
        await WaitAsync(() => row.Package.Enabled == expected, "Checkbox two-way binding did not update the package.");
        await DrainAsync();
    }

    private async Task<ListBoxItem> ContainerAsync(ModRow row)
    {
        Mods.ScrollIntoView(row);
        window.UpdateLayout();
        await DrainAsync();
        return Mods.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem
            ?? throw new InvalidOperationException("The imported row has no generated WPF item container.");
    }

    private void ClickRadio(string name)
    {
        var radio = Find<RadioButton>(name);
        radio.IsChecked = true;
        radio.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, radio));
    }

    private T Find<T>(string name) where T : FrameworkElement => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing UI control: {name} ({typeof(T).Name}).");

    private Rect Bounds(FrameworkElement control) => control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));

    private async Task DrainAsync()
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task WaitAsync(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(7)) throw new InvalidOperationException(failure);
            await Task.Delay(20);
        }
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private static void Pass(string name) => Console.WriteLine("PASS " + name);
}
