using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Scanning;

public readonly record struct ResizedFile(int Id, FsFileInfo Info);

/// <summary>스캔 결과와 지금의 파일 시스템을 폴더 하나의 직속 항목 수준에서 비교한 결과.</summary>
public sealed class FolderDiff
{
    public required int DirId { get; init; }
    public required string Path { get; init; }

    /// <summary>폴더를 읽을 수 있었나(권한 없음 / 오류면 비교하지 못했으므로 변경 없음으로 본다).</summary>
    public bool Readable { get; init; } = true;

    /// <summary>폴더 자체가 없어졌다.</summary>
    public bool DirectoryGone { get; init; }

    public List<FsFileInfo> AddedFiles { get; } = new();
    public List<SnapFile> RemovedFiles { get; } = new();
    public List<ResizedFile> Resized { get; } = new();
    public List<FsDirInfo> AddedDirs { get; } = new();
    public List<SnapDir> RemovedDirs { get; } = new();

    public int Count => (DirectoryGone ? 1 : 0) + AddedFiles.Count + RemovedFiles.Count + Resized.Count + AddedDirs.Count + RemovedDirs.Count;
    public bool Any => Count > 0;

    /// <summary>"파일 +2 −1 · 크기 변경 2 · 폴더 +1" 같은 한 줄 요약.</summary>
    public string Summary
    {
        get
        {
            if (DirectoryGone) return "폴더가 없어졌습니다";

            var parts = new List<string>();
            if (AddedFiles.Count + RemovedFiles.Count > 0)
            {
                var f = new List<string>();
                if (AddedFiles.Count > 0) f.Add($"+{AddedFiles.Count:N0}");
                if (RemovedFiles.Count > 0) f.Add($"−{RemovedFiles.Count:N0}");
                parts.Add("파일 " + string.Join(" ", f));
            }
            if (Resized.Count > 0) parts.Add($"크기 변경 {Resized.Count:N0}");
            if (AddedDirs.Count + RemovedDirs.Count > 0)
            {
                var d = new List<string>();
                if (AddedDirs.Count > 0) d.Add($"+{AddedDirs.Count:N0}");
                if (RemovedDirs.Count > 0) d.Add($"−{RemovedDirs.Count:N0}");
                parts.Add("폴더 " + string.Join(" ", d));
            }
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>저장소에 넣을 준비가 끝난 새로고침 내용. 백그라운드에서 만들고 UI 스레드에서 <see cref="NodeStore.ApplyRefresh"/> 로 적용한다.</summary>
public sealed class FolderRefreshPlan
{
    public required int DirId { get; init; }
    public required FolderDiff Diff { get; init; }
    public bool Deep { get; init; }
    public bool DirectoryGone { get; init; }

    public List<int> RemoveFiles { get; } = new();
    public List<int> RemoveDirs { get; } = new();
    public List<ResizedFile> Resized { get; } = new();
    public List<FsFileInfo> AddFiles { get; } = new();
    public List<(FsDirInfo Info, ScannedTree Contents)> AddDirs { get; } = new();
}

/// <summary>
/// 폴더 새로고침 — "열 때 변경 감지"와 "F5 로 그 폴더만 새로고침"의 엔진.
///  · <see cref="Detect"/> : 직속 항목만 한 번 읽어서 달라진 것을 센다(빠르다).
///  · <see cref="Build"/>  : 실제로 반영할 계획을 만든다. 새로 생긴 하위 폴더는 안쪽까지 읽는다.
///                           <c>deep</c> 이면 기존 항목을 모두 버리고 이 폴더 아래 전체를 다시 읽는다.
/// 비교 기준은 파일 크기(FileVerifier 와 같다). 수정 시각만 달라진 것은 변경으로 세지 않는다 —
/// 스캔 방식(MFT / 열거)에 따라 시각이 미세하게 달라 매번 "변경 감지" 로 뜨는 것을 막는다.
/// </summary>
public static class FolderRefresher
{
    public static FolderDiff Detect(FolderSnapshot snapshot, ScanOptions options)
        => Diff(snapshot, FolderScanner.List(snapshot.Path, options));

    public static FolderDiff Diff(FolderSnapshot snapshot, FolderListing listing)
    {
        if (listing.Status == ListingStatus.NotFound)
            return new FolderDiff { DirId = snapshot.DirId, Path = snapshot.Path, DirectoryGone = true };
        if (!listing.Ok)
            return new FolderDiff { DirId = snapshot.DirId, Path = snapshot.Path, Readable = false };

        var diff = new FolderDiff { DirId = snapshot.DirId, Path = snapshot.Path };

        var files = new Dictionary<string, SnapFile>(snapshot.Files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapshot.Files) files[f.Name] = f;
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in listing.Files)
        {
            seenFiles.Add(f.Name);
            if (!files.TryGetValue(f.Name, out var old)) diff.AddedFiles.Add(f);
            else if (old.Size != f.Size) diff.Resized.Add(new ResizedFile(old.Id, f));
        }
        foreach (var f in snapshot.Files)
            if (!seenFiles.Contains(f.Name)) diff.RemovedFiles.Add(f);

        var dirs = new Dictionary<string, SnapDir>(snapshot.Dirs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var d in snapshot.Dirs) dirs[d.Name] = d;
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in listing.Dirs)
        {
            seenDirs.Add(d.Name);
            if (!dirs.ContainsKey(d.Name)) diff.AddedDirs.Add(d);
        }
        foreach (var d in snapshot.Dirs)
            if (!seenDirs.Contains(d.Name)) diff.RemovedDirs.Add(d);

        return diff;
    }

    public static FolderRefreshPlan Build(FolderSnapshot snapshot, ScanOptions options, bool deep, CancellationToken ct = default)
    {
        var listing = FolderScanner.List(snapshot.Path, options);
        var diff = Diff(snapshot, listing);

        if (diff.DirectoryGone)
            return new FolderRefreshPlan { DirId = snapshot.DirId, Diff = diff, Deep = deep, DirectoryGone = true };

        var plan = new FolderRefreshPlan { DirId = snapshot.DirId, Diff = diff, Deep = deep };
        if (!listing.Ok) return plan;   // 읽을 수 없는 폴더는 손대지 않는다

        if (deep)
        {
            // 이 폴더 아래를 통째로 다시 읽는다: 기존 직속 항목을 모두 버리고 지금 있는 것을 전부 새로 넣는다.
            foreach (var f in snapshot.Files) plan.RemoveFiles.Add(f.Id);
            foreach (var d in snapshot.Dirs) plan.RemoveDirs.Add(d.Id);
            plan.AddFiles.AddRange(listing.Files);
            foreach (var d in listing.Dirs) plan.AddDirs.Add((d, ScanFolder(snapshot.Path, d, options, ct)));
            return plan;
        }

        foreach (var f in diff.RemovedFiles) plan.RemoveFiles.Add(f.Id);
        foreach (var d in diff.RemovedDirs) plan.RemoveDirs.Add(d.Id);
        plan.Resized.AddRange(diff.Resized);
        plan.AddFiles.AddRange(diff.AddedFiles);
        foreach (var d in diff.AddedDirs) plan.AddDirs.Add((d, ScanFolder(snapshot.Path, d, options, ct)));
        return plan;
    }

    private static ScannedTree ScanFolder(string parentPath, FsDirInfo dir, ScanOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (dir.IsReparse && !options.FollowReparsePoints) return new ScannedTree();   // 링크는 따라가지 않는다
        return FolderScanner.ScanContents(FolderScanner.Combine(parentPath, dir.Name), options, ct);
    }
}
