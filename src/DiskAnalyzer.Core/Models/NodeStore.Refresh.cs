using DiskAnalyzer.Core.Scanning;

namespace DiskAnalyzer.Core.Models;

public readonly record struct SnapFile(int Id, string Name, long Size);

public readonly record struct SnapDir(int Id, string Name);

/// <summary>폴더 하나의 직속 항목을 저장소에서 떠 낸 사본. 백그라운드에서 실제 파일 시스템과 비교하는 데 쓴다(저장소를 동시에 읽지 않게).</summary>
public sealed class FolderSnapshot
{
    public required int DirId { get; init; }
    public required string Path { get; init; }
    public List<SnapFile> Files { get; } = new();
    public List<SnapDir> Dirs { get; } = new();
}

public enum PathKind
{
    /// <summary>스캔 범위(루트) 밖.</summary>
    OutOfScope,
    /// <summary>범위 안이지만 스캔 결과에 없다(스캔 이후 생겼거나 처음부터 없다).</summary>
    NotFound,
    Directory,
    File,
}

/// <param name="Id">Directory 면 폴더 id, File 이면 파일 인덱스.</param>
/// <param name="NearestDirId">찾은 곳 또는 가장 깊이 찾을 수 있었던 폴더. File 이면 그 파일이 든 폴더.</param>
public readonly record struct PathResolution(PathKind Kind, int Id, int NearestDirId);

public readonly record struct RefreshResult(
    int AddedFiles, int AddedDirectories, int RemovedFiles, int RemovedDirectories, int ResizedFiles, long NetBytes)
{
    public bool Any => AddedFiles + AddedDirectories + RemovedFiles + RemovedDirectories + ResizedFiles > 0;
}

// 스캔 밖에서 일어난 변화를 폴더 단위로 저장소에 반영한다(전체 재스캔 없이). 쓰기 규칙은 삭제 반영(RemoveNodes)과 같다 —
// 스캔이 돌고 있지 않은 동안 UI 스레드 하나만 쓴다.
public sealed partial class NodeStore
{
    // ------------------------------------------------------------------ 경로 찾기

    /// <summary>
    /// 전체 경로를 이 저장소 안에서 <b>정확히</b> 찾는다(대소문자 무시). <see cref="FindDirectory"/> 는 없으면 가까운 곳을 조용히 돌려주지만,
    /// 여기서는 "있다 / 없다 / 범위 밖" 을 구분해서 알려 준다 — 사용자가 직접 입력한 경로를 다룰 때 필요하다.
    /// </summary>
    public PathResolution ResolvePath(string fullPath)
    {
        string root = NormalizeRoot(_pool.GetString(_dirName[RootId])).TrimEnd('\\');
        string path = (fullPath ?? string.Empty).Trim().TrimEnd('\\');

        if (path.Length == 0) return new PathResolution(PathKind.OutOfScope, -1, RootId);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || (path.Length > root.Length && path[root.Length] != '\\'))
            return new PathResolution(PathKind.OutOfScope, -1, RootId);
        if (path.Length == root.Length) return new PathResolution(PathKind.Directory, RootId, RootId);

        var parts = path[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int cur = RootId;
        for (int k = 0; k < parts.Length; k++)
        {
            int child = FindChildDirectory(cur, parts[k]);
            if (child >= 0) { cur = child; continue; }

            // 마지막 조각은 파일일 수도 있다.
            if (k == parts.Length - 1)
            {
                int file = FindChildFile(cur, parts[k]);
                if (file >= 0) return new PathResolution(PathKind.File, file, cur);
            }
            return new PathResolution(PathKind.NotFound, -1, cur);
        }
        return new PathResolution(PathKind.Directory, cur, cur);
    }

    private int FindChildDirectory(int parent, string name)
    {
        for (int i = parent + 1; i < _dirCount; i++)
        {
            if (_dirParent[i] != parent || _dirDeleted[i]) continue;
            if (_pool.Get(_dirName[i]).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private int FindChildFile(int parent, string name)
    {
        foreach (int f in EnumerateFiles(parent))
            if (_fileParent[f] == parent && _pool.Get(_fileName[f]).Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        return -1;
    }

    /// <summary>폴더의 직속 하위 폴더 이름들(입력 중 자동 완성용). 삭제된 폴더는 뺀다.</summary>
    public List<string> ChildDirectoryNames(int dirId)
    {
        var names = new List<string>();
        for (int i = dirId + 1; i < _dirCount; i++)
            if (_dirParent[i] == dirId && !_dirDeleted[i]) names.Add(_pool.GetString(_dirName[i]));
        return names;
    }

    /// <summary>삭제 표시되지 않은 가장 가까운 상위(자기 자신 포함) 폴더. 루트까지 없으면 루트.</summary>
    public int NearestLiveDirectory(int dirId)
    {
        int cur = dirId;
        while (cur > RootId && (uint)cur < (uint)_dirCount && _dirDeleted[cur]) cur = _dirParent[cur];
        return (uint)cur < (uint)_dirCount && cur >= 0 ? cur : RootId;
    }

    // ------------------------------------------------------------------ 폴더 사본 / 쓰기 도구

    public FolderSnapshot SnapshotChildren(int dirId)
    {
        var snap = new FolderSnapshot { DirId = dirId, Path = GetDirectoryPath(dirId) };
        if ((uint)dirId >= (uint)_dirCount) return snap;

        for (int i = dirId + 1; i < _dirCount; i++)
            if (_dirParent[i] == dirId && !_dirDeleted[i]) snap.Dirs.Add(new SnapDir(i, _pool.GetString(_dirName[i])));

        foreach (int f in EnumerateFiles(dirId))
            if (_fileParent[f] == dirId) snap.Files.Add(new SnapFile(f, _pool.GetString(_fileName[f]), _fileSize[f]));

        return snap;
    }

    /// <summary>새 폴더 노드를 끝에 붙인다. 새 id 는 언제나 기존 모든 id 보다 크므로 "자식 id &gt; 부모 id" 가 유지된다.</summary>
    public int AddDirectory(int parentId, string name, long fileTime, int attributes)
    {
        int id = _dirCount;
        SetDirectory(id, parentId, name, fileTime, attributes);
        return id;
    }

    /// <summary>부분 스캔 결과를 <paramref name="attachDirId"/> 아래에 붙인다(부모가 먼저 오는 순서 그대로).</summary>
    public void AttachTree(int attachDirId, ScannedTree tree)
    {
        var map = new int[tree.Nodes.Count];
        for (int i = 0; i < tree.Nodes.Count; i++)
        {
            var n = tree.Nodes[i];
            int parent = n.Parent < 0 ? attachDirId : map[n.Parent];
            if (n.IsDirectory) map[i] = AddDirectory(parent, n.Name, n.Modified, (int)n.Attributes);
            else AddFile(parent, n.Name, n.Size, n.Modified, n.Created, n.Accessed, (int)n.Attributes);
        }
    }

    /// <summary>쓰기를 마친 뒤 파생 값(폴더 크기 / 폴더별 파일 인덱스 / 큰 파일 목록)을 다시 맞춘다.</summary>
    public void EndMutation()
    {
        InvalidateFileIndex();
        Rollup();
        RebuildTopFiles();
    }

    /// <summary>
    /// 폴더 새로고침 계획을 저장소에 반영한다. 계획을 세운 뒤 다른 곳에서 이미 지워진 항목은 건너뛰고,
    /// 같은 이름이 이미 있는 항목은 다시 넣지 않는다(같은 계획을 두 번 적용해도 중복이 생기지 않는다).
    /// </summary>
    public RefreshResult ApplyRefresh(FolderRefreshPlan plan)
    {
        int dir = plan.DirId;
        if (IsDirectoryDeleted(dir)) return default;

        long before = TotalSize;

        // 폴더 자신이 사라졌다.
        if (plan.DirectoryGone)
        {
            if (dir == RootId) return default;   // 루트는 지우지 않는다 — 스캔 자체가 무의미해진 경우다
            var gone = RemoveNodes(new[] { (RowKind.Directory, dir) });
            return new RefreshResult(0, 0, gone.RemovedFiles, gone.RemovedDirectories, 0, TotalSize - before);
        }

        var removeList = new List<(RowKind, int)>();
        foreach (int id in plan.RemoveFiles)
            if (!IsFileDeleted(id) && _fileParent[id] == dir) removeList.Add((RowKind.File, id));
        foreach (int id in plan.RemoveDirs)
            if (!IsDirectoryDeleted(id) && _dirParent[id] == dir) removeList.Add((RowKind.Directory, id));
        var removed = removeList.Count > 0 ? RemoveNodes(removeList) : default;

        int resized = 0;
        foreach (var r in plan.Resized)
        {
            if (IsFileDeleted(r.Id) || _fileParent[r.Id] != dir) continue;
            long delta = r.Info.Size - _fileSize[r.Id];
            _dirOwnSize[dir] += delta;
            Extensions.Adjust(_fileExt[r.Id], delta);
            _fileSize[r.Id] = r.Info.Size;
            if (r.Info.Modified > 0) _fileTime[r.Id] = r.Info.Modified;
            if (r.Info.Accessed > 0) _fileAccessedDay[r.Id] = ToDay(r.Info.Accessed);
            resized++;
        }

        // 이미 있는 이름은 넣지 않는다.
        var haveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int f in EnumerateFiles(dir)) if (_fileParent[f] == dir) haveFiles.Add(_pool.GetString(_fileName[f]));
        var haveDirs = new HashSet<string>(ChildDirectoryNames(dir), StringComparer.OrdinalIgnoreCase);

        int addedFiles = 0, addedDirs = 0;
        foreach (var f in plan.AddFiles)
        {
            if (!haveFiles.Add(f.Name)) continue;
            AddFile(dir, f.Name, f.Size, f.Modified, f.Created, f.Accessed, (int)f.Attributes);
            addedFiles++;
        }
        foreach (var (info, tree) in plan.AddDirs)
        {
            if (!haveDirs.Add(info.Name)) continue;
            int id = AddDirectory(dir, info.Name, info.Modified, (int)info.Attributes);
            AttachTree(id, tree);
            addedDirs += 1 + tree.Directories;
            addedFiles += tree.Files;
        }

        EndMutation();
        return new RefreshResult(addedFiles, addedDirs, removed.RemovedFiles, removed.RemovedDirectories, resized, TotalSize - before);
    }
}
