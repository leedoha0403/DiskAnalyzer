using System.Runtime.InteropServices;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Scanning;

public readonly record struct FsFileInfo(string Name, long Size, long Modified, long Created, long Accessed, uint Attributes);

public readonly record struct FsDirInfo(string Name, long Modified, long Created, long Accessed, uint Attributes, bool IsReparse);

public enum ListingStatus { Ok, NotFound, AccessDenied, Error }

/// <summary>폴더 하나의 직속 항목(파일 / 하위 폴더). 스캔과 같은 규칙(숨김·시스템 필터, 이름 길이)으로 읽는다.</summary>
public sealed class FolderListing
{
    public ListingStatus Status { get; init; }
    public List<FsFileInfo> Files { get; } = new();
    public List<FsDirInfo> Dirs { get; } = new();
    public bool Ok => Status == ListingStatus.Ok;
}

/// <summary>부분 스캔 결과의 노드 하나. <see cref="Parent"/> 는 같은 트리 안의 인덱스이고, -1 이면 붙일 폴더의 직속이다.</summary>
public sealed class ScannedNode
{
    public required int Parent { get; init; }
    public required bool IsDirectory { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public long Modified { get; init; }
    public long Created { get; init; }
    public long Accessed { get; init; }
    public uint Attributes { get; init; }
}

/// <summary>
/// 폴더 아래를 스캔한 결과. 부모가 항상 자식보다 앞에 오도록(레벨 순) 쌓는다 —
/// NodeStore 의 "자식 id &gt; 부모 id" 불변식을 그대로 지킬 수 있다.
/// </summary>
public sealed class ScannedTree
{
    public List<ScannedNode> Nodes { get; } = new();
    public int Files => Nodes.Count(n => !n.IsDirectory);
    public int Directories => Nodes.Count(n => n.IsDirectory);
    public long Bytes => Nodes.Where(n => !n.IsDirectory).Sum(n => n.Size);
}

/// <summary>
/// 폴더 단위 다시 읽기. 드라이브 전체 스캔(ScanController)과 달리 필요한 폴더만 읽는다.
///  · <see cref="List"/>      : 폴더 하나의 직속 항목 (변경 감지에 쓴다 — 보통 수 ms)
///  · <see cref="ScanContents"/> : 폴더 아래 전체 (새로 생긴 폴더 / 하위까지 다시 스캔)
/// 저장소를 건드리지 않는다. 결과를 저장소에 넣는 일은 <see cref="NodeStore.ApplyRefresh"/> 가 UI 스레드에서 한다.
/// </summary>
public static class FolderScanner
{
    private const int MaxNameChars = 255;   // ScanBatch 와 같은 상한 — 그래야 저장된 이름과 비교가 맞는다

    public static unsafe FolderListing List(string path, ScanOptions options)
    {
        var listing = new FolderListing();

        using var handle = Win32.FindFirstFileEx(
            BuildSearchPattern(path), Win32.FindExInfoBasic, out var data,
            Win32.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            // 빈 폴더는 "." / ".." 가 있어서 여기로 오지 않는다. 여기로 왔다면 폴더가 없거나 못 여는 것이다.
            return new FolderListing
            {
                Status = err switch
                {
                    Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND => ListingStatus.NotFound,
                    Win32.ERROR_ACCESS_DENIED => ListingStatus.AccessDenied,
                    _ => ListingStatus.Error,
                },
            };
        }

        bool showHidden = options.ShowHiddenFiles;
        bool showSystem = options.ShowSystemFiles;

        do
        {
            var name = GetName(data.cFileName);
            if (name.Length == 0) continue;
            if (name[0] == '.' && (name.Length == 1 || (name.Length == 2 && name[1] == '.'))) continue;

            uint attr = data.dwFileAttributes;
            if (!showHidden && (attr & Win32.FILE_ATTRIBUTE_HIDDEN) != 0) continue;
            if (!showSystem && (attr & Win32.FILE_ATTRIBUTE_SYSTEM) != 0) continue;

            string n = new string(name[..Math.Min(name.Length, MaxNameChars)]);
            if ((attr & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0)
                listing.Dirs.Add(new FsDirInfo(n, data.ftLastWriteTime, data.ftCreationTime, data.ftLastAccessTime, attr,
                    (attr & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0));
            else
                listing.Files.Add(new FsFileInfo(n, data.Size, data.ftLastWriteTime, data.ftCreationTime, data.ftLastAccessTime, attr));
        }
        while (Win32.FindNextFile(handle, out data));

        return listing;
    }

    /// <summary>
    /// <paramref name="path"/> 아래 전체를 읽는다(폴더 자신은 포함하지 않는다). 한 층씩 병렬로 읽고, 결과는 부모가 먼저 오게 쌓는다.
    /// 읽지 못하는 하위 폴더는 건너뛴다(전체 스캔과 같다). 취소되면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public static ScannedTree ScanContents(string path, ScanOptions options, CancellationToken ct = default)
    {
        var tree = new ScannedTree();
        var level = new List<(int Node, string Path)> { (-1, path) };
        int degree = Math.Clamp(Environment.ProcessorCount, 2, 8);

        while (level.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var listings = new FolderListing?[level.Count];
            if (level.Count == 1) listings[0] = List(level[0].Path, options);
            else
                Parallel.For(0, level.Count, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                    i => listings[i] = List(level[i].Path, options));

            var next = new List<(int, string)>();
            for (int i = 0; i < level.Count; i++)
            {
                var listing = listings[i];
                if (listing is not { Ok: true }) continue;
                var (parentNode, parentPath) = level[i];

                foreach (var d in listing.Dirs)
                {
                    int index = tree.Nodes.Count;
                    tree.Nodes.Add(new ScannedNode
                    {
                        Parent = parentNode, IsDirectory = true, Name = d.Name,
                        Modified = d.Modified, Created = d.Created, Accessed = d.Accessed, Attributes = d.Attributes,
                    });

                    // 심볼릭 링크 / 정션은 노드만 기록하고 따라가지 않는다(전체 스캔과 같은 규칙).
                    if (d.IsReparse && !options.FollowReparsePoints) continue;
                    next.Add((index, Combine(parentPath, d.Name)));
                }

                foreach (var f in listing.Files)
                {
                    tree.Nodes.Add(new ScannedNode
                    {
                        Parent = parentNode, IsDirectory = false, Name = f.Name, Size = f.Size,
                        Modified = f.Modified, Created = f.Created, Accessed = f.Accessed, Attributes = f.Attributes,
                    });
                }
            }
            level = next;
        }
        return tree;
    }

    private static string BuildSearchPattern(string path)
    {
        bool needsSeparator = path.Length > 0 && path[^1] != '\\';
        string prefix = path.StartsWith(@"\\", StringComparison.Ordinal) ? string.Empty : @"\\?\";
        return needsSeparator ? prefix + path + @"\*" : prefix + path + "*";
    }

    internal static string Combine(string dir, string name)
        => dir.Length > 0 && dir[^1] == '\\' ? dir + name : dir + "\\" + name;

    private static unsafe ReadOnlySpan<char> GetName(char* p)
    {
        int len = 0;
        while (len < Win32.MAX_PATH && p[len] != '\0') len++;
        return new ReadOnlySpan<char>(p, len);
    }
}
