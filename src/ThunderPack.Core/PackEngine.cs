using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThunderPack.Core;

/// <summary>Reads data from packages; never runs a mod or writes to a game's directory.</summary>
public sealed partial class PackEngine
{
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxEntries = 50_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly HttpClient _http;
    public string CacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderPack", "cache");

    public PackEngine(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ThunderPack/1.0 (+https://thunderstore.io)");
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var result = new ImportResult();
        var suppliedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var suppliedPath in suppliedPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(suppliedPath);
                if (!File.Exists(path)) throw new InvalidDataException("文件不存在。");
                if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("只支持 ZIP 格式，请直接选择从 Thunderstore 下载的压缩包。");
                if (new FileInfo(path).Length > MaxArchiveBytes) throw new InvalidDataException("单个压缩包超过 2 GB 上限。");
                using var archive = ZipFile.OpenRead(path);
                ValidateArchive(archive);
                var bundle = archive.Entries.SingleOrDefault(e => Normalize(e.FullName).Equals("pack.json", StringComparison.OrdinalIgnoreCase));
                if (bundle is not null && !archive.Entries.Any(e => Normalize(e.FullName).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                {
                    var metadata = await ImportBundleAsync(archive, bundle, result.Packages, ct);
                    if (suppliedPaths.Length == 1)
                    {
                        result.Game = string.IsNullOrWhiteSpace(metadata.Game) ? "generic" : metadata.Game;
                        result.InstallMode = metadata.InstallMode is "archive" or "bepinex" ? metadata.InstallMode : "archive";
                    }
                }
                else result.Packages.Add(await ReadPackageAsync(archive, path, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"{Path.GetFileName(suppliedPath)}：{ex.Message}");
            }
        }
        InferNamespaces(result.Packages);
        return result;
    }

    public IReadOnlyList<DependencyIssue> CheckDependencies(IEnumerable<ModPackage> packages) => CheckDependenciesCore(packages);

    private static IReadOnlyList<DependencyIssue> CheckDependenciesCore(IEnumerable<ModPackage> packages)
    {
        var enabled = packages.Where(p => p.Enabled).ToList();
        InferNamespaces(enabled);
        var issues = new List<DependencyIssue>();
        foreach (var package in enabled)
        {
            if (!package.HasNamespace || !OwnerPattern().IsMatch(package.Namespace))
                issues.Add(new() { Kind = "identity", RequiredBy = package.Name, Dependency = package.Name,
                    Message = $"{package.Name} 缺少有效作者 / 团队名；请使用官网原始文件名，或手动补充。" });
        }
        foreach (var group in enabled.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new() { Kind = "duplicate", Dependency = group.Key, RequiredBy = group.Key,
                Message = $"{group.Key} 同时启用了多个副本或版本，请只保留一个。" });

        var requests = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in enabled)
        foreach (var dependency in package.Dependencies)
        {
            if (!TryParseDependency(dependency, out var reference))
            {
                issues.Add(new() { Kind = "invalid", Dependency = dependency, RequiredBy = package.Id,
                    Message = $"{package.Id} 的前置声明格式无效：{dependency}" });
                continue;
            }
            if (!requests.TryGetValue(reference.Id, out var versions)) requests[reference.Id] = versions = [];
            versions.Add(reference.Version);
            var installed = enabled.Where(p => p.Id.Equals(reference.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (installed.Any(p => p.Version == reference.Version)) continue;
            var missing = installed.Count == 0;
            issues.Add(new()
            {
                Kind = missing ? "missing" : "version", Dependency = dependency, RequiredBy = package.Id,
                CanDownload = missing, DownloadUrl = reference.DownloadUrl,
                Message = missing ? $"{package.Id} 缺少前置 {dependency}。"
                    : $"{package.Id} 声明 {reference.Version}，当前 {reference.Id} 为 {string.Join("、", installed.Select(p => p.Version))}；导出使用精确版本校验。"
            });
        }
        foreach (var request in requests.Where(r => r.Value.Count > 1))
            issues.Add(new() { Kind = "conflict", Dependency = request.Key, RequiredBy = "多个 Mod",
                Message = $"多个 Mod 为 {request.Key} 声明了不同版本（{string.Join("、", request.Value)}）；请统一版本后导出。" });
        return issues;
    }

    public static bool TryParseDependency(string value, out DependencyReference reference)
    {
        reference = new("", "", "");
        if (string.IsNullOrEmpty(value)) return false;
        var match = DependencyPattern().Match(value);
        if (!match.Success) return false;
        reference = new(match.Groups["owner"].Value, match.Groups["name"].Value, match.Groups["version"].Value);
        return true;
    }

    public async Task<ModPackage> DownloadDependencyAsync(string dependency, string cacheDirectory,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!TryParseDependency(dependency, out var reference)) throw new ArgumentException("前置 Mod 标识格式无效。", nameof(dependency));
        Directory.CreateDirectory(cacheDirectory);
        var target = Path.Combine(Path.GetFullPath(cacheDirectory), reference.FullName + ".zip");
        if (File.Exists(target))
        {
            try { return await VerifyDownloadedAsync(target, reference, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* Download a fresh copy into a separate file. */ }
        }
        progress?.Report($"正在下载 {reference.FullName}…");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".zip";
        try
        {
            using var response = await _http.GetAsync(reference.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException($"官网没有找到 {reference.FullName}。请检查作者及版本。");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new InvalidDataException("下载文件超过 2 GB 上限。");
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await CopyLimitedAsync(source, destination, MaxArchiveBytes, ct);
            // The temporary filename is not an identity source. Use the verified request identity only after checking the manifest.
            using (var archive = ZipFile.OpenRead(temporary))
            {
                ValidateArchive(archive);
                var package = await ReadPackageAsync(archive, temporary, ct);
                if (!package.Name.Equals(reference.Name, StringComparison.Ordinal) || package.Version != reference.Version)
                    throw new InvalidDataException("下载包的名称或版本与请求不符，已拒绝导入。");
            }
            File.Move(temporary, target, true);
            return await VerifyDownloadedAsync(target, reference, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> ExportOfflineAsync(IEnumerable<ModPackage> packages, PackOptions options, string outputPath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var enabled = ValidateExport(packages, options, outputPath);
        var includeInstallFiles = options.InstallMode == "bepinex";
        return await WriteAtomicAsync(outputPath, async archive =>
        {
            var locks = new List<LockedPackage>();
            var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in enabled.OrderByDescending(p => p.IsLoader).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"正在打包 {package.Id}…");
                using var input = ZipFile.OpenRead(package.SourcePath);
                ValidateArchive(input);
                await VerifySourceAsync(input, package, ct);
                string? loaderRoot = null;
                if (includeInstallFiles)
                {
                    if (package.IsLoader && !package.IsBepInExLoader)
                        throw new InvalidDataException($"{package.Id} 不是 BepInEx 加载器。请改用「通用原包」导出，按该游戏的加载器说明安装。");
                    if (package.IsBepInExLoader) loaderRoot = FindBepInExLoaderRoot(input, package);
                    else EnsureBepInExCompatible(input, package);
                }
                var fileName = "mods/" + package.DependencyString + ".zip";
                var entry = archive.CreateEntry(fileName, CompressionLevel.NoCompression);
                string hash;
                await using (var original = File.OpenRead(package.SourcePath))
                {
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(original, ct)).ToLowerInvariant();
                    original.Position = 0;
                    await using var stream = entry.Open();
                    await original.CopyToAsync(stream, ct);
                }
                locks.Add(new() { Namespace = package.Namespace, Name = package.Name, Version = package.Version,
                    Archive = fileName, Sha256 = hash, Dependencies = package.Dependencies });
                if (!includeInstallFiles) continue;
                foreach (var source in input.Entries.Where(e => !IsDirectory(e)))
                {
                    var relative = RelativeEntry(source, package.ArchiveRoot);
                    if (relative is null) continue;
                    var mapped = MapBepInExEntry(package, relative, loaderRoot);
                    if (mapped is null) continue;
                    var target = "INSTALL_FILES/" + mapped;
                    var digest = await HashEntryAsync(source, ct);
                    if (destinations.TryGetValue(target, out var existing))
                    {
                        if (digest != existing) throw new InvalidDataException($"不同 Mod 写入相同文件：{mapped}。请检查包内容，避免互相覆盖。");
                        continue;
                    }
                    destinations[target] = digest;
                    await using var from = source.Open();
                    await using var to = archive.CreateEntry(target, CompressionLevel.Optimal).Open();
                    await from.CopyToAsync(to, ct);
                }
            }
            var bundle = new BundleManifest { Name = options.Name, Version = options.Version, Description = options.Description,
                Game = options.Game, InstallMode = options.InstallMode, CreatedAt = DateTimeOffset.UtcNow, Packages = locks,
                IncludesLoader = enabled.Any(p => p.IsLoader) };
            await WriteTextAsync(archive, "pack.json", JsonSerializer.Serialize(bundle, JsonOptions), ct);
            await WriteTextAsync(archive, "README_安装说明.txt", OfflineReadme(options, enabled), ct);
        }, ct);
    }

    public async Task<string> ExportThunderstoreAsync(IEnumerable<ModPackage> packages, PackOptions options, string outputPath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var enabled = ValidateExport(packages, options, outputPath);
        var icon = options.IconBytes ?? CreateDefaultIcon();
        ValidateIcon(icon);
        return await WriteAtomicAsync(outputPath, async archive =>
        {
            progress?.Report("正在生成 Thunderstore 发布包…");
            var manifest = new { name = options.Name, version_number = options.Version, website_url = options.WebsiteUrl,
                description = options.Description, dependencies = enabled.Select(p => p.DependencyString).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray() };
            await WriteTextAsync(archive, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions), ct);
            var readme = $"# {options.Name}\n\n{options.Description}\n\n游戏 / 社区：{GameLabel(options.Game)}\n\n## 安装\n\n在对应游戏的 Thunderstore Mod Manager / r2modman 中安装本整合包，管理器会下载以下依赖。本发布包不包含第三方 Mod 二进制文件。\n\n"
                + string.Join("\n", enabled.Select(p => $"- `{p.DependencyString}` — {p.Description}"))
                + "\n\n由 ThunderPack 生成。发布前请确认所有 Mod 属于同一游戏、许可和兼容性，并在对应游戏社区选择 Modpacks 分类。\n";
            await WriteTextAsync(archive, "README.md", readme, ct);
            await using var image = archive.CreateEntry("icon.png").Open();
            await image.WriteAsync(icon, ct);
        }, ct);
    }

    private static List<ModPackage> ValidateExport(IEnumerable<ModPackage> packages, PackOptions options, string outputPath)
    {
        var all = packages.ToList();
        var enabled = all.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0) throw new InvalidDataException("请至少导入并启用一个 Mod。");
        if (!NamePattern().IsMatch(options.Name) || options.Name.Length > 128)
            throw new InvalidDataException("整合包名称只能包含英文字母、数字和下划线，长度为 1–128 个字符。");
        if (!VersionPattern().IsMatch(options.Version)) throw new InvalidDataException("版本号应为三个数字段，例如 1.0.0。");
        if (string.IsNullOrWhiteSpace(options.Description) || options.Description.Length > 250)
            throw new InvalidDataException("简介不能为空，且不能超过 250 个字符。");
        if (options.Game.Length > 128 || options.Game.Any(char.IsControl))
            throw new InvalidDataException("游戏 / 社区名称最多 128 个字符，且不能包含控制字符。");
        if (options.InstallMode is not ("archive" or "bepinex"))
            throw new InvalidDataException("请选择「通用原包」或「BepInEx」安装布局。");
        if (!string.IsNullOrEmpty(options.WebsiteUrl) && (!Uri.TryCreate(options.WebsiteUrl, UriKind.Absolute, out var website)
            || (website.Scheme != "http" && website.Scheme != "https"))) throw new InvalidDataException("网站地址应为有效的 HTTP 或 HTTPS 地址，或留空。");
        var issues = CheckDependenciesCore(enabled);
        if (issues.Count > 0) throw new InvalidDataException("导出前请处理前置检查：" + string.Join("\n", issues.Take(6).Select(i => i.Message)));
        var fullOutput = Path.GetFullPath(outputPath);
        foreach (var package in all)
        {
            if (package.Enabled && !File.Exists(package.SourcePath)) throw new FileNotFoundException($"源压缩包已移动或删除：{package.Id}", package.SourcePath);
            if (Path.GetFullPath(package.SourcePath).Equals(fullOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("导出位置不能覆盖已导入的原始 Mod 压缩包。");
        }
        return enabled;
    }

    private static async Task<ModPackage> ReadPackageAsync(ZipArchive archive, string sourcePath, CancellationToken ct)
    {
        var manifests = archive.Entries.Where(e => !IsDirectory(e) && Path.GetFileName(Normalize(e.FullName)).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => Normalize(e.FullName).Count(c => c == '/')).ToList();
        if (manifests.Count == 0) throw new InvalidDataException("未找到 manifest.json；这不是有效的 Thunderstore Mod 压缩包。");
        var minDepth = Normalize(manifests[0].FullName).Count(c => c == '/');
        if (manifests.Count(e => Normalize(e.FullName).Count(c => c == '/') == minDepth) != 1)
            throw new InvalidDataException("压缩包中存在多个同级 manifest.json，无法确定 Mod。");
        if (minDepth > 1) throw new InvalidDataException("manifest.json 必须在 ZIP 根目录，或一个包装目录中。");
        var manifestEntry = manifests[0];
        using var document = JsonDocument.Parse(await ReadEntryBytesAsync(manifestEntry, 1024 * 1024, ct));
        var root = document.RootElement;
        var name = RequiredString(root, "name");
        var version = RequiredString(root, "version_number");
        if (!NamePattern().IsMatch(name) || name.Length > 128) throw new InvalidDataException("manifest 中的 Mod 名称无效。");
        if (!VersionPattern().IsMatch(version)) throw new InvalidDataException("manifest 中的版本号无效，应为 Major.Minor.Patch。");
        if (!root.TryGetProperty("dependencies", out var dependencyArray) || dependencyArray.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("manifest 缺少 dependencies 数组。");
        if (dependencyArray.GetArrayLength() > 2000) throw new InvalidDataException("前置依赖过多。");
        var dependencies = dependencyArray.EnumerateArray().Select(d => d.ValueKind == JsonValueKind.String ? d.GetString()! : throw new InvalidDataException("依赖项必须为字符串。"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var archiveRoot = minDepth == 0 ? "" : Normalize(manifestEntry.FullName)[..(Normalize(manifestEntry.FullName).LastIndexOf('/') + 1)];
        var expectedSuffix = "-" + name + "-" + version;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var owner = stem.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase) ? stem[..^expectedSuffix.Length] : "";
        if (!OwnerPattern().IsMatch(owner)) owner = "";
        var iconEntry = archive.Entries.FirstOrDefault(e => Normalize(e.FullName).Equals(archiveRoot + "icon.png", StringComparison.OrdinalIgnoreCase));
        byte[]? icon = null;
        if (iconEntry is not null && iconEntry.Length <= 2 * 1024 * 1024)
            icon = await ReadEntryBytesAsync(iconEntry, 2 * 1024 * 1024, ct);
        return new ModPackage { Namespace = owner, Name = name, Version = version,
            Description = OptionalString(root, "description"), WebsiteUrl = OptionalString(root, "website_url"),
            SourcePath = sourcePath, ArchiveRoot = archiveRoot, Dependencies = dependencies,
            FileSize = new FileInfo(sourcePath).Length, IconBytes = icon };
    }

    private async Task<BundleManifest> ImportBundleAsync(ZipArchive archive, ZipArchiveEntry manifestEntry, List<ModPackage> destination, CancellationToken ct)
    {
        var bytes = await ReadEntryBytesAsync(manifestEntry, 4 * 1024 * 1024, ct);
        var bundle = JsonSerializer.Deserialize<BundleManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("整合包清单为空。");
        if (bundle.Format != "thunderpack/1" || bundle.Packages.Count == 0 || bundle.Packages.Count > 2000)
            throw new InvalidDataException("不支持此整合包格式或清单中没有 Mod。");
        var directory = Path.Combine(Path.GetFullPath(CacheDirectory), "imports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var imported = new List<ModPackage>();
        try
        {
            foreach (var locked in bundle.Packages)
            {
                ct.ThrowIfCancellationRequested();
                if (!OwnerPattern().IsMatch(locked.Namespace) || !NamePattern().IsMatch(locked.Name) || !VersionPattern().IsMatch(locked.Version))
                    throw new InvalidDataException("整合包包含无效的 Mod 标识。");
                if (locked.Archive != $"mods/{locked.Namespace}-{locked.Name}-{locked.Version}.zip")
                    throw new InvalidDataException("整合包中的 Mod 压缩包路径与标识不符。");
                var entry = archive.Entries.SingleOrDefault(e => Normalize(e.FullName).Equals(locked.Archive, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"整合包缺少 {locked.Archive}。");
                if (entry.Length > MaxArchiveBytes) throw new InvalidDataException("整合包内的 Mod 超过 2 GB。");
                var path = Path.Combine(directory, Path.GetFileName(locked.Archive));
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await using (var input = entry.Open()) await CopyLimitedAsync(input, output, MaxArchiveBytes, ct);
                await using (var input = File.OpenRead(path))
                {
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                    if (!digest.Equals(locked.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{locked.Name} 的 SHA256 校验失败。");
                }
                using var nested = ZipFile.OpenRead(path);
                ValidateArchive(nested);
                var package = await ReadPackageAsync(nested, path, ct);
                if (package.Name != locked.Name || package.Version != locked.Version)
                    throw new InvalidDataException("整合包清单与 Mod 元数据不符。");
                imported.Add(package);
            }
            destination.AddRange(imported);
            return bundle;
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    private static void InferNamespaces(IEnumerable<ModPackage> packages)
    {
        var list = packages.ToList();
        var references = list.SelectMany(p => p.Dependencies).Select(d => TryParseDependency(d, out var r) ? r : null).Where(r => r is not null).Cast<DependencyReference>().ToArray();
        foreach (var package in list.Where(p => !p.HasNamespace))
        {
            var owners = references.Where(r => r.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase) && r.Version == package.Version)
                .Select(r => r.Namespace).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (owners.Length == 1) package.Namespace = owners[0];
        }
    }

    private static string FindBepInExLoaderRoot(ZipArchive archive, ModPackage package)
    {
        var files = archive.Entries.Where(e => !IsDirectory(e)).Select(e => RelativeEntry(e, package.ArchiveRoot))
            .Where(p => p is not null).Cast<string>().ToArray();
        var candidates = new[] { "" }.Concat(files.Where(p => p.Contains('/'))
            .Select(p => p[..(p.IndexOf('/') + 1)])).Distinct(StringComparer.OrdinalIgnoreCase);
        var matches = candidates.Where(prefix =>
            files.Any(p => p.StartsWith(prefix + "BepInEx/core/", StringComparison.OrdinalIgnoreCase)
                && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            && files.Any(p => p.Equals(prefix + "winhttp.dll", StringComparison.OrdinalIgnoreCase)
                || p.Equals(prefix + "version.dll", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{package.Id} 无法确定 Windows BepInEx 加载器根目录；需要 BepInEx/core 和同级 winhttp.dll 或 version.dll。请使用通用原包导出。");
        return matches[0];
    }

    private static void EnsureBepInExCompatible(ZipArchive archive, ModPackage package)
    {
        var incompatibleFolders = new[] { "MelonLoader", "Mods", "UserLibs", "UserData", "GDWeave", "ReturnOfModding", "shimloader" };
        foreach (var entry in archive.Entries.Where(e => !IsDirectory(e)))
        {
            var relative = RelativeEntry(entry, package.ArchiveRoot);
            if (relative is null) continue;
            if (relative.Split('/').SkipLast(1).Any(part => incompatibleFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{package.Id} 包含其他加载器的目录 {relative}，不能按 BepInEx 布局安装。请使用通用原包导出。");
        }
    }

    private static string? MapBepInExEntry(ModPackage package, string relative, string? loaderRoot)
    {
        var pieces = relative.Split('/');
        var fileName = pieces[^1];
        if (pieces.Any(p => (p.StartsWith('.') && !p.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase))
            || p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) return null;
        if (pieces.Length == 1 && IsMetadata(fileName)) return null;
        if (package.IsBepInExLoader)
        {
            if (loaderRoot is null) throw new InvalidDataException("无法识别 BepInEx 加载器目录。");
            if (!relative.StartsWith(loaderRoot, StringComparison.OrdinalIgnoreCase)) return null;
            relative = relative[loaderRoot.Length..];
            if (!relative.Contains('/') && IsMetadata(relative)) return null;
            if (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{package.Id} 包含专用程序 {relative}，请使用通用原包导出。");
            return relative;
        }
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{package.Id} 包含专用程序 {relative}，不能按通用 BepInEx 布局安装。请使用通用原包导出。");
        var routes = new[] { "plugins", "patchers", "core", "config", "monomod" };
        for (var start = 0; start < pieces.Length - 1; start++)
        {
            if (routes.FirstOrDefault(r => r.Equals(pieces[start], StringComparison.OrdinalIgnoreCase)) is not { } route) continue;
            var remaining = string.Join('/', pieces.Skip(start + 1));
            if (route == "config") return $"BepInEx/{route}/{remaining}";
            return $"BepInEx/{route}/{package.Id}/{remaining}";
        }
        if (fileName.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{package.Id} 包含不能自动路由的游戏根目录文件 {relative}。请检查是否为专用加载器。");
        // Common BepInEx package routes flatten folders outside a recognized route.
        var fallback = fileName.EndsWith(".mm.dll", StringComparison.OrdinalIgnoreCase) ? "monomod" : "plugins";
        return $"BepInEx/{fallback}/{package.Id}/{fileName}";
    }

    private static string? RelativeEntry(ZipArchiveEntry entry, string prefix)
    {
        var path = Normalize(entry.FullName);
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return path[prefix.Length..];
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("压缩包文件数量超过安全上限。");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = Normalize(entry.FullName);
            if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Length > 512)
                throw new InvalidDataException("压缩包包含无效或过长的路径。");
            var pieces = path.TrimEnd('/').Split('/');
            foreach (var part in pieces)
            {
                if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || part.Any(char.IsControl)
                    || ReservedNamePattern().IsMatch(part)) throw new InvalidDataException($"压缩包包含不安全的路径：{entry.FullName}");
            }
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new InvalidDataException("压缩包包含符号链接，已拒绝导入。");
            if (!names.Add(path.TrimEnd('/'))) throw new InvalidDataException($"压缩包存在大小写重复路径：{path}");
            if (entry.Length > MaxArchiveBytes) throw new InvalidDataException("压缩包中的单个文件超过 2 GB。");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("压缩包解压后超过 4 GB 上限。");
            if (entry.Length > 10 * 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 2000)
                throw new InvalidDataException("压缩包的压缩比例异常，已拒绝导入。");
        }
    }

    private static async Task<ModPackage> VerifyDownloadedAsync(string path, DependencyReference reference, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        ValidateArchive(archive);
        var package = await ReadPackageAsync(archive, path, ct);
        if (package.Name != reference.Name || package.Version != reference.Version || package.Namespace != reference.Namespace)
            throw new InvalidDataException("缓存的 Mod 身份与请求不符。");
        return package;
    }

    private static async Task VerifySourceAsync(ZipArchive archive, ModPackage package, CancellationToken ct)
    {
        var current = await ReadPackageAsync(archive, package.SourcePath, ct);
        if (current.Name != package.Name || current.Version != package.Version
            || current.ArchiveRoot != package.ArchiveRoot || !current.Dependencies.SequenceEqual(package.Dependencies))
            throw new InvalidDataException($"{package.Id} 的源文件在导入后发生变化，请重新导入。");
    }

    private static async Task<string> WriteAtomicAsync(string outputPath, Func<ZipArchive, Task> write, CancellationToken ct)
    {
        var target = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create)) await write(archive);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteTextAsync(ZipArchive archive, string name, string text, CancellationToken ct)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry, long maximum, CancellationToken ct)
    {
        if (entry.Length > maximum) throw new InvalidDataException($"{entry.Name} 超过大小限制。");
        await using var from = entry.Open();
        using var output = new MemoryStream();
        await CopyLimitedAsync(from, output, maximum, ct);
        return output.ToArray();
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long maximum, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("文件实际大小超过限制。");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"manifest 缺少有效的 {property}。");
        return value.GetString()!;
    }
    private static string OptionalString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static bool IsDirectory(ZipArchiveEntry entry) => Normalize(entry.FullName).EndsWith('/');
    private static bool IsMetadata(string name) => name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("icon.png", StringComparison.OrdinalIgnoreCase) || name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CHANGELOG", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase);

    private static string GameLabel(string game) => string.IsNullOrWhiteSpace(game) || game.Equals("generic", StringComparison.OrdinalIgnoreCase)
        ? "通用 / 未指定游戏" : game;

    private static string OfflineReadme(PackOptions options, List<ModPackage> packages)
    {
        var installation = options.InstallMode == "bepinex"
            ? "安装布局：通用 Windows BepInEx\r\n"
                + "1. 确认目标游戏使用此 BepInEx 布局，并关闭游戏。此预设不是全部游戏的安装规则。\r\n"
                + "2. 找到游戏可执行文件所在目录，备份已有 BepInEx、winhttp.dll、version.dll 和 doorstop_config.ini。\r\n"
                + "3. 按该游戏的安装说明，将 INSTALL_FILES 里面的内容复制到正确位置。不要直接复制 INSTALL_FILES 文件夹本身。\r\n"
                + "4. 启动游戏；多人联机时应按各 Mod 说明保持所需版本一致。\r\n"
                + (packages.Any(p => p.IsBepInExLoader) ? "此包包含 BepInEx 加载器原包，请确认其版本适用于目标游戏。\r\n"
                    : "此包未包含 BepInEx 加载器；目标游戏需要先装好适用的加载器。\r\n")
            : "安装布局：通用原包\r\n"
                + "本整合包保留各 Mod 的原始 ZIP，不生成游戏安装目录，也不假定目标游戏使用哪种加载器。\r\n"
                + "将本 ZIP 拖入 ThunderPack 可离线恢复完整 Mod 列表，再按目标游戏和加载器的说明安装 mods/ 中的原包。\r\n"
                + "不要将 mods/ 中全部压缩包直接解压到游戏目录；不同游戏的文件布局可能不同。\r\n";
        return $"{options.Name}  v{options.Version}\r\n{options.Description}\r\n游戏 / 社区：{GameLabel(options.Game)}\r\n\r\n"
            + installation + "\r\n前置检查使用精确版本；只检查声明，不会确认所有 Mod 属于同一游戏或验证运行兼容性。\r\n"
            + "mods/ 保留原始 ZIP；pack.json 包含游戏标签、安装布局和 SHA256 校验值。\r\n"
            + "此 ZIP 为 ThunderPack 离线整合格式，不能作为 r2modman / Thunderstore profile 导入。\r\n"
            + "分享前请确认各 Mod 的分发许可。程序不会自动修改游戏或运行压缩包中的文件。\r\n\r\nMod 清单\r\n"
            + string.Join("\r\n", packages.Select(p => $"- {p.DependencyString}"));
    }

    public static byte[] CreateDefaultIcon()
    {
        // A compact, deterministic 256 × 256 RGBA PNG, generated without image libraries.
        using var raw = new MemoryStream();
        for (var y = 0; y < 256; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < 256; x++)
            {
                var bolt = (y >= 38 && y < 133 && x >= 139 - (y - 38) * 64 / 95 && x < 171)
                    || (y >= 122 && y < 218 && x > 85 && x <= 126 + (218 - y) * 57 / 96);
                raw.WriteByte(bolt ? (byte)255 : (byte)113);
                raw.WriteByte(bolt ? (byte)255 : (byte)87);
                raw.WriteByte(bolt ? (byte)255 : (byte)237);
                raw.WriteByte(255);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(raw.ToArray());
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 256);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 256);
        header[8] = 8; header[9] = 6;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void ValidateIcon(byte[] icon)
    {
        if (icon.Length < 33 || !icon.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(16, 4)) != 256 || BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(20, 4)) != 256)
            throw new InvalidDataException("发布图标必须为 256 × 256 的 PNG 图片。");
    }

    private static void WritePngChunk(Stream output, string name, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length); output.Write(length);
        var kind = Encoding.ASCII.GetBytes(name); output.Write(kind); output.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in kind.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")] private static partial Regex NamePattern();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_-]*$")] private static partial Regex OwnerPattern();
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9_][A-Za-z0-9_-]*)-(?<name>[A-Za-z0-9_]+)-(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))$")]
    private static partial Regex DependencyPattern();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase)] private static partial Regex ReservedNamePattern();

    private sealed class BundleManifest
    {
        public string Format { get; set; } = "thunderpack/1";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Game { get; set; } = "generic";
        public string InstallMode { get; set; } = "archive";
        public DateTimeOffset CreatedAt { get; set; }
        public bool IncludesLoader { get; set; }
        public List<LockedPackage> Packages { get; set; } = [];
    }
    private sealed class LockedPackage
    {
        public string Namespace { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Archive { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string[] Dependencies { get; set; } = [];
    }
}
