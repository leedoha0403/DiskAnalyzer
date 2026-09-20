using System.Runtime.CompilerServices;

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 32/33. 인덱스 기반 트리 저장소 (Structure of Arrays).
///
/// [설계 근거]
/// - 파일 1개 = 클래스 인스턴스 1개 구조는 500만 파일에서 객체 헤더만 80MB 를 넘고 GC 를 마비시킨다.
///   여기서는 파일/폴더를 "배열의 인덱스"로 표현하고, 속성별로 별도의 배열(SoA)에 담는다.
///   크기 정렬처럼 한 필드만 훑는 연산에서 캐시 라인을 100% 활용할 수 있다.
/// - Full Path 는 어디에도 저장하지 않는다. 각 노드는 parentId 와 이름 핸들만 갖고,
///   경로는 필요할 때 parent chain 을 따라 생성한다(33).
/// - 문자열은 StringPool 에 연속 저장한다.
///
/// [불변식] childDirId > parentDirId
///   Compatibility 스캔은 BFS 라 부모가 항상 먼저 id 를 받고,
///   Fast(MFT) 스캔도 MFT 인덱스를 그대로 쓰지 않고 루트부터 BFS 로 재번호를 매긴다.
///   덕분에 폴더 크기 롤업을 "id 역순 1회 순회"로 끝낼 수 있다(25).
///
/// [스레드 규칙]
///   쓰기는 Aggregator 단일 스레드만 수행한다. 따라서 이 클래스에는 Lock 이 하나도 없다(23).
///   스캔 중 UI 조회는 Aggregator 스레드에서 스냅샷을 만들어 전달한다.
/// </summary>
public sealed class NodeStore
{
    public const int RootId = 0;
    private const int InitialDirCapacity = 1 << 14;
    private const int InitialFileCapacity = 1 << 16;

    private readonly StringPool _pool = new();

    // ---- 디렉터리 SoA ----
    private int[] _dirParent;
    private long[] _dirName;
    private long[] _dirOwnSize;      // 직속 파일 크기 합
    private int[] _dirOwnFiles;      // 직속 파일 개수
    private long[] _dirTotalSize;    // 롤업 결과(하위 전체)
    private long[] _dirTotalFiles;
    private int[] _dirTotalDirs;
    private long[] _dirTime;         // FILETIME(UTC)
    private int[] _dirAttr;
    private uint[] _dirCat;
    private bool[] _dirDeleted;      // 14. 삭제 반영용 tombstone
    private int _dirCount;
    private int _deletedDirCount;
    private int _deletedFileCount;

    // ---- 파일 SoA ----
    private int[] _fileParent;
    private long[] _fileName;
    private long[] _fileSize;
    private long[] _fileTime;          // 최종 수정 FILETIME (분 단위까지 표시해야 해서 원본 유지)
    private int[] _fileCreatedDay;     // 53/56. 생성일 / 접근일은 "일" 단위면 충분하다.
    private int[] _fileAccessedDay;    //        long 2개 대신 int 2개 -> 파일당 16B 절약
    private int[] _fileExt;
    private int[] _fileAttr;
    private int _fileCount;

    // 34. Lazy: 폴더별 파일 CSR 인덱스는 스캔이 끝나고 실제로 필요할 때만 만든다.
    private int[]? _fileIndexStart;
    private int[]? _fileIndexEntries;

    public NodeStore(string rootPath, int topK = 1000)
    {
        RootPath = rootPath;
        Extensions = new ExtensionTable();
        TopFiles = new TopKHeap(topK);

        _dirParent = new int[InitialDirCapacity];
        _dirName = new long[InitialDirCapacity];
        _dirOwnSize = new long[InitialDirCapacity];
        _dirOwnFiles = new int[InitialDirCapacity];
        _dirTotalSize = new long[InitialDirCapacity];
        _dirTotalFiles = new long[InitialDirCapacity];
        _dirTotalDirs = new int[InitialDirCapacity];
        _dirTime = new long[InitialDirCapacity];
        _dirAttr = new int[InitialDirCapacity];
        _dirCat = new uint[InitialDirCapacity];
        _dirDeleted = new bool[InitialDirCapacity];
        Array.Fill(_dirParent, -1);

        _fileParent = new int[InitialFileCapacity];
        _fileName = new long[InitialFileCapacity];
        _fileSize = new long[InitialFileCapacity];
        _fileTime = new long[InitialFileCapacity];
        _fileCreatedDay = new int[InitialFileCapacity];
        _fileAccessedDay = new int[InitialFileCapacity];
        _fileExt = new int[InitialFileCapacity];
        _fileAttr = new int[InitialFileCapacity];

        // 루트 노드 생성
        _dirName[RootId] = _pool.Add(rootPath.TrimEnd('\\'));
        _dirParent[RootId] = -1;
        _dirCount = 1;
    }

    public string RootPath { get; }
    public ExtensionTable Extensions { get; }
    public TopKHeap TopFiles { get; }

    /// <summary>배열 슬롯 수(삭제된 노드 포함). 내부 순회 상한으로 쓴다.</summary>
    public int DirectorySlots => _dirCount;
    public int FileSlots => _fileCount;

    /// <summary>화면에 보이는 실제 개수(삭제 반영).</summary>
    public int DirectoryCount => _dirCount - _deletedDirCount;
    public int FileCount => _fileCount - _deletedFileCount;
    public long TotalSize => _dirCount > 0 ? _dirTotalSize[RootId] : 0;
    public bool IsSealed { get; private set; }

    // ------------------------------------------------------------------ 쓰기

    /// <summary>
    /// 워커가 Interlocked 로 미리 할당한 id 에 실제 디렉터리 정보를 채운다.
    /// id 는 순서가 뒤바뀐 채 도착할 수 있으므로 용량만 보장하면 된다.
    /// </summary>
    public void SetDirectory(int id, int parentId, ReadOnlySpan<char> name, long fileTime, int attributes)
    {
        EnsureDirCapacity(id + 1);
        _dirParent[id] = parentId;
        _dirName[id] = _pool.Add(name);
        _dirTime[id] = fileTime;
        _dirAttr[id] = attributes;
        _dirCat[id] = (uint)Categorizer.ForDirectory(name, parentId == RootId);
        if (id >= _dirCount) _dirCount = id + 1;
    }

    public int AddFile(int parentId, ReadOnlySpan<char> name, long size, long fileTime, int attributes)
        => AddFile(parentId, name, size, fileTime, fileTime, fileTime, attributes);

    public int AddFile(int parentId, ReadOnlySpan<char> name, long size,
        long modifiedTime, long createdTime, long accessedTime, int attributes)
    {
        if (_fileCount >= _fileParent.Length) GrowFiles();

        int idx = _fileCount++;
        _fileParent[idx] = parentId;
        _fileName[idx] = _pool.Add(name);
        _fileSize[idx] = size;
        _fileTime[idx] = modifiedTime;
        _fileCreatedDay[idx] = ToDay(createdTime);
        _fileAccessedDay[idx] = ToDay(accessedTime);
        _fileAttr[idx] = attributes;
        _fileExt[idx] = Extensions.Add(GetExtensionSpan(name), size);

        // 24. 파일 시스템 1회 순회: 폴더 크기/확장자/Top-N 을 여기서 동시에 갱신한다.
        //
        // 주의: 병렬 열거에서는 "폴더 X 의 파일 배치"가 "폴더 X 자신의 배치"보다 먼저 도착할 수 있다.
        // 그래도 크기를 잃지 않도록 부모 슬롯 용량만 먼저 확보한다(이름/부모는 나중에 SetDirectory 가 채운다).
        if (parentId >= 0)
        {
            if (parentId >= _dirParent.Length) EnsureDirCapacity(parentId + 1);
            _dirOwnSize[parentId] += size;
            _dirOwnFiles[parentId]++;
        }
        TopFiles.Offer(size, idx);
        return idx;
    }

    /// <summary>
    /// 25. 폴더 크기 계산 - Post-order Aggregation 을 id 역순 단일 루프로 수행한다.
    /// 자식 id 가 항상 부모보다 크다는 불변식 덕분에 재귀도, 자식 리스트도 필요 없다.
    /// O(D) 이며 메모리 접근이 순차적이라 50만 폴더 기준 수 ms 안에 끝난다.
    /// </summary>
    public void Rollup()
    {
        int n = _dirCount;
        for (int i = 0; i < n; i++)
        {
            _dirTotalSize[i] = _dirOwnSize[i];
            _dirTotalFiles[i] = _dirOwnFiles[i];
            _dirTotalDirs[i] = 0;
        }

        for (int i = n - 1; i > 0; i--)
        {
            if (_dirDeleted[i]) continue;              // 14. 삭제된 서브트리는 상위에 더하지 않는다
            int p = _dirParent[i];
            if ((uint)p >= (uint)n) continue;          // 미완성/고아 노드는 건너뛴다
            _dirTotalSize[p] += _dirTotalSize[i];
            _dirTotalFiles[p] += _dirTotalFiles[i];
            _dirTotalDirs[p] += _dirTotalDirs[i] + 1;
        }
    }

    public void Seal()
    {
        Rollup();
        IsSealed = true;
    }

    // ------------------------------------------------------------------ 14. 삭제 / 갱신 반영

    public bool IsFileDeleted(int fileIndex)
        => (uint)fileIndex >= (uint)_fileCount || _fileParent[fileIndex] < 0;

    public bool IsDirectoryDeleted(int dirId)
        => (uint)dirId >= (uint)_dirCount || _dirDeleted[dirId];

    /// <summary>
    /// 14. 삭제된 노드를 데이터 모델에서 제거한다. <strong>전체 디스크를 다시 스캔하지 않는다.</strong>
    ///
    /// 파일은 parent 를 -1 로, 폴더는 tombstone 으로 표시하고 직속 부모의 누적값에서 크기를 뺀다.
    /// 상위 폴더까지의 반영은 마지막 Rollup 한 번(O(폴더 수))으로 자동 처리된다 -
    /// 삭제된 파일마다 부모 체인을 거슬러 올라가며 빼면 O(삭제 수 x 깊이)가 되기 때문.
    ///
    /// 폴더를 지우면 그 하위 파일도 모두 제거해야 하므로, 여러 건을 한 번에 받아
    /// <b>파일 배열을 단 한 번만 훑는다</b>(건별로 훑으면 O(삭제 수 x 파일 수)).
    /// </summary>
    public MutationSummary RemoveNodes(IReadOnlyList<(RowKind Kind, int Id)> nodes)
    {
        if (nodes.Count == 0) return default;

        // 1) 삭제 대상 폴더를 표시하고 하위로 전파한다(자식 id > 부모 id 불변식 덕분에 오름차순 1패스).
        bool[]? removedDir = null;
        var directFiles = new HashSet<int>();
        int removedDirCount = 0;

        foreach (var (kind, id) in nodes)
        {
            if (kind == RowKind.Directory)
            {
                if ((uint)id >= (uint)_dirCount || id == RootId || _dirDeleted[id]) continue;
                removedDir ??= new bool[_dirCount];
                removedDir[id] = true;
            }
            else if (!IsFileDeleted(id))
            {
                directFiles.Add(id);
            }
        }

        if (removedDir != null)
        {
            for (int i = 1; i < _dirCount; i++)
            {
                int p = _dirParent[i];
                if ((uint)p < (uint)_dirCount && removedDir[p]) removedDir[i] = true;
                if (!removedDir[i] || _dirDeleted[i]) continue;
                _dirDeleted[i] = true;
                removedDirCount++;
            }
        }

        // 2) 파일 배열 1패스: 직접 지정된 파일 + 삭제된 폴더 하위 파일을 제거한다.
        int removedFiles = 0;
        long removedBytes = 0;

        for (int f = 0; f < _fileCount; f++)
        {
            int parent = _fileParent[f];
            if (parent < 0) continue;

            bool remove = directFiles.Contains(f)
                          || (removedDir != null && (uint)parent < (uint)removedDir.Length && removedDir[parent]);
            if (!remove) continue;

            long size = _fileSize[f];
            _dirOwnSize[parent] -= size;
            _dirOwnFiles[parent]--;
            Extensions.Remove(_fileExt[f], size);

            _fileParent[f] = -1;          // tombstone
            removedFiles++;
            removedBytes += size;
        }

        // 3) 삭제된 폴더의 직속 누적값을 0으로 만들고, 부모의 폴더 수에서도 뺀다.
        if (removedDir != null)
        {
            for (int i = 1; i < _dirCount; i++)
            {
                if (!removedDir[i]) continue;
                _dirOwnSize[i] = 0;
                _dirOwnFiles[i] = 0;
            }
        }

        _deletedFileCount += removedFiles;
        _deletedDirCount += removedDirCount;

        InvalidateFileIndex();
        Rollup();
        RebuildTopFiles();

        return new MutationSummary(removedFiles, removedDirCount, removedBytes, 0, 0);
    }

    /// <summary>1/2. 새로고침 - 실제 파일 시스템과 대조한 결과를 반영한다.</summary>
    public MutationSummary ApplyVerification(IReadOnlyList<VerifyOutcome> outcomes)
    {
        var missing = new List<(RowKind, int)>();
        int resized = 0;
        long delta = 0;

        foreach (var o in outcomes)
        {
            switch (o.State)
            {
                case VerifyState.Missing:
                    missing.Add((o.Kind, o.Id));
                    break;

                case VerifyState.Changed when o.Kind == RowKind.File && !IsFileDeleted(o.Id):
                    int parent = _fileParent[o.Id];
                    long d = o.NewSize - _fileSize[o.Id];
                    if ((uint)parent < (uint)_dirCount) _dirOwnSize[parent] += d;
                    Extensions.Adjust(_fileExt[o.Id], d);
                    _fileSize[o.Id] = o.NewSize;
                    if (o.NewModified > 0) _fileTime[o.Id] = o.NewModified;
                    if (o.NewAccessed > 0) _fileAccessedDay[o.Id] = ToDay(o.NewAccessed);
                    resized++;
                    delta += d;
                    break;
            }
        }

        var removed = missing.Count > 0 ? RemoveNodes(missing) : default;

        if (resized > 0)
        {
            InvalidateFileIndex();
            Rollup();
            RebuildTopFiles();
        }

        return new MutationSummary(
            removed.RemovedFiles, removed.RemovedDirectories, removed.RemovedBytes, resized, delta);
    }

    /// <summary>
    /// 파일이 지워지거나 크기가 바뀌면 기존 Top-K 힙은 더 이상 정확하지 않다.
    /// 힙에서 특정 원소를 빼는 것보다 전체를 다시 쌓는 편이 단순하고, O(N log K) 라 실제로도 빠르다.
    /// </summary>
    public void RebuildTopFiles()
    {
        TopFiles.Clear();
        for (int f = 0; f < _fileCount; f++)
        {
            if (_fileParent[f] < 0) continue;
            TopFiles.Offer(_fileSize[f], f);
        }
    }

    private void InvalidateFileIndex()
    {
        _fileIndexStart = null;
        _fileIndexEntries = null;
    }

    // ------------------------------------------------------------------ 경로

    public string GetDirectoryPath(int dirId)
    {
        if ((uint)dirId >= (uint)_dirCount) return string.Empty;
        if (dirId == RootId) return NormalizeRoot(_pool.GetString(_dirName[RootId]));

        Span<int> stackBuf = stackalloc int[64];
        var chain = stackBuf;
        int depth = 0;
        int cur = dirId;
        int[]? rented = null;

        while (cur > 0 && depth < 4096)
        {
            if (depth == chain.Length)
            {
                var bigger = new int[chain.Length * 2];
                chain.CopyTo(bigger);
                rented = bigger;
                chain = rented;
            }
            chain[depth++] = cur;
            cur = _dirParent[cur];
            if (cur < 0) break;
        }

        var sb = new System.Text.StringBuilder(260);
        sb.Append(NormalizeRoot(_pool.GetString(_dirName[RootId])));
        for (int i = depth - 1; i >= 0; i--)
        {
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
            sb.Append(_pool.Get(_dirName[chain[i]]));
        }
        GC.KeepAlive(rented);
        return sb.ToString();
    }

    public string GetFilePath(int fileIndex)
    {
        if ((uint)fileIndex >= (uint)_fileCount) return string.Empty;
        string dir = GetDirectoryPath(_fileParent[fileIndex]);
        if (dir.Length > 0 && dir[^1] == '\\') return dir + _pool.GetString(_fileName[fileIndex]);
        return dir + "\\" + _pool.GetString(_fileName[fileIndex]);
    }

    public string GetDirectoryName(int dirId)
        => (uint)dirId < (uint)_dirCount ? _pool.GetString(_dirName[dirId]) : string.Empty;

    public string GetFileName(int fileIndex)
        => (uint)fileIndex < (uint)_fileCount ? _pool.GetString(_fileName[fileIndex]) : string.Empty;

    public long GetFileSize(int fileIndex)
        => (uint)fileIndex < (uint)_fileCount ? _fileSize[fileIndex] : 0;

    public long GetDirectorySize(int dirId)
        => (uint)dirId < (uint)_dirCount ? _dirTotalSize[dirId] : 0;

    public int GetParent(int dirId)
        => (uint)dirId < (uint)_dirCount ? _dirParent[dirId] : -1;

    /// <summary>파일이 속한 폴더 id. 삭제되었거나 범위 밖이면 -1.</summary>
    public int GetFileParent(int fileIndex)
        => (uint)fileIndex < (uint)_fileCount ? _fileParent[fileIndex] : -1;

    /// <summary>
    /// <paramref name="dirId"/> 아래에서 <paramref name="ancestorDirId"/> 의 <strong>직속 자식</strong>이 되는 폴더를 찾는다.
    /// Treemap 은 여러 단계를 겹쳐 그리므로, 깊은 곳의 사각형을 눌러도 폴더 목록에서는
    /// "현재 폴더 바로 아래의 어느 항목 안쪽인지"를 선택해 줘야 한다. 하위가 아니면 -1.
    /// </summary>
    public int ChildOnPathTo(int ancestorDirId, int dirId)
    {
        int cur = dirId;
        while ((uint)cur < (uint)_dirCount)
        {
            int parent = _dirParent[cur];
            if (parent == ancestorDirId) return cur;
            if (parent < 0) return -1;
            cur = parent;
        }
        return -1;
    }

    /// <summary>7. Breadcrumb - 루트부터 현재 폴더까지.</summary>
    public IReadOnlyList<(int Id, string Name)> GetAncestors(int dirId)
    {
        var list = new List<(int, string)>();
        int cur = dirId;
        while (cur >= 0 && (uint)cur < (uint)_dirCount)
        {
            string name = cur == RootId
                ? NormalizeRoot(_pool.GetString(_dirName[RootId]))
                : _pool.GetString(_dirName[cur]);
            list.Add((cur, name));
            if (cur == RootId) break;
            cur = _dirParent[cur];
        }
        list.Reverse();
        return list;
    }

    /// <summary>경로 문자열로 폴더 id 를 찾는다(캐시 복원 후 위치 유지 등에 사용).</summary>
    public int FindDirectory(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return RootId;
        string root = NormalizeRoot(_pool.GetString(_dirName[RootId]));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return RootId;

        string rest = fullPath[root.Length..].Trim('\\');
        if (rest.Length == 0) return RootId;

        int cur = RootId;
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int found = -1;
            for (int i = cur + 1; i < _dirCount; i++)
            {
                if (_dirParent[i] != cur || _dirDeleted[i]) continue;
                if (_pool.Get(_dirName[i]).Equals(part, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            }
            if (found < 0) return cur;
            cur = found;
        }
        return cur;
    }

    /// <summary>
    /// 경로로 노드(파일/폴더)를 찾는다 — 스캔 밖에서 옮겨지거나 지워진 항목을 저장소에서 빼기 위해 쓴다.
    /// 여러 경로를 한 번에 받아서, 같은 폴더 아래 항목이 수천 개여도 폴더 배열과 파일 배열을 각각 한 번만 훑는다.
    /// 스캔 범위 밖이거나 이미 없는 경로는 결과에서 빠진다. 스캔 루트 자체는 찾지 않는다.
    /// </summary>
    public List<(RowKind Kind, int Id)> FindNodes(IEnumerable<string> fullPaths)
    {
        var found = new List<(RowKind, int)>();
        string root = NormalizeRoot(_pool.GetString(_dirName[RootId])).TrimEnd('\\');

        var childMap = new Dictionary<int, Dictionary<string, int>>();
        Dictionary<string, int> ChildDirs(int parent)
        {
            if (childMap.TryGetValue(parent, out var map)) return map;
            map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = parent + 1; i < _dirCount; i++)
                if (_dirParent[i] == parent && !_dirDeleted[i]) map.TryAdd(_pool.GetString(_dirName[i]), i);
            return childMap[parent] = map;
        }

        var wantedFiles = new Dictionary<int, HashSet<string>>();

        foreach (var raw in fullPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path = raw.TrimEnd('\\');
            if (path.Length <= root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path[root.Length] != '\\')
                continue;

            var parts = path[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            int cur = RootId;
            bool ok = true;
            for (int k = 0; k < parts.Length - 1 && ok; k++)
                ok = ChildDirs(cur).TryGetValue(parts[k], out cur);
            if (!ok) continue;

            string leaf = parts[^1];
            if (ChildDirs(cur).TryGetValue(leaf, out int dirId))
            {
                found.Add((RowKind.Directory, dirId));
            }
            else
            {
                if (!wantedFiles.TryGetValue(cur, out var names))
                    wantedFiles[cur] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                names.Add(leaf);
            }
        }

        if (wantedFiles.Count > 0)
        {
            for (int f = 0; f < _fileCount; f++)
            {
                int parent = _fileParent[f];
                if (parent < 0 || !wantedFiles.TryGetValue(parent, out var names)) continue;
                if (names.Contains(_pool.GetString(_fileName[f]))) found.Add((RowKind.File, f));
            }
        }

        return found;
    }

    // ------------------------------------------------------------------ 조회

    /// <summary>
    /// 6. 현재 폴더의 직속 하위 폴더 + (옵션) 직속 파일.
    ///
    /// 자식 목록용 인덱스를 상시 유지하지 않고 parent 배열을 선형 스캔한다.
    /// int[] 연속 스캔이라 50만 폴더에서도 0.3ms 수준이고, 스캔 중 계속 자라는
    /// 자료구조를 따로 관리할 필요가 없어(=쓰기 비용 0) 오히려 전체 처리량이 높다.
    /// </summary>
    public List<EntryRow> GetChildren(int dirId, bool includeFiles, FilterOptions? filter = null)
    {
        var rows = new List<EntryRow>(64);
        if ((uint)dirId >= (uint)_dirCount) return rows;

        long denom = _dirTotalSize[dirId];
        if (denom <= 0) denom = 1;
        string parentPath = GetDirectoryPath(dirId);

        for (int i = dirId + 1; i < _dirCount; i++)
        {
            if (_dirParent[i] != dirId || _dirDeleted[i]) continue;
            long size = _dirTotalSize[i];
            if (filter is { MinSize: > 0 } && size < filter.MinSize) continue;

            var name = _pool.GetString(_dirName[i]);
            if (filter != null && !MatchesName(name, filter.NamePattern)) continue;

            rows.Add(new EntryRow
            {
                Kind = RowKind.Directory,
                Id = i,
                Name = name,
                FullPath = Combine(parentPath, name),
                Size = size,
                Ratio = (double)size / denom,
                FileCount = _dirTotalFiles[i],
                DirectoryCount = _dirTotalDirs[i],
                Modified = ToDateTime(_dirTime[i]),
                Category = (CategoryFlags)_dirCat[i],
            });
        }

        if (includeFiles)
        {
            foreach (int f in EnumerateFiles(dirId))
            {
                long size = _fileSize[f];
                if (filter is { MinSize: > 0 } && size < filter.MinSize) continue;
                string name = _pool.GetString(_fileName[f]);
                string ext = Extensions.NameOf(_fileExt[f]);
                if (filter != null && !MatchesName(name, filter.NamePattern)) continue;
                if (filter != null && !MatchesExtension(ext, filter.ExtensionPattern)) continue;

                rows.Add(new EntryRow
                {
                    Kind = RowKind.File,
                    Id = f,
                    Name = name,
                    FullPath = Combine(parentPath, name),
                    Size = size,
                    Ratio = (double)size / denom,
                    Modified = ToDateTime(_fileTime[f]),
                    Created = FromDay(_fileCreatedDay[f]),
                    Accessed = FromDay(_fileAccessedDay[f]),
                    Extension = ext,
                    Category = Extensions.CategoryOf(_fileExt[f]),
                });
            }
        }

        rows.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return rows;
    }

    /// <summary>9. 큰 파일 - Top-K 힙 결과를 행으로 변환한다(정렬 대상은 최대 K 개).</summary>
    public List<EntryRow> GetTopFiles(int take, FilterOptions? filter = null)
    {
        var indices = TopFiles.ToSortedDescending(Math.Max(take, 1));
        var rows = new List<EntryRow>(indices.Length);
        long denom = TotalSize > 0 ? TotalSize : 1;

        foreach (int f in indices)
        {
            if ((uint)f >= (uint)_fileCount || _fileParent[f] < 0) continue;
            long size = _fileSize[f];
            if (filter is { MinSize: > 0 } && size < filter.MinSize) continue;
            string name = _pool.GetString(_fileName[f]);
            string ext = Extensions.NameOf(_fileExt[f]);
            if (filter != null && !MatchesName(name, filter.NamePattern)) continue;
            if (filter != null && !MatchesExtension(ext, filter.ExtensionPattern)) continue;

            rows.Add(MakeFileRow(f, name, ext, size, denom));
        }
        return rows;
    }

    /// <summary>10. 확장자 클릭 시 해당 확장자 파일 목록(Lazy, 상한 있음).</summary>
    public List<EntryRow> GetFilesByExtension(string extension, int max = 2000, FilterOptions? filter = null)
    {
        var rows = new List<EntryRow>(Math.Min(max, 256));
        int extId = Extensions.Find(extension);
        if (extId < 0) return rows;
        long denom = TotalSize > 0 ? TotalSize : 1;

        for (int f = 0; f < _fileCount && rows.Count < max; f++)
        {
            if (_fileExt[f] != extId || _fileParent[f] < 0) continue;
            long size = _fileSize[f];
            if (filter is { MinSize: > 0 } && size < filter.MinSize) continue;
            rows.Add(MakeFileRow(f, _pool.GetString(_fileName[f]), extension, size, denom));
        }
        rows.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return rows;
    }

    /// <summary>12. 검색 - <see cref="SearchWithCount"/> 의 행 목록만 돌려주는 간편 버전.</summary>
    public List<EntryRow> Search(string term, int max = 5000, FilterOptions? filter = null, int scopeDirId = RootId)
        => SearchWithCount(term, max, filter, scopeDirId).Rows;

    /// <summary>
    /// 12. 검색 - 파일/폴더 이름. 와일드카드(`*` `?`)가 없으면 부분 일치, 있으면 이름 전체 glob(<see cref="NameMatcher"/>).
    ///
    /// 표시 상한(<paramref name="max"/>)은 <strong>크기 상위 K 개</strong>를 뜻한다. 예전처럼 앞에서부터 K 건을
    /// 채우고 자른 뒤 정렬하면 흔한 검색어에서 정작 큰 항목이 결과에서 빠진다.
    /// 최소 힙(크기 기준)으로 O(N log K) 에 상위 K 개만 유지하고, 행(EntryRow)은 살아남은 K 개에 대해서만 만든다.
    ///
    /// <paramref name="scopeDirId"/> 가 루트가 아니면 그 폴더의 하위만 찾는다(폴더 자신은 제외).
    /// "자식 id &gt; 부모 id" 불변식 덕분에 하위 판정은 폴더 배열 오름차순 1패스로 끝난다.
    /// </summary>
    public SearchOutcome SearchWithCount(string term, int max = 5000, FilterOptions? filter = null, int scopeDirId = RootId)
    {
        var matcher = new NameMatcher(term ?? string.Empty);
        if (matcher.IsEmpty || max <= 0) return new SearchOutcome { Rows = new List<EntryRow>(), TotalMatches = 0 };

        bool[]? inScope = null;
        int firstDir = 1;
        if (scopeDirId > RootId && scopeDirId < _dirCount)
        {
            inScope = new bool[_dirCount];
            inScope[scopeDirId] = true;
            for (int i = scopeDirId + 1; i < _dirCount; i++)
            {
                int p = _dirParent[i];
                if (p >= scopeDirId && p < i && inScope[p]) inScope[i] = true;
            }
            firstDir = scopeDirId + 1;
        }

        var heap = new PriorityQueue<(bool IsDir, int Id), long>(Math.Min(max, 1024));
        int total = 0;

        for (int i = firstDir; i < _dirCount; i++)
        {
            if (_dirParent[i] < 0 || _dirDeleted[i]) continue;
            if (inScope != null && !inScope[i]) continue;
            long size = _dirTotalSize[i];
            if (filter is { MinSize: > 0 } && size < filter.MinSize) continue;
            if (!matcher.IsMatch(_pool.Get(_dirName[i]))) continue;

            total++;
            OfferMatch(heap, max, (true, i), size);
        }

        for (int f = 0; f < _fileCount; f++)
        {
            int parent = _fileParent[f];
            if (parent < 0) continue;
            if (inScope != null && ((uint)parent >= (uint)inScope.Length || !inScope[parent])) continue;
            long size = _fileSize[f];
            if (filter is { MinSize: > 0 } && size < filter.MinSize) continue;
            if (!matcher.IsMatch(_pool.Get(_fileName[f]))) continue;
            if (filter != null && !MatchesExtension(Extensions.NameOf(_fileExt[f]), filter.ExtensionPattern)) continue;

            total++;
            OfferMatch(heap, max, (false, f), size);
        }

        long denom = TotalSize > 0 ? TotalSize : 1;
        var rows = new List<EntryRow>(heap.Count);
        while (heap.TryDequeue(out var hit, out _))
        {
            rows.Add(hit.IsDir
                ? MakeSearchDirRow(hit.Id, denom)
                : MakeFileRow(hit.Id, _pool.GetString(_fileName[hit.Id]),
                    Extensions.NameOf(_fileExt[hit.Id]), _fileSize[hit.Id], denom));
        }

        rows.Sort(static (a, b) =>
        {
            int c = b.Size.CompareTo(a.Size);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return new SearchOutcome { Rows = rows, TotalMatches = total };
    }

    private static void OfferMatch(PriorityQueue<(bool IsDir, int Id), long> heap, int max, (bool IsDir, int Id) item, long size)
    {
        if (heap.Count < max)
        {
            heap.Enqueue(item, size);
        }
        else if (heap.TryPeek(out _, out long smallest) && size > smallest)
        {
            heap.EnqueueDequeue(item, size);   // 가장 작은 항목을 밀어낸다
        }
    }

    private EntryRow MakeSearchDirRow(int i, long denom) => new()
    {
        Kind = RowKind.Directory,
        Id = i,
        Name = _pool.GetString(_dirName[i]),
        FullPath = GetDirectoryPath(i),
        Size = _dirTotalSize[i],
        Ratio = (double)_dirTotalSize[i] / denom,
        FileCount = _dirTotalFiles[i],
        DirectoryCount = _dirTotalDirs[i],
        Modified = ToDateTime(_dirTime[i]),
        Category = (CategoryFlags)_dirCat[i],
    };

    public IReadOnlyList<ExtensionRow> GetExtensionRows() => Extensions.BuildRows(TotalSize);

    /// <summary>43. 성능 모니터용 - 저장소가 실제로 쓰고 있는 대략적인 바이트 수.</summary>
    public long EstimatedMemoryBytes
        => (long)_dirParent.Length * (4 + 8 + 8 + 4 + 8 + 8 + 4 + 8 + 4 + 2)
         + (long)_fileParent.Length * (4 + 8 + 8 + 8 + 4 + 4)
         + _pool.CharCount * 2
         + (_fileIndexEntries?.Length ?? 0) * 4L
         + (_fileIndexStart?.Length ?? 0) * 4L;

    // ------------------------------------------------------------------ 내부

    private EntryRow MakeFileRow(int f, string name, string ext, long size, long denom) => new()
    {
        Kind = RowKind.File,
        Id = f,
        Name = name,
        FullPath = GetFilePath(f),
        Size = size,
        Ratio = (double)size / denom,
        Modified = ToDateTime(_fileTime[f]),
        Created = FromDay(_fileCreatedDay[f]),
        Accessed = FromDay(_fileAccessedDay[f]),
        Extension = ext,
        Category = Extensions.CategoryOf(_fileExt[f]),
    };

    /// <summary>
    /// 폴더의 직속 파일 열거.
    /// 스캔이 끝난 뒤(Sealed) 처음 필요해질 때만 CSR 인덱스를 만든다(34. Lazy Loading).
    /// 스캔 중에는 인덱스를 만들지 않고 선형 스캔한다 - 인덱스를 계속 갱신하는 비용이 더 크기 때문.
    /// </summary>
    private IEnumerable<int> EnumerateFiles(int dirId)
    {
        if (IsSealed && _fileIndexStart == null && _fileCount > 0) BuildFileIndex();

        if (_fileIndexStart != null && _fileIndexEntries != null && dirId + 1 < _fileIndexStart.Length)
        {
            for (int k = _fileIndexStart[dirId]; k < _fileIndexStart[dirId + 1]; k++)
                yield return _fileIndexEntries[k];
            yield break;
        }

        for (int f = 0; f < _fileCount; f++)
            if (_fileParent[f] == dirId) yield return f;
    }

    private void BuildFileIndex()
    {
        int d = _dirCount;
        var start = new int[d + 1];
        for (int f = 0; f < _fileCount; f++)
        {
            int p = _fileParent[f];
            if ((uint)p < (uint)d) start[p + 1]++;
        }
        for (int i = 0; i < d; i++) start[i + 1] += start[i];

        var entries = new int[_fileCount];
        var cursor = (int[])start.Clone();
        for (int f = 0; f < _fileCount; f++)
        {
            int p = _fileParent[f];
            if ((uint)p < (uint)d) entries[cursor[p]++] = f;
        }

        _fileIndexEntries = entries;
        _fileIndexStart = start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> GetExtensionSpan(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return ReadOnlySpan<char>.Empty;
        var ext = name[dot..];
        return ext.Length > 16 ? ReadOnlySpan<char>.Empty : ext;
    }

    private static bool MatchesName(string name, string pattern)
        => string.IsNullOrWhiteSpace(pattern)
           || name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>"*.pdb;*.log" / ".pdb" / "pdb" 모두 허용한다.</summary>
    internal static bool MatchesExtension(string ext, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        foreach (var raw in pattern.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().TrimStart('*');
            if (token.Length == 0) continue;
            if (token[0] != '.') token = "." + token;
            if (string.Equals(ext, token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Combine(string dir, string name)
        => dir.Length > 0 && dir[^1] == '\\' ? dir + name : dir + "\\" + name;

    private static string NormalizeRoot(string root)
        => root.Length == 2 && root[1] == ':' ? root + "\\" : root;

    public static DateTime ToDateTime(long fileTime)
    {
        if (fileTime <= 0 || fileTime > 2650467743999999999L) return default;
        try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
        catch { return default; }
    }

    private void EnsureDirCapacity(int needed)
    {
        if (needed <= _dirParent.Length) return;
        int n = _dirParent.Length;
        while (n < needed) n <<= 1;

        int old = _dirParent.Length;
        Array.Resize(ref _dirParent, n);
        Array.Fill(_dirParent, -1, old, n - old);
        Array.Resize(ref _dirName, n);
        Array.Resize(ref _dirOwnSize, n);
        Array.Resize(ref _dirOwnFiles, n);
        Array.Resize(ref _dirTotalSize, n);
        Array.Resize(ref _dirTotalFiles, n);
        Array.Resize(ref _dirTotalDirs, n);
        Array.Resize(ref _dirTime, n);
        Array.Resize(ref _dirAttr, n);
        Array.Resize(ref _dirCat, n);
        Array.Resize(ref _dirDeleted, n);
    }

    private void GrowFiles()
    {
        int n = _fileParent.Length * 2;
        Array.Resize(ref _fileParent, n);
        Array.Resize(ref _fileName, n);
        Array.Resize(ref _fileSize, n);
        Array.Resize(ref _fileTime, n);
        Array.Resize(ref _fileCreatedDay, n);
        Array.Resize(ref _fileAccessedDay, n);
        Array.Resize(ref _fileExt, n);
        Array.Resize(ref _fileAttr, n);
    }

    // ---- 53~66. 정리 추천 엔진 전용 내부 접근자 ----
    // 분석기는 같은 어셈블리 안에 있으므로 배열을 그대로 훑는다.
    // 파일 수백만 개를 IEnumerable 로 돌리면 열거자 오버헤드만으로 수백 ms 가 날아간다.
    internal int[] FileParentsRaw => _fileParent;
    internal long[] FileSizesRaw => _fileSize;
    internal long[] FileTimesRaw => _fileTime;
    internal int[] FileCreatedDaysRaw => _fileCreatedDay;
    internal int[] FileAccessedDaysRaw => _fileAccessedDay;
    internal int[] FileExtIdsRaw => _fileExt;
    internal int[] DirParentsRaw => _dirParent;
    internal uint[] DirCategoriesRaw => _dirCat;
    internal int[] DirAttributesRaw => _dirAttr;
    internal int[] FileAttributesRaw => _fileAttr;
    internal long[] DirTotalSizesRaw => _dirTotalSize;
    internal long[] DirTotalFilesRaw => _dirTotalFiles;
    internal long[] DirTimesRaw => _dirTime;
    internal ReadOnlySpan<char> FileNameSpan(int i) => _pool.Get(_fileName[i]);
    internal ReadOnlySpan<char> DirNameSpan(int i) => _pool.Get(_dirName[i]);

    /// <summary>
    /// 53/61/62. 각 폴더가 부모로부터 물려받은 카테고리 플래그.
    /// "C:\Windows 아래의 파일은 전부 보호 대상", "cache 폴더 안의 파일은 캐시" 같은 판단을 위해
    /// 파일마다 부모 체인을 거슬러 올라가면 O(파일 수 x 깊이) 가 된다.
    /// 자식 id > 부모 id 불변식 덕분에 id 오름차순 1패스로 전파할 수 있다 - O(폴더 수).
    /// </summary>
    internal uint[] BuildInheritedCategories()
    {
        var inherited = new uint[_dirCount];
        if (_dirCount == 0) return inherited;

        inherited[RootId] = _dirCat[RootId];
        for (int i = 1; i < _dirCount; i++)
        {
            int p = _dirParent[i];
            uint parentFlags = (uint)p < (uint)_dirCount ? inherited[p] : 0u;

            // Build / Log / Cache 는 "그 폴더와 그 하위 전체"에 적용되어야 의미가 있다.
            // 반면 Download / UserData / SourceCode / System 도 하위로 상속된다.
            inherited[i] = _dirCat[i] | parentFlags;
        }
        return inherited;
    }

    /// <summary>FILETIME -> 1601 기준 일수. 0 은 "알 수 없음".</summary>
    internal static int ToDay(long fileTime)
        => fileTime <= 0 ? 0 : (int)(fileTime / 864_000_000_000L);

    internal static DateTime FromDay(int day)
    {
        if (day <= 0) return default;
        try { return DateTime.FromFileTimeUtc(day * 864_000_000_000L).ToLocalTime(); }
        catch { return default; }
    }

    internal static int TodayDay => ToDay(DateTime.UtcNow.ToFileTimeUtc());

    // ---- 캐시 직렬화 지원(40. 결과 Cache) ----
    internal void WriteTo(BinaryWriter w)
    {
        w.Write(RootPath);
        w.Write(_dirCount);
        for (int i = 0; i < _dirCount; i++)
        {
            w.Write(_dirParent[i]);
            WriteName(w, _pool.Get(_dirName[i]));
            w.Write(_dirOwnSize[i]);
            w.Write(_dirOwnFiles[i]);
            w.Write(_dirTime[i]);
            w.Write(_dirAttr[i]);
        }
        w.Write(_fileCount);
        for (int i = 0; i < _fileCount; i++)
        {
            w.Write(_fileParent[i]);
            WriteName(w, _pool.Get(_fileName[i]));
            w.Write(_fileSize[i]);
            w.Write(_fileTime[i]);
            w.Write(_fileCreatedDay[i]);
            w.Write(_fileAccessedDay[i]);
            w.Write(_fileAttr[i]);
        }
    }

    private static void WriteName(BinaryWriter w, ReadOnlySpan<char> name)
    {
        w.Write((ushort)name.Length);
        for (int i = 0; i < name.Length; i++) w.Write((ushort)name[i]);
    }

    internal static NodeStore ReadFrom(BinaryReader r, int topK)
    {
        string root = r.ReadString();
        var store = new NodeStore(root, topK);

        int dirCount = r.ReadInt32();
        Span<char> buf = stackalloc char[StringPool.MaxNameLength > 512 ? 512 : StringPool.MaxNameLength];
        for (int i = 0; i < dirCount; i++)
        {
            int parent = r.ReadInt32();
            var name = ReadName(r, buf);
            _ = r.ReadInt64();   // ownSize: 아래에서 파일을 다시 넣으며 재계산되므로 버린다
            _ = r.ReadInt32();   // ownFiles
            long time = r.ReadInt64();
            int attr = r.ReadInt32();
            if (i != RootId) store.SetDirectory(i, parent, name, time, attr);
        }

        int fileCount = r.ReadInt32();
        for (int i = 0; i < fileCount; i++)
        {
            int parent = r.ReadInt32();
            var name = ReadName(r, buf);
            long size = r.ReadInt64();
            long time = r.ReadInt64();
            int createdDay = r.ReadInt32();
            int accessedDay = r.ReadInt32();
            int attr = r.ReadInt32();

            int idx = store.AddFile(parent, name, size, time, attr);
            store._fileCreatedDay[idx] = createdDay;
            store._fileAccessedDay[idx] = accessedDay;
        }

        store.Seal();
        return store;
    }

    private static ReadOnlySpan<char> ReadName(BinaryReader r, Span<char> buf)
    {
        int len = r.ReadUInt16();
        if (len > buf.Length) len = buf.Length;
        for (int i = 0; i < len; i++) buf[i] = (char)r.ReadUInt16();
        return buf[..len];
    }
}
