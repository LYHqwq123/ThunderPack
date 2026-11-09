using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ThunderPack.Core;

// Opt-in integration verification against unmodified official packages. Never executes game/mod code.
// Run from repository root. Supply --download to fetch missing dependencies from Thunderstore.
var root = Directory.GetCurrentDirectory();
var cache = Path.Combine(root, "artifacts", "real-cache");
var input = Path.Combine(root, "artifacts", "real-mods");
var output = Path.Combine(root, "artifacts", "real-output");
Directory.CreateDirectory(cache);
Directory.CreateDirectory(output);
var engine = new PackEngine { CacheDirectory = cache };
var mainPath = Path.Combine(input, "glarmer-PEAK_Unlimited-4.0.2.zip");
Require(File.Exists(mainPath), "Official PEAK_Unlimited ZIP is present.");
var imports = await engine.ImportAsync([mainPath]);
Require(imports.Errors.Count == 0, "Official PEAK_Unlimited ZIP imports: " + string.Join("; ", imports.Errors));
Require(imports.Packages.Count == 1, "Exactly one main Mod imported.");
var main = imports.Packages[0];
Require(main.DependencyString == "glarmer-PEAK_Unlimited-4.0.2", "Author, name and version parsed from official filename.");
Require(main.Dependencies.SequenceEqual(new[] { "BepInEx-BepInExPack_PEAK-5.4.75301" }), "Real manifest declares the expected loader version.");
var initial = engine.CheckDependencies(imports.Packages);
Require(initial.Count == 1 && initial[0].Kind == "missing" && initial[0].CanDownload, "Missing loader detected with downloadable exact version.");
var packages = imports.Packages;
var loaderFileName = main.Dependencies[0] + ".zip";
var loaderCandidates = new[] { Path.Combine(input, loaderFileName), Path.Combine(cache, loaderFileName) };
var loaderPath = loaderCandidates.FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length > 0);
if (loaderPath is not null)
{
    var loaderImport = await engine.ImportAsync([loaderPath]);
    Require(loaderImport.Errors.Count == 0, "Official loader ZIP imports: " + string.Join("; ", loaderImport.Errors));
    packages.AddRange(loaderImport.Packages);
}
else if (args.Contains("--download", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Downloading exact declared loader dependency from Thunderstore…");
    packages.Add(await engine.DownloadDependencyAsync(main.Dependencies[0], cache, new Progress<string>(Console.WriteLine)));
}
else throw new InvalidOperationException("Official loader ZIP is missing. Supply --download or place it in artifacts/real-mods/.");
Require(packages.Count == 2 && packages.Count(p => p.IsLoader) == 1, "PEAK loader recognized.");
Require(engine.CheckDependencies(packages).Count == 0, "Exact dependency closure is complete.");
var loader = packages.Single(p => p.IsLoader);
string[] loaderEntries;
using (var archive = ZipFile.OpenRead(loader.SourcePath))
{
    loaderEntries = archive.Entries.Select(e => e.FullName).ToArray();
    Require(loaderEntries.Any(e => e.EndsWith("BepInEx/core/BepInEx.dll", StringComparison.OrdinalIgnoreCase)), "Real loader contains BepInEx core.");
    Require(loaderEntries.Any(e => e.EndsWith("winhttp.dll", StringComparison.OrdinalIgnoreCase)), "Real loader contains Doorstop Windows bootstrap.");
}
var options = new PackOptions { Name = "PEAK_Real_Verification", Version = "1.0.0", Description = "Real official PEAK_Unlimited + exact BepInEx loader integration verification.", Game = "peak", InstallMode = "bepinex" };
var offline = await engine.ExportOfflineAsync(packages, options, Path.Combine(output, "PEAK_Real_Verification_offline.zip"));
string[] offlineEntries;
using (var archive = ZipFile.OpenRead(offline))
{
    offlineEntries = archive.Entries.Select(e => e.FullName).ToArray();
    foreach (var expected in new[] { "pack.json", "README_安装说明.txt", "INSTALL_FILES/winhttp.dll", "INSTALL_FILES/doorstop_config.ini",
        "INSTALL_FILES/BepInEx/core/BepInEx.dll", "INSTALL_FILES/BepInEx/plugins/glarmer-PEAK_Unlimited/PEAKUnlimited.dll" })
        Require(offlineEntries.Contains(expected, StringComparer.OrdinalIgnoreCase), "Offline layout includes " + expected);
    foreach (var package in packages)
    {
        var sourceHash = await Digest(package.SourcePath);
        var entry = archive.GetEntry($"mods/{package.DependencyString}.zip") ?? throw new InvalidOperationException("Original archive missing.");
        await using var stream = entry.Open();
        var embeddedHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        Require(sourceHash == embeddedHash, "Original ZIP bytes preserved: " + package.Id);
    }
}
var reimport = await engine.ImportAsync([offline]);
Require(reimport.Errors.Count == 0 && reimport.Packages.Count == packages.Count, "Offline bundle reimports with complete hash validation.");
Require(reimport.Packages.Select(p => p.DependencyString).Order().SequenceEqual(packages.Select(p => p.DependencyString).Order()), "Roundtrip identities and versions preserved.");
foreach (var original in packages)
{
    var restored = reimport.Packages.Single(p => p.DependencyString == original.DependencyString);
    Require(await Digest(original.SourcePath) == await Digest(restored.SourcePath), "Roundtrip original SHA256 preserved: " + original.Id);
}
Require(engine.CheckDependencies(reimport.Packages).Count == 0, "Roundtrip dependency closure complete.");
var publication = await engine.ExportThunderstoreAsync(packages, options, Path.Combine(output, "PEAK_Real_Verification_thunderstore.zip"));
using (var archive = ZipFile.OpenRead(publication))
{
    Require(archive.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "README.md", "icon.png", "manifest.json" }.Order()), "Publication ZIP has exactly the three root metadata files.");
    await using var stream = archive.GetEntry("manifest.json")!.Open();
    using var manifest = await JsonDocument.ParseAsync(stream);
    var dependencies = manifest.RootElement.GetProperty("dependencies").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();
    Require(dependencies.SequenceEqual(packages.Select(p => p.DependencyString).Order()), "Publication references exact official dependencies.");
    await using var iconStream = archive.GetEntry("icon.png")!.Open();
    var header = new byte[24];
    await iconStream.ReadExactlyAsync(header);
    Require(BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)) == 256 && BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)) == 256, "Publication icon is 256 × 256.");
}
var report = new
{
    verifiedAtUtc = DateTimeOffset.UtcNow,
    outcome = "passed",
    checks = "Real package import, exact dependency resolution, PEAK loader routing, offline and publication export, byte-preserving ZIP roundtrip.",
    packages = await Task.WhenAll(packages.Select(async p => new { identity = p.DependencyString, p.Dependencies, bytes = new FileInfo(p.SourcePath).Length, sha256 = await Digest(p.SourcePath) })),
    loaderEntries,
    offline = new { path = offline, bytes = new FileInfo(offline).Length, entries = offlineEntries },
    publication = new { path = publication, bytes = new FileInfo(publication).Length },
    limitations = "Static archive verification only. PEAK was not launched; runtime Mod compatibility and multiplayer behavior were not tested."
};
await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS: Real official ZIP integration verification completed. Report: " + Path.Combine(output, "verification.json"));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS: " + message);
}
static async Task<string> Digest(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}
