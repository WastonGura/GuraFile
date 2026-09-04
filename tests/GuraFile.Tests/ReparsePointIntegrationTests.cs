using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class ReparsePointIntegrationTests
{
    [TestMethod]
    public async Task Junction_SelfReferencingLoop_TerminatesSafelyWithoutOverflow()
    {
        using var temp = TempDirectory.Create();
        var logsDir = temp.CreateDirectory("logs");
        var logger = new DiagnosticLogger(logsDir);

        var rootPath = temp.CreateDirectory("root");
        var subPath = Path.Combine(rootPath, "sub");
        Directory.CreateDirectory(subPath);

        var legitFile = Path.Combine(subPath, "real_document.txt");
        await File.WriteAllTextAsync(legitFile, "Hello from inside managed root");

        // Create an NTFS junction pointing to the root directory (self-referencing loop)
        var junctionPath = Path.Combine(subPath, "loop_junction");
        var created = CreateJunction(junctionPath, rootPath);
        Assert.IsTrue(created, "Failed to create directory junction using mklink /J.");
        temp.Junctions.Add(junctionPath);

        var dbPath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(dbPath, logger: logger);
        var root = scanner.AddRoot(rootPath);

        // Scan must finish promptly without hanging, stack overflow, or exploding into infinite loop
        var scanResult = await scanner.ScanAsync(root.Id);

        Assert.IsFalse(scanResult.Canceled);
        Assert.AreEqual(1, scanResult.CommittedFiles);
        Assert.AreEqual(1, scanResult.AddedFiles);
        Assert.IsGreaterThanOrEqualTo(1, scanResult.SkippedReparsePoints);

        var files = await new FileQueryService(dbPath).QueryAsync(new());
        Assert.HasCount(1, files);
        Assert.AreEqual(legitFile, files[0].Path);

        // Verify DiagnosticLogger logged ReparsePointSkipped
        var logFiles = Directory.GetFiles(logsDir, "gurafile_*.log");
        Assert.HasCount(1, logFiles);

        var entries = File.ReadAllLines(logFiles[0])
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var skippedEntries = entries
            .Where(e => e.GetProperty("event").GetString() == "ReparsePointSkipped")
            .ToList();

        Assert.IsGreaterThan(0, skippedEntries.Count);
        Assert.AreEqual("Scanner", skippedEntries[0].GetProperty("category").GetString());
        Assert.AreEqual("Skipped", skippedEntries[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Junction_PointingOutsideManagedRoot_DoesNotLeakExternalFiles()
    {
        using var temp = TempDirectory.Create();
        var logsDir = temp.CreateDirectory("logs");
        var logger = new DiagnosticLogger(logsDir);

        var rootPath = temp.CreateDirectory("root");
        var insideFile = Path.Combine(rootPath, "inside.txt");
        await File.WriteAllTextAsync(insideFile, "Inside Root Content");

        var outsidePath = temp.CreateDirectory("outside_sensitive");
        var outsideFile = Path.Combine(outsidePath, "secret_external.txt");
        await File.WriteAllTextAsync(outsideFile, "Super Secret Outside Content");

        // Create junction inside root pointing outside
        var junctionPath = Path.Combine(rootPath, "link_to_outside");
        var created = CreateJunction(junctionPath, outsidePath);
        Assert.IsTrue(created, "Failed to create directory junction using mklink /J.");
        temp.Junctions.Add(junctionPath);

        var dbPath = Path.Combine(temp.Path, "index.db");
        var scanner = new ManagedRootScanner(dbPath, logger: logger);
        var root = scanner.AddRoot(rootPath);

        var scanResult = await scanner.ScanAsync(root.Id);

        Assert.IsFalse(scanResult.Canceled);
        Assert.AreEqual(1, scanResult.CommittedFiles);
        Assert.AreEqual(1, scanResult.SkippedReparsePoints);

        var queryService = new FileQueryService(dbPath);
        var files = await queryService.QueryAsync(new());

        Assert.HasCount(1, files);
        Assert.AreEqual(insideFile, files[0].Path);

        // Confirm external sensitive file was NEVER indexed
        var secretSearch = await queryService.QueryAsync(new(Search: "secret_external.txt"));
        Assert.HasCount(0, secretSearch);
    }

    [TestMethod]
    public void Junction_Cleanup_DeletesJunctionWithoutImpactingTarget()
    {
        using var temp = TempDirectory.Create();
        var targetDir = temp.CreateDirectory("target_dir");
        var targetFile = Path.Combine(targetDir, "important_data.txt");
        File.WriteAllText(targetFile, "Persistent data");

        var junctionPath = Path.Combine(temp.Path, "test_junction");
        var created = CreateJunction(junctionPath, targetDir);
        Assert.IsTrue(created);

        Assert.IsTrue(Directory.Exists(junctionPath));
        Assert.IsTrue(File.Exists(Path.Combine(junctionPath, "important_data.txt")));

        // Safely delete junction itself using Directory.Delete (non-recursive)
        Directory.Delete(junctionPath);

        Assert.IsFalse(Directory.Exists(junctionPath), "Junction mount point should be deleted.");
        Assert.IsTrue(Directory.Exists(targetDir), "Target directory must remain intact.");
        Assert.IsTrue(File.Exists(targetFile), "Target file inside target directory must remain intact.");
        Assert.AreEqual("Persistent data", File.ReadAllText(targetFile));
    }

    private static bool CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        process?.WaitForExit();
        return process?.ExitCode == 0;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }
        public List<string> Junctions { get; } = [];

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GuraFile.JunctionTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new(path);
        }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            foreach (var junction in Junctions)
            {
                try
                {
                    if (Directory.Exists(junction))
                    {
                        Directory.Delete(junction);
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
