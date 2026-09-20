using System.IO.Enumeration;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.QuickMove;

/// <summary>빠른 이동 패널에 보여 줄 파일 / 폴더 한 줄.</summary>
public sealed class FsEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public bool IsReparsePoint { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>파일 크기. 폴더는 계산하지 않으므로 -1(탐색이 느려지지 않게 한다).</summary>
    public long Size { get; init; } = -1;

    public DateTime Modified { get; init; }
    public uint Attributes { get; init; }

    /// <summary>기존 보호 등급 정책(ProtectionEvaluator)을 그대로 적용한 결과. P0 는 이동할 수 없다.</summary>
    public ProtectionLevel Level { get; init; } = ProtectionLevel.P3;
    public string ProtectionReason { get; init; } = string.Empty;

    public bool IsProtected => Level == ProtectionLevel.P0;

    /// <summary>이름 옆에 붙는 작은 표시. P0 이동 금지 / P1 강한 경고.</summary>
    public string Glyph => Level switch
    {
        ProtectionLevel.P0 => "🔒",
        ProtectionLevel.P1 => "⚠",
        _ => string.Empty,
    };

    public string ToolTipText => Level >= ProtectionLevel.P1 && ProtectionReason.Length > 0
        ? $"{FullPath}\n{ProtectionText.Badge(Level)} — {ProtectionReason}"
        : FullPath;

    public string SizeText => IsDirectory ? string.Empty : SizeFormatter.Format(Size);
    public string ModifiedText => Modified == default ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm");

    public string TypeText
    {
        get
        {
            if (IsDirectory) return IsReparsePoint ? "링크" : "폴더";
            string ext = Path.GetExtension(Name);
            return ext.Length > 1 ? ext[1..].ToUpperInvariant() + " 파일" : "파일";
        }
    }

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name).ToLowerInvariant();
}

public enum BrowseStatus { Ok, NotFound, AccessDenied, Error }

public sealed class BrowseResult
{
    public BrowseStatus Status { get; init; }
    public IReadOnlyList<FsEntry> Entries { get; init; } = Array.Empty<FsEntry>();
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 폴더 하나의 내용을 실제 파일 시스템에서 읽는다. 스캔 결과(NodeStore)와 무관하다.
/// 폴더 크기는 계산하지 않는다 — 하위를 전부 훑어야 해서 큰 폴더에서 탐색이 멈춘 것처럼 보이기 때문이다.
/// </summary>
public static class DirectoryBrowser
{
    public static BrowseResult List(string path, bool showHidden, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            // 숨김/시스템 항목은 옵션이 켜져 있을 때만 보인다. (보호 정책은 별개로 항상 적용된다)
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            BufferSize = 64 * 1024,
        };

        try
        {
            if (!Directory.Exists(path))
                return new BrowseResult { Status = BrowseStatus.NotFound, Message = "폴더를 찾을 수 없습니다." };

            var list = new List<FsEntry>();
            var enumerable = new FileSystemEnumerable<FsEntry>(path, ToEntry, options);
            foreach (var e in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(e);
            }
            return new BrowseResult { Status = BrowseStatus.Ok, Entries = list };
        }
        catch (UnauthorizedAccessException)
        {
            return new BrowseResult { Status = BrowseStatus.AccessDenied, Message = "이 폴더에 접근할 권한이 없습니다." };
        }
        catch (DirectoryNotFoundException)
        {
            return new BrowseResult { Status = BrowseStatus.NotFound, Message = "폴더를 찾을 수 없습니다." };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return new BrowseResult { Status = BrowseStatus.Error, Message = ex.Message };
        }
    }

    /// <summary>
    /// 경로 하나를 목록의 한 줄(<see cref="FsEntry"/>)로 만든다. 다른 탭(폴더 / Treemap / 큰 파일)에서 고른 항목을
    /// 폴더를 열지 않고 바로 이동 대기열에 넣을 때 쓴다. 없는 경로면 null.
    /// </summary>
    public static FsEntry? Describe(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            var info = isDir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            string full = info.FullName;
            var verdict = ProtectionEvaluator.Evaluate(full, isDir, (uint)attrs, CategoryFlags.None);

            return new FsEntry
            {
                Name = info.Name,
                FullPath = full,
                IsDirectory = isDir,
                IsReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0,
                IsHidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                Size = isDir ? -1 : ((FileInfo)info).Length,
                Modified = info.LastWriteTime,
                Attributes = (uint)attrs,
                Level = verdict.Level,
                ProtectionReason = verdict.Reason,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static FsEntry ToEntry(ref FileSystemEntry e)
    {
        bool isDir = e.IsDirectory;
        var attrs = e.Attributes;
        string full = e.ToFullPath();
        var verdict = ProtectionEvaluator.Evaluate(full, isDir, (uint)attrs, CategoryFlags.None);

        return new FsEntry
        {
            Name = e.FileName.ToString(),
            FullPath = full,
            IsDirectory = isDir,
            IsReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0,
            IsHidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0,
            Size = isDir ? -1 : e.Length,
            Modified = e.LastWriteTimeUtc.LocalDateTime,
            Attributes = (uint)attrs,
            Level = verdict.Level,
            ProtectionReason = verdict.Reason,
        };
    }
}

/// <summary>폴더 하나의 총 크기 / 파일 수. 심볼릭 링크와 Junction 은 따라가지 않는다(대상이 다른 곳일 수 있다).</summary>
public static class TreeMeasure
{
    public readonly record struct Result(long Bytes, long Files, long Directories);

    public static Result Measure(string path, CancellationToken ct = default)
    {
        if (PathUtil.IsDirectory(path) != true)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? new Result(fi.Length, 1, 0) : default;
            }
            catch (Exception) { return default; }
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            BufferSize = 64 * 1024,
        };

        var counter = new Counter(path, options, ct);
        while (counter.MoveNext()) { }
        return new Result(counter.Bytes, counter.Files, counter.Directories);
    }

    private sealed class Counter : FileSystemEnumerator<int>
    {
        private readonly CancellationToken _ct;
        public long Bytes, Files, Directories;

        public Counter(string directory, EnumerationOptions options, CancellationToken ct)
            : base(directory, options) => _ct = ct;

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            _ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) Directories++;
            else { Files++; Bytes += entry.Length; }
            return false;   // 값을 모으지 않고 합계만 센다
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
            => (entry.Attributes & FileAttributes.ReparsePoint) == 0;

        protected override int TransformEntry(ref FileSystemEntry entry) => 0;
    }
}
