using ProjectSync.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static string Normalize(string path) =>
    SyncPath.Normalize(path, "sync", @"D:\项目 空格", "/home/user/项目 空格", "Ubuntu");

foreach (string path in new[] { @"文档\a.txt", "\"d:/项目 空格/文档/a.txt\"",
    "/home/user/项目 空格/文档/a.txt", @"\\wsl.localhost\Ubuntu\home\user\项目 空格\文档\a.txt" })
    Check(Normalize(path) == "文档/a.txt", $"Unexpected normalization: {path}");

foreach (string path in new[] { "", ".", "../a", "dir/.GIT/config", "src//a", "src/*.txt",
    @"\\wsl$\Other\home\user\项目 空格\文档", @"D:\项目 空格2\a", "/home/user/outside/a" })
    Reject<ArgumentException>(() => Normalize(path));

string root = Directory.CreateTempSubdirectory("projectsync-tests-").FullName;
try
{
    string source = Directory.CreateDirectory(Path.Combine(root, "来源 空格")).FullName;
    string target = Directory.CreateDirectory(Path.Combine(root, "目标 空格")).FullName;
    string input = Path.Combine(source, "文件.txt");
    File.WriteAllText(input, "original");
    string output = Path.Combine(target, Path.GetFileName(source), Path.GetFileName(input));

    var first = FileCopy.Preview(source, target, replace: false, skipDownloadMark: true);
    Check(first.Any(entry => entry.Action == CopyAction.Copy), "Preview should include the new file.");
    FileCopy.Apply(first, skipDownloadMark: true, _ => { });
    Check(File.ReadAllText(output) == "original", "Apply should copy file contents.");

    var repeat = FileCopy.Preview(source, target, replace: false, skipDownloadMark: true);
    Check(repeat.Any(entry => entry.Action == CopyAction.SkipExisting), "Repeated copy should skip existing files.");
    FileCopy.Apply(repeat, skipDownloadMark: true, _ => { });

    var staleSource = FileCopy.Preview(source, target, replace: true, skipDownloadMark: true);
    File.WriteAllText(input, "source changed");
    Reject<IOException>(() => FileCopy.Apply(staleSource, true, _ => { }));
    Check(File.ReadAllText(output) == "original", "A changed source must preserve the target.");

    var staleTarget = FileCopy.Preview(source, target, replace: true, skipDownloadMark: true);
    File.WriteAllText(output, "target changed");
    Reject<IOException>(() => FileCopy.Apply(staleTarget, true, _ => { }));
    Check(File.ReadAllText(output) == "target changed", "A changed target must be preserved.");

    Reject<IOException>(() => FileCopy.Preview(source, source, true, false));
    Reject<OperationCanceledException>(() => FileCopy.Preview(source, target, true, false, new CancellationToken(true)));
}
finally
{
    Directory.Delete(root, recursive: true);
}
Console.WriteLine("FileCopy and SyncPath checks passed.");
