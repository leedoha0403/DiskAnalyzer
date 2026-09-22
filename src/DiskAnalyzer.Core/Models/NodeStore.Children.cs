namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 선버스트 한 겹을 만드는 데 필요한 최소 정보. 이름과 경로는 필요할 때만 따로 만든다
/// (한 번의 레이아웃에서 수천 건이 지나가므로 <see cref="EntryRow"/> 를 만들면 그것만으로 비용이다).
/// </summary>
public readonly record struct ChildRef(RowKind Kind, int Id, long Size);

/// <summary>
/// <see cref="NodeStore.CollectChildren"/> 가 재사용하는 작업 버퍼.
/// 호출자가 들고 있다가 같은 레이아웃의 다음 겹에 다시 넘긴다.
/// </summary>
public sealed class ChildScratch
{
    internal int[] Mark = [];
    internal int Stamp;
}

public sealed partial class NodeStore
{
    /// <summary>
    /// 폴더에 붙은 분류 플래그. <see cref="GetChildren"/> 로 형제 전체를 만들어 찾는 대신 바로 읽는다
    /// (보호 등급 1차 판정이 이 플래그를 쓴다).
    /// </summary>
    public CategoryFlags GetDirectoryCategory(int dirId)
        => (uint)dirId < (uint)_dirCount ? (CategoryFlags)_dirCat[dirId] : CategoryFlags.None;

    /// <summary>파일의 확장자 분류 플래그. 같은 이유로 행을 만들지 않고 바로 읽는다.</summary>
    public CategoryFlags GetFileCategory(int fileIndex)
        => (uint)fileIndex < (uint)_fileCount ? Extensions.CategoryOf(_fileExt[fileIndex]) : CategoryFlags.None;

    /// <summary>
    /// 여러 폴더의 직속 자식을 <b>배열 1패스</b>로 모은다.
    ///
    /// <para><see cref="GetChildren"/> 를 폴더마다 부르면 폴더 하나당 <c>_dirParent</c> 전체를 훑는다.
    /// 선버스트는 한 겹에 수백 개의 폴더를 동시에 펼치므로 그 방식으로는 O(겹 × 폴더수 × 전체폴더수) 가 된다.
    /// 여기서는 관심 폴더에 스탬프를 찍어 두고 배열을 한 번만 지나간다 — 겹당 O(전체폴더수),
    /// 그것도 Dictionary 조회가 아니라 <c>int[]</c> 접근이다.</para>
    ///
    /// <para>파일은 봉인 후 CSR 인덱스가 만들어져 있으므로 폴더별로 바로 꺼낸다.</para>
    /// </summary>
    /// <param name="dirIds">자식을 모을 폴더들. 중복이 있어도 된다.</param>
    /// <param name="includeFiles">직속 파일도 함께 모을지.</param>
    /// <param name="scratch">같은 레이아웃 안에서 재사용하는 버퍼. null 이면 매번 새로 만든다.</param>
    /// <returns>폴더 id → 크기 내림차순 자식 목록. 자식이 없는 폴더는 빈 목록이 들어 있다.</returns>
    public Dictionary<int, List<ChildRef>> CollectChildren(
        IReadOnlyList<int> dirIds, bool includeFiles, ChildScratch? scratch = null)
    {
        var result = new Dictionary<int, List<ChildRef>>(dirIds.Count);
        if (dirIds.Count == 0 || _dirCount == 0) return result;

        scratch ??= new ChildScratch();
        if (scratch.Mark.Length < _dirCount)
        {
            scratch.Mark = new int[Math.Max(_dirCount, 64)];
            scratch.Stamp = 0;
        }

        // 스탬프를 매번 올려 쓰므로 버퍼를 지울 필요가 없다.
        // int 가 한 바퀴 돌 만큼 부르면(21억 번) 그때만 초기화한다.
        if (++scratch.Stamp == int.MaxValue)
        {
            Array.Clear(scratch.Mark);
            scratch.Stamp = 1;
        }
        int stamp = scratch.Stamp;
        var mark = scratch.Mark;

        foreach (int id in dirIds)
        {
            if ((uint)id >= (uint)_dirCount || _dirDeleted[id]) continue;
            mark[id] = stamp;
            result[id] = new List<ChildRef>(8);
        }
        if (result.Count == 0) return result;

        // 하위 폴더 — 배열 1패스. 루트(0)는 부모가 없으므로 1부터.
        for (int i = 1; i < _dirCount; i++)
        {
            if (_dirDeleted[i]) continue;
            int p = _dirParent[i];
            if ((uint)p >= (uint)_dirCount || mark[p] != stamp) continue;
            long size = _dirTotalSize[i];
            if (size > 0) result[p].Add(new ChildRef(RowKind.Directory, i, size));
        }

        if (includeFiles)
        {
            foreach (var (dirId, list) in result)
            {
                foreach (int f in EnumerateFiles(dirId))
                {
                    long size = _fileSize[f];
                    if (size > 0) list.Add(new ChildRef(RowKind.File, f, size));
                }
            }
        }

        foreach (var list in result.Values)
            list.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        return result;
    }
}
