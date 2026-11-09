using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThunderPack.Core;

var workspace = args.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(args[0]);
var suite = new SmokeSuite(workspace);
return await suite.RunAsync();

internal sealed class SmokeSuite
{
    private readonly string _root;
    private readonly PackEngine _engine = new();
    private readonly PackOptions _options = new() { Name = "Generic_Test_Pack", Version = "1.2.3", Description = "本地自动化测试整合包", InstallMode = "bepinex" };
    private readonly List<(string Name, Func<Task> Run)> _tests = [];
    private string _loaderPath = "";
    private string _modPath = "";
    private List<ModPackage> _validPackages = [];

    public SmokeSuite(string workspace)
    {
        _root = Path.Combine(workspace, "artifacts", "test-data", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _engine.CacheDirectory = Path.Combine(_root, "cache");
    }

    public async Task<int> RunAsync()
    {
        _loaderPath = await CreatePackageAsync("BepInEx", "BepInExPack_Common", "5.4.75301", [], new()
        {
            ["BepInExPack_Common/BepInEx/core/BepInEx.dll"] = "loader-core",
            ["BepInExPack_Common/BepInEx/config/BepInEx.cfg"] = "loader-config",
            ["BepInExPack_Common/winhttp.dll"] = "doorstop",
            ["BepInExPack_Common/doorstop_config.ini"] = "[UnityDoorstop]"
        });
        _modPath = await CreatePackageAsync("Climbers", "Helpful_Mod", "1.0.0", ["BepInEx-BepInExPack_Common-5.4.75301"], new()
        {
            ["Helpful.dll"] = "plugin",
            ["plugins/assets/picture.dat"] = "asset-content",
            ["config/climbers.cfg"] = "settings",
            ["patchers/Patcher.dll"] = "patcher",
            ["core/CoreHelper.dll"] = "helper",
            ["Helpful.mm.dll"] = "monomod",
            ["wrapper/Nested.dll"] = "nested-plugin"
        });
        _validPackages = (await ImportCleanAsync(_loaderPath, _modPath)).Packages;
        Add("读取官网下载包与前置声明", RootImportAsync);
        Add("识别一层包装目录及旧式含连字符作者", WrapperAndLegacyOwnerAsync);
        Add("从依赖关系恢复重命名包作者", NamespaceInferenceAsync);
        Add("未知作者阻止导出", UnknownOwnerAsync);
        Add("损坏 manifest 与缺少 manifest 被拒绝", MalformedManifestsAsync);
        Add("缺失、版本偏差、停用前置被识别", DependencyStatesAsync);
        Add("多个启用副本与冲突版本被识别", DuplicatesAsync);
        Add("不安全 Windows 路径和符号链接被拒绝", UnsafeArchivesAsync);
        Add("伪造超大解压尺寸被拒绝", OversizedArchiveAsync);
        Add("前置下载验证身份并复用有效缓存", DependencyDownloadAsync);
        Add("错误下载响应保留现有缓存", DownloadMismatchAsync);
        Add("取消下载不会留下压缩包", DownloadCancellationAsync);
        Add("离线导出保留原包及常见 BepInEx 文件布局", OfflineExportAsync);
        Add("默认通用包不强加任何游戏安装目录", GenericArchiveAsync);
        Add("通用包原样保留其他加载器与专用文件", OtherLoaderArchiveAsync);
        Add("游戏标签与安装方式随整合包往返", BundleMetadataAsync);
        Add("未知安装方式在写入前被拒绝", InvalidInstallModeAsync);
        Add("BepInEx 模式拒绝混用其他加载器", WrongLoaderLayoutAsync);
        Add("整合包重新导入及 SHA256 篡改检测", BundleRoundTripAsync);
        Add("发布 ZIP 根目录清单、依赖及 256px 图标", PublicationExportAsync);
        Add("配置文件冲突拒绝导出且保留原目标", CollisionAsync);
        Add("禁止覆盖输入 ZIP", SourceOverwriteAsync);
        Add("取消导出不残留半成品且保留原目标", CancellationAsync);
        Add("源 manifest 在导入后改变时拒绝离线导出", SourceMutationAsync);
        var failed = 0;
        foreach (var (name, run) in _tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n     " + ex.Message); }
        }
        Console.WriteLine($"\n{_tests.Count - failed}/{_tests.Count} checks passed. Artifacts: {_root}");
        return failed == 0 ? 0 : 1;
    }

    private void Add(string name, Func<Task> run) => _tests.Add((name, run));

    private Task RootImportAsync()
    {
        Check(_validPackages.Count == 2, "Expected both imported packages.");
        var mod = _validPackages.Single(p => p.Name == "Helpful_Mod");
        Check(mod.Namespace == "Climbers" && mod.Version == "1.0.0", "Filename identity must match the manifest.");
        Check(mod.Dependencies.SequenceEqual(new[] { "BepInEx-BepInExPack_Common-5.4.75301" }), "Dependency array changed.");
        Check(_engine.CheckDependencies(_validPackages).Count == 0, "Exact imported prerequisite should satisfy dependency checks.");
        return Task.CompletedTask;
    }

    private async Task WrapperAndLegacyOwnerAsync()
    {
        var path = await CreatePackageAsync("Old-Team", "Wrapped_Mod", "1.2.0", [], new() { ["plugins/Wrapped.dll"] = "wrapped" }, "single-wrapper/");
        var package = (await ImportCleanAsync(path)).Packages.Single();
        Check(package.Namespace == "Old-Team" && package.ArchiveRoot == "single-wrapper/", "Wrapper/legacy team inference failed.");
        Check(PackEngine.TryParseDependency("Old-Team-Wrapped_Mod-1.2.0", out var reference) && reference.Namespace == "Old-Team", "Dependency parser split the legacy team incorrectly.");
    }

    private async Task NamespaceInferenceAsync()
    {
        var renamed = Path.Combine(_root, "download-renamed.zip");
        File.Copy(_loaderPath, renamed);
        var packages = (await ImportCleanAsync(renamed, _modPath)).Packages;
        Check(packages.Single(p => p.IsLoader).Namespace == "BepInEx", "Unique dependency reference must restore a renamed package identity.");
    }

    private async Task UnknownOwnerAsync()
    {
        var renamed = Path.Combine(_root, "unidentified.zip");
        File.Copy(_modPath, renamed);
        var unknown = (await ImportCleanAsync(renamed)).Packages.Single();
        Check(!unknown.HasNamespace, "Unknown namespace must not be guessed from the website URL.");
        Check(_engine.CheckDependencies([unknown]).Any(i => i.Kind == "identity"), "Missing namespace not reported.");
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync([unknown], _options, Output("unknown")));
    }

    private async Task MalformedManifestsAsync()
    {
        var malformed = await CreateRawArchiveAsync("malformed.zip", new() { ["manifest.json"] = "{invalid-json" });
        var wrongType = await CreateRawArchiveAsync("wrong-type.zip", new() { ["manifest.json"] = "{\"name\":\"Bad\",\"version_number\":\"1.0.0\",\"dependencies\":[42]}" });
        var missing = await CreateRawArchiveAsync("missing-manifest.zip", new() { ["README.md"] = "No manifest" });
        var shortVersion = await CreatePackageAsync("Test", "BadVersion", "1.0", [], new() { ["test.dll"] = "payload" });
        foreach (var path in new[] { malformed, wrongType, missing, shortVersion }) await RejectedAsync(path);
    }

    private async Task DependencyStatesAsync()
    {
        var mod = _validPackages.Single(p => !p.IsLoader);
        Check(_engine.CheckDependencies([mod]).Any(i => i.Kind == "missing" && i.CanDownload), "Missing pinned dependency not downloadable.");
        var older = await CreatePackageAsync("BepInEx", "BepInExPack_Common", "5.4.2403", [], new() { ["winhttp.dll"] = "old" });
        var olderPackage = (await ImportCleanAsync(older)).Packages.Single();
        Check(_engine.CheckDependencies([mod, olderPackage]).Any(i => i.Kind == "version"), "Different local version should produce a version issue.");
        var loader = (await ImportCleanAsync(_loaderPath)).Packages.Single();
        loader.Enabled = false;
        Check(_engine.CheckDependencies([mod, loader]).Any(i => i.Kind == "missing"), "Disabled prerequisite must not count as installed.");
        Check(!PackEngine.TryParseDependency("Author-Mod-1.2", out _), "Two-part versions must be rejected.");
        Check(!PackEngine.TryParseDependency("../Author-Mod-1.2.0", out _), "Unsafe dependency identifiers must be rejected.");
    }

    private async Task DuplicatesAsync()
    {
        var duplicate = (await ImportCleanAsync(_modPath)).Packages.Single();
        Check(_engine.CheckDependencies([.._validPackages, duplicate]).Any(i => i.Kind == "duplicate"), "Enabled duplicate not detected.");
        var other = await CreatePackageAsync("Climbers", "Other", "1.0.0", ["BepInEx-BepInExPack_Common-5.4.2403"], new() { ["Other.dll"] = "payload" });
        var otherPackage = (await ImportCleanAsync(other)).Packages.Single();
        Check(_engine.CheckDependencies([.._validPackages, otherPackage]).Any(i => i.Kind == "conflict"), "Two explicit dependency pins not reported.");
        duplicate.Enabled = false;
        Check(_engine.CheckDependencies([.._validPackages, duplicate]).Count == 0, "Disabled duplicate should not block export.");
    }

    private async Task UnsafeArchivesAsync()
    {
        var names = new[] { "../escape.dll", "C:/escape.dll", "folder/CON.txt", "folder/file.dll:stream", "folder/trailing.", "folder/trailing ", "folder/../escape.dll", "\\\\server\\share\\file.dll" };
        for (var i = 0; i < names.Length; i++)
        {
            var path = await CreatePackageAsync("Unsafe", "Path_" + i, "1.0.0", [], new() { [names[i]] = "unsafe" });
            await RejectedAsync(path);
        }
        var symlink = await CreatePackageAsync("Unsafe", "Link", "1.0.0", [], new() { ["link.dll"] = "../../outside" });
        using (var zip = ZipFile.Open(symlink, ZipArchiveMode.Update)) zip.GetEntry("link.dll")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        await RejectedAsync(symlink);
        var duplicate = await CreateRawArchiveAsync("duplicate-entries.zip", new() { ["manifest.json"] = "{}", ["ReadMe.md"] = "one", ["README.md"] = "two" });
        await RejectedAsync(duplicate);
    }

    private async Task OversizedArchiveAsync()
    {
        var path = await CreatePackageAsync("Unsafe", "Huge", "1.0.0", [], new() { ["huge.dll"] = "x" });
        var bytes = await File.ReadAllBytesAsync(path);
        var central = FindSignature(bytes, 0x02014b50);
        Check(central >= 0, "Synthetic ZIP central directory not found.");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), 2_147_483_649);
        await File.WriteAllBytesAsync(path, bytes);
        await RejectedAsync(path);

        var ratio = await CreatePackageAsync("Unsafe", "Ratio", "1.0.0", [], new() { ["ratio.dll"] = "x" });
        var ratioBytes = await File.ReadAllBytesAsync(ratio);
        var ratioCentral = FindSignature(ratioBytes, 0x02014b50);
        BinaryPrimitives.WriteUInt32LittleEndian(ratioBytes.AsSpan(ratioCentral + 20, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(ratioBytes.AsSpan(ratioCentral + 24, 4), 12 * 1024 * 1024);
        await File.WriteAllBytesAsync(ratio, ratioBytes);
        await RejectedAsync(ratio);
    }

    private async Task DependencyDownloadAsync()
    {
        var handler = new PackageResponseHandler(await File.ReadAllBytesAsync(_loaderPath));
        using var client = new HttpClient(handler);
        var engine = new PackEngine(client) { CacheDirectory = Path.Combine(_root, "download-cache") };
        var cache = Path.Combine(_root, "downloads-good");
        var first = await engine.DownloadDependencyAsync("BepInEx-BepInExPack_Common-5.4.75301", cache);
        Check(first.IsLoader && first.Version == "5.4.75301", "Downloaded package identity changed.");
        Check(handler.LastUrl == "https://thunderstore.io/package/download/BepInEx/BepInExPack_Common/5.4.75301/", "Download did not use the expected official URL.");
        var second = await engine.DownloadDependencyAsync(first.DependencyString, cache);
        Check(handler.RequestCount == 1 && first.SourcePath == second.SourcePath, "Valid downloaded cache was not reused.");
    }

    private async Task DownloadMismatchAsync()
    {
        var handler = new PackageResponseHandler(await File.ReadAllBytesAsync(_modPath));
        using var client = new HttpClient(handler);
        var engine = new PackEngine(client);
        var cache = Path.Combine(_root, "downloads-mismatch");
        Directory.CreateDirectory(cache);
        var existing = Path.Combine(cache, "BepInEx-BepInExPack_Common-5.4.75301.zip");
        await File.WriteAllTextAsync(existing, "preexisting-invalid-cache");
        await ThrowsAsync<InvalidDataException>(() => engine.DownloadDependencyAsync("BepInEx-BepInExPack_Common-5.4.75301", cache));
        Check(await File.ReadAllTextAsync(existing) == "preexisting-invalid-cache", "Rejected download replaced the existing cache.");
        Check(Directory.EnumerateFiles(cache).Count() == 1, "Rejected download left temporary files.");
    }

    private async Task DownloadCancellationAsync()
    {
        var handler = new PackageResponseHandler(await File.ReadAllBytesAsync(_loaderPath));
        using var client = new HttpClient(handler);
        var engine = new PackEngine(client);
        using var cts = new CancellationTokenSource();
        var cache = Path.Combine(_root, "downloads-cancelled");
        await ThrowsAsync<OperationCanceledException>(() => engine.DownloadDependencyAsync("BepInEx-BepInExPack_Common-5.4.75301", cache, new InlineProgress(_ => cts.Cancel()), cts.Token));
        Check(!Directory.EnumerateFiles(cache).Any(), "Cancelled download left final or temporary ZIP files.");
    }

    private async Task OfflineExportAsync()
    {
        var output = Output("offline-layout");
        await _engine.ExportOfflineAsync(_validPackages, _options, output);
        using var zip = ZipFile.OpenRead(output);
        foreach (var expected in new[]
        {
            "pack.json", "README_安装说明.txt",
            "mods/BepInEx-BepInExPack_Common-5.4.75301.zip", "mods/Climbers-Helpful_Mod-1.0.0.zip",
            "INSTALL_FILES/winhttp.dll", "INSTALL_FILES/doorstop_config.ini",
            "INSTALL_FILES/BepInEx/core/BepInEx.dll", "INSTALL_FILES/BepInEx/config/BepInEx.cfg",
            "INSTALL_FILES/BepInEx/config/climbers.cfg", "INSTALL_FILES/BepInEx/plugins/Climbers-Helpful_Mod/Helpful.dll",
            "INSTALL_FILES/BepInEx/plugins/Climbers-Helpful_Mod/assets/picture.dat",
            "INSTALL_FILES/BepInEx/patchers/Climbers-Helpful_Mod/Patcher.dll",
            "INSTALL_FILES/BepInEx/core/Climbers-Helpful_Mod/CoreHelper.dll",
            "INSTALL_FILES/BepInEx/monomod/Climbers-Helpful_Mod/Helpful.mm.dll",
            "INSTALL_FILES/BepInEx/plugins/Climbers-Helpful_Mod/Nested.dll"
        }) Check(zip.GetEntry(expected) is not null, "Missing exported entry: " + expected);
        Check(!zip.Entries.Any(e => e.FullName.StartsWith("INSTALL_FILES/BepInExPack", StringComparison.OrdinalIgnoreCase)), "Loader wrapper leaked into install directory.");
        foreach (var package in _validPackages)
        {
            var copied = zip.GetEntry("mods/" + package.DependencyString + ".zip")!;
            await using var stream = copied.Open();
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            await using var original = File.OpenRead(package.SourcePath);
            Check(digest == Convert.ToHexString(await SHA256.HashDataAsync(original)), "Original archive bytes changed during export.");
        }
    }

    private async Task GenericArchiveAsync()
    {
        var options = new PackOptions();
        Check(options.Game == "generic" && options.InstallMode == "archive", "Default export must be game-neutral original archives.");
        Check(options.Name == "My_Modpack", "Default package name must be game neutral.");
        var output = Output("generic-default");
        await _engine.ExportOfflineAsync(_validPackages, options, output);
        using var zip = ZipFile.OpenRead(output);
        Check(!zip.Entries.Any(e => e.FullName.StartsWith("INSTALL_FILES/", StringComparison.OrdinalIgnoreCase)), "Default universal export must not impose BepInEx installation paths.");
        Check(zip.GetEntry("mods/Climbers-Helpful_Mod-1.0.0.zip") is not null, "Universal export lost original package.");
        using var manifest = JsonDocument.Parse(await ReadTextAsync(zip, "pack.json"));
        Check(manifest.RootElement.GetProperty("game").GetString() == "generic", "Universal game metadata changed.");
        Check(manifest.RootElement.GetProperty("installMode").GetString() == "archive", "Universal installation metadata changed.");
        var readme = await ReadTextAsync(zip, "README_安装说明.txt");
        Check(!readme.Contains("PEAK", StringComparison.OrdinalIgnoreCase), "Default universal instructions still mention a specific game.");
        Check(!readme.Contains("PEAK.exe", StringComparison.OrdinalIgnoreCase), "Universal export still directs users to a specific game executable.");
    }

    private async Task OtherLoaderArchiveAsync()
    {
        var path = await CreatePackageAsync("OtherTeam", "Custom_Loader_Mod", "2.0.0", [], new()
        {
            ["Mods/OtherLoaderMod.dll"] = "other-loader-plugin",
            ["LoaderBootstrap.exe"] = "unexecuted bootstrap fixture",
            ["resources/custom.data"] = "game-specific-layout"
        });
        var packages = (await ImportCleanAsync(path)).Packages;
        var output = Output("other-game-archive");
        await _engine.ExportOfflineAsync(packages, new PackOptions { Game = "another_game" }, output);
        using var zip = ZipFile.OpenRead(output);
        Check(zip.Entries.All(e => !e.FullName.StartsWith("INSTALL_FILES/", StringComparison.OrdinalIgnoreCase)), "Unknown loader package was incorrectly routed to BepInEx.");
        await using var embedded = zip.GetEntry("mods/OtherTeam-Custom_Loader_Mod-2.0.0.zip")!.Open();
        await using var original = File.OpenRead(path);
        var embeddedHash = await SHA256.HashDataAsync(embedded);
        var originalHash = await SHA256.HashDataAsync(original);
        Check(embeddedHash.SequenceEqual(originalHash), "Other-game payload was altered by universal export.");
    }

    private async Task BundleMetadataAsync()
    {
        var output = Output("custom-game-metadata");
        await _engine.ExportOfflineAsync(_validPackages, new PackOptions { Game = "riskofrain2", InstallMode = "archive" }, output);
        var imported = await ImportCleanAsync(output);
        Check(imported.Game == "riskofrain2" && imported.InstallMode == "archive", "Bundle import lost its game or install mode.");
        Check(imported.Packages.Count == _validPackages.Count, "Bundle metadata altered package contents.");
        var legacy = Output("legacy-bundle-metadata");
        File.Copy(output, legacy);
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Update))
        {
            var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await ReadTextAsync(zip, "pack.json"))!;
            manifest.Remove("installMode");
            zip.GetEntry("pack.json")!.Delete();
            await using var stream = zip.CreateEntry("pack.json").Open();
            await JsonSerializer.SerializeAsync(stream, manifest);
        }
        var older = await ImportCleanAsync(legacy);
        Check(older.Packages.Count == _validPackages.Count, "Existing bundles without installMode must remain importable.");
    }

    private async Task InvalidInstallModeAsync()
    {
        var output = Output("invalid-install-mode");
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync(_validPackages, new PackOptions { InstallMode = "unknown-loader" }, output));
        Check(!File.Exists(output), "Invalid layout created an output archive.");
    }

    private async Task WrongLoaderLayoutAsync()
    {
        var path = await CreatePackageAsync("LavaGang", "MelonLoader", "0.7.0", [], new()
        {
            ["MelonLoader/Loader.dll"] = "different-loader",
            ["version.dll"] = "different-loader-bootstrap"
        });
        var packages = (await ImportCleanAsync(path)).Packages;
        await _engine.ExportOfflineAsync(packages, new PackOptions(), Output("melon-archive"));
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync(packages, new PackOptions { InstallMode = "bepinex" }, Output("wrong-loader")));
    }

    private static async Task<string> ReadTextAsync(ZipArchive zip, string name)
    {
        await using var stream = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task BundleRoundTripAsync()
    {
        var output = Output("roundtrip");
        await _engine.ExportOfflineAsync(_validPackages, _options, output);
        var restored = (await ImportCleanAsync(output)).Packages;
        Check(restored.Select(p => p.DependencyString).Order().SequenceEqual(_validPackages.Select(p => p.DependencyString).Order()), "Roundtrip identities changed.");
        Check(_engine.CheckDependencies(restored).Count == 0, "Roundtrip dependency graph changed.");
        var tampered = Output("tampered-bundle");
        File.Copy(output, tampered);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("mods/Climbers-Helpful_Mod-1.0.0.zip")!;
            byte[] bytes;
            using (var stream = entry.Open()) { using var memory = new MemoryStream(); await stream.CopyToAsync(memory); bytes = memory.ToArray(); }
            entry.Delete();
            await using var changed = zip.CreateEntry("mods/Climbers-Helpful_Mod-1.0.0.zip").Open();
            await changed.WriteAsync(bytes);
            changed.WriteByte(42);
        }
        var rejected = await _engine.ImportAsync([tampered]);
        Check(rejected.Packages.Count == 0 && rejected.Errors.Any(e => e.Contains("SHA256")), "Tampered archive must fail SHA256 without importing partial contents.");
    }

    private async Task PublicationExportAsync()
    {
        var output = Output("publish");
        await _engine.ExportThunderstoreAsync(_validPackages, _options, output);
        using var zip = ZipFile.OpenRead(output);
        Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "README.md", "icon.png", "manifest.json" }.Order()), "Publish archive must contain only standard root metadata.");
        Check(!(await ReadTextAsync(zip, "README.md")).Contains("PEAK", StringComparison.OrdinalIgnoreCase), "Publication instructions still force a specific game's community.");
        await using (var manifest = zip.GetEntry("manifest.json")!.Open())
        {
            using var doc = await JsonDocument.ParseAsync(manifest);
            Check(doc.RootElement.GetProperty("name").GetString() == "Generic_Test_Pack", "Publish package name changed.");
            var dependencies = doc.RootElement.GetProperty("dependencies").EnumerateArray().Select(e => e.GetString()!).Order();
            Check(dependencies.SequenceEqual(_validPackages.Select(p => p.DependencyString).Order()), "Published dependencies must pin all enabled packages.");
        }
        await using var icon = zip.GetEntry("icon.png")!.Open();
        using var memory = new MemoryStream(); await icon.CopyToAsync(memory);
        var png = memory.ToArray();
        Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Publish icon is not PNG.");
        Check(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) == 256 && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) == 256, "Publish icon must be exactly 256 × 256.");
    }

    private async Task CollisionAsync()
    {
        var a = await CreatePackageAsync("Team", "Config_A", "1.0.0", [], new() { ["config/shared.cfg"] = "A" });
        var b = await CreatePackageAsync("Team", "Config_B", "1.0.0", [], new() { ["BepInEx/config/shared.cfg"] = "B" });
        var packages = (await ImportCleanAsync(a, b)).Packages;
        var output = Output("collision-preserve");
        await File.WriteAllTextAsync(output, "existing-export");
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync(packages, _options, output));
        Check(await File.ReadAllTextAsync(output) == "existing-export", "Failed export overwrote an existing target.");
        Check(!Directory.EnumerateFiles(_root, "*.partial").Any(), "Failed export left a partial file.");
    }

    private async Task SourceOverwriteAsync()
    {
        var before = await File.ReadAllBytesAsync(_modPath);
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync(_validPackages, _options, _modPath));
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportThunderstoreAsync(_validPackages, _options, _modPath));
        var after = await File.ReadAllBytesAsync(_modPath);
        Check(before.SequenceEqual(after), "Source ZIP changed after refused overwrite.");
        var disabled = (await ImportCleanAsync(_modPath)).Packages.Single();
        disabled.Enabled = false;
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync([_validPackages.Single(p => p.IsLoader), disabled], _options, disabled.SourcePath));
    }

    private async Task CancellationAsync()
    {
        var output = Output("cancelled");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel());
        await ThrowsAsync<OperationCanceledException>(() => _engine.ExportOfflineAsync(_validPackages, _options, output, progress, cts.Token));
        Check(!File.Exists(output), "Cancelled export created a final output.");
        Check(!Directory.EnumerateFiles(_root, "*.partial").Any(), "Cancelled export left a temporary archive.");
        await File.WriteAllTextAsync(output, "previous-export");
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => _engine.ExportOfflineAsync(_validPackages, _options, output, ct: alreadyCancelled.Token));
        Check(await File.ReadAllTextAsync(output) == "previous-export", "Cancellation replaced an existing target.");
    }

    private async Task SourceMutationAsync()
    {
        var path = await CreatePackageAsync("Team", "Mutating", "1.0.0", [], new() { ["mod.dll"] = "payload" });
        var packages = (await ImportCleanAsync(path)).Packages;
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("manifest.json")!.Delete();
            await using var stream = zip.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(stream, new { name = "Mutating", version_number = "2.0.0", description = "changed", website_url = "", dependencies = Array.Empty<string>() });
        }
        await ThrowsAsync<InvalidDataException>(() => _engine.ExportOfflineAsync(packages, _options, Output("mutation")));
    }

    private string Output(string name) => Path.Combine(_root, name + ".zip");

    private async Task<ImportResult> ImportCleanAsync(params string[] paths)
    {
        var result = await _engine.ImportAsync(paths);
        Check(result.Errors.Count == 0, "Unexpected import errors: " + string.Join("; ", result.Errors));
        return result;
    }

    private async Task RejectedAsync(string path)
    {
        var result = await _engine.ImportAsync([path]);
        Check(result.Packages.Count == 0 && result.Errors.Count > 0, "Unsafe/invalid archive accepted: " + Path.GetFileName(path));
    }

    private async Task<string> CreatePackageAsync(string owner, string name, string version, string[] dependencies, Dictionary<string, string> files, string prefix = "")
    {
        var manifest = JsonSerializer.Serialize(new { name, version_number = version, description = "Synthetic smoke-test fixture", website_url = "https://example.com/does-not-identify-author", dependencies });
        var content = new Dictionary<string, string> { [prefix + "manifest.json"] = manifest, [prefix + "README.md"] = "# Synthetic fixture\nNot an actual game mod." };
        foreach (var pair in files) content[prefix + pair.Key] = pair.Value;
        return await CreateRawArchiveAsync($"{owner}-{name}-{version}.zip", content);
    }

    private async Task<string> CreateRawArchiveAsync(string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in files)
        {
            await using var stream = zip.CreateEntry(pair.Key).Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(pair.Value));
        }
        return path;
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (var i = 0; i <= bytes.Length - 4; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == signature) return i;
        return -1;
    }

    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
    }

    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    private sealed class PackageResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            LastUrl = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
