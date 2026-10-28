using System.Collections.Concurrent;
using MilesFontExtractor.Models;

namespace MilesFontExtractor.Core;

public sealed class ScanState
{
    private int _folders, _files, _invalid, _cloud;
    public int Folders => _folders;
    public int FontFiles => _files;
    public int Invalid => _invalid;
    public int CloudSkipped => _cloud;
    public volatile string Current = "";

    internal void AddFolder() => Interlocked.Increment(ref _folders);
    internal void AddFont() => Interlocked.Increment(ref _files);
    internal void AddInvalid() => Interlocked.Increment(ref _invalid);
    internal void AddCloud() => Interlocked.Increment(ref _cloud);
}

public static class Scanner
{
    // Folders that can only contain Windows/tooling internals or deleted files. Skipping them keeps the scan fast.
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Windows.old", "WinSxS",
        "node_modules", ".git", "Config.Msi", "$SysReset", "Recovery"
    };

    // Dev aid for screenshots: MILES_SCAN_SLOWMO=<ms> pauses briefly per folder so the scan screen can be captured.
    private static readonly int SlowMoMs = int.TryParse(Environment.GetEnvironmentVariable("MILES_SCAN_SLOWMO"), out var ms) ? ms : 0;

    private const FileAttributes CloudOnly = (FileAttributes)0x00040000 | (FileAttributes)0x00400000 | FileAttributes.Offline;

    public static Task<List<FontItem>> ScanAsync(IEnumerable<string> roots, Classifier classifier,
        string? excludeDir, ScanState state, CancellationToken ct) =>
        Task.Run(() =>
        {
            var results = new ConcurrentBag<FontItem>();
            var units = new List<string>();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false
            };
            string? exclude = excludeDir?.TrimEnd('\\');

            void Process(FileInfo f)
            {
                if (!FontReader.Extensions.Contains(f.Extension)) return;
                if ((f.Attributes & CloudOnly) != 0) { state.AddCloud(); return; }
                if (f.Length is < 32 or > 250_000_000) { state.AddInvalid(); return; }

                var info = FontReader.TryRead(f.FullName);
                if (info == null) { state.AddInvalid(); return; }

                var (cat, reason) = classifier.Classify(f.FullName, info);
                results.Add(new FontItem
                {
                    Path = f.FullName,
                    Family = info.Family,
                    FullName = info.FullName,
                    Format = info.Format,
                    Vendor = info.Vendor,
                    Copyright = info.Copyright,
                    Size = f.Length,
                    Category = cat,
                    Reason = reason,
                    IsSelected = false
                });
                state.AddFont();
            }

            bool Skip(DirectoryInfo d) =>
                (d.Attributes & FileAttributes.ReparsePoint) != 0 ||
                SkipNames.Contains(d.Name) ||
                (exclude != null && d.FullName.TrimEnd('\\').Equals(exclude, StringComparison.OrdinalIgnoreCase));

            // Visits one folder: handles its files, hands subfolders to the callback.
            void Visit(string dir, Action<string> onSubDir)
            {
                ct.ThrowIfCancellationRequested();
                state.AddFolder();
                if (SlowMoMs > 0) Thread.Sleep(SlowMoMs);
                state.Current = dir;
                try
                {
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                    {
                        if (entry is FileInfo f) Process(f);
                        else if (entry is DirectoryInfo d && !Skip(d)) onSubDir(d.FullName);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* access denied / vanished: skip folder */ }
            }

            // Top two levels sequentially (so work can be split evenly), the rest in parallel.
            void Collect(string dir, int depth) =>
                Visit(dir, sub => { if (depth < 1) Collect(sub, depth + 1); else units.Add(sub); });

            foreach (var root in roots) Collect(root, 0);

            Parallel.ForEach(units,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = ct },
                unit =>
                {
                    var stack = new Stack<string>();
                    stack.Push(unit);
                    while (stack.Count > 0) Visit(stack.Pop(), stack.Push);
                });

            return results
                .OrderBy(r => r.Family, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
}
