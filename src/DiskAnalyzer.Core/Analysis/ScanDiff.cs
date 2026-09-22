using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 지금 스캔과 직전 스캔을 맞춰 본다 — "지난번보다 무엇이 늘었는가".
///
/// <para>두 저장소는 서로 다른 스캔이라 <b>id 가 전혀 호환되지 않는다</b>. 유일한 공통 좌표는 경로다.
/// 그렇다고 조각마다 경로를 만들어 옛 저장소에서 찾으면, 파일 하나를 찾을 때마다 그 폴더의 자식을
/// 전부 만들게 되어 금방 느려진다.</para>
///
/// <para>그래서 <b>부모 단위로 모은다</b>: 링에 실제로 그려진 조각들의 부모 폴더만 추려 옛 저장소에서
/// 한 번에 자식을 모으고(<see cref="NodeStore.CollectChildren"/>), 이름으로 맞춘다.
/// 경로 탐색은 부모 수만큼만 일어나고 자식 조회는 배열 1패스다.</para>
/// </summary>
public static class ScanDiff
{
    /// <summary>
    /// 레이아웃의 각 조각이 <b>직전 스캔에서 얼마였는지</b>. 그때 없던 조각은 결과에 들어가지 않는다
    /// (0 으로 넣으면 "새로 생긴 것"과 "0 바이트였던 것"이 구별되지 않는다).
    /// </summary>
    /// <param name="current">지금 스캔. 조각 id 는 이쪽 기준이다.</param>
    /// <param name="previous">직전 스캔.</param>
    /// <param name="layout">비교할 조각들.</param>
    /// <param name="currentDirId">레이아웃의 중심 폴더. 첫 겹 조각들의 부모다.</param>
    /// <returns>조각 키(<see cref="SunburstLayout.KeyOf"/>) → 직전 크기(바이트).</returns>
    public static Dictionary<long, long> PreviousSizes(
        NodeStore? current, NodeStore? previous, SunburstLayout? layout, int currentDirId)
    {
        var result = new Dictionary<long, long>();
        if (current == null || previous == null || layout == null || layout.IsEmpty) return result;

        // 1) 조각마다 "부모 폴더의 현재 경로" 를 구한다. 부모는 언제나 폴더다(파일은 자식을 갖지 않는다).
        var parentPathOf = new Dictionary<int, string>();   // 레이아웃 부모 인덱스(-1 = 중심) → 경로
        foreach (var segment in layout.Segments)
        {
            int parent = segment.ParentIndex;
            if (parentPathOf.ContainsKey(parent)) continue;

            int dirId = parent < 0 ? currentDirId : layout.Segments[parent].Id;
            if (dirId < 0) continue;
            parentPathOf[parent] = current.GetDirectoryPath(dirId);
        }

        // 2) 그 경로들을 옛 저장소에서 찾는다. 경로 탐색은 부모 수만큼만 일어난다.
        var oldParentOf = new Dictionary<int, int>(parentPathOf.Count);
        var oldParentIds = new List<int>(parentPathOf.Count);
        foreach (var (parent, path) in parentPathOf)
        {
            int oldId = previous.FindDirectory(path);
            if (oldId < 0) continue;              // 그 폴더 자체가 그때는 없었다
            oldParentOf[parent] = oldId;
            oldParentIds.Add(oldId);
        }
        if (oldParentIds.Count == 0) return result;

        // 3) 옛 자식들을 배열 1패스로 모아 이름 → 크기 표를 만든다.
        var oldChildren = previous.CollectChildren(oldParentIds, includeFiles: true);
        var byName = new Dictionary<int, Dictionary<string, long>>(oldChildren.Count);

        foreach (var (oldDirId, kids) in oldChildren)
        {
            var map = new Dictionary<string, long>(kids.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var k in kids)
            {
                string name = k.Kind == RowKind.Directory
                    ? previous.GetDirectoryName(k.Id)
                    : previous.GetFileName(k.Id);

                // 같은 폴더에 폴더와 파일이 같은 이름으로 있을 수는 없다. 그래도 덮어쓰지 않고 첫 것을 남긴다.
                map.TryAdd(name, k.Size);
            }
            byName[oldDirId] = map;
        }

        // 4) 조각을 이름으로 맞춘다.
        foreach (var segment in layout.Segments)
        {
            if (segment.Id < 0) continue;                                  // 묶음 등 가상 조각
            if (!oldParentOf.TryGetValue(segment.ParentIndex, out int oldParent)) continue;
            if (!byName.TryGetValue(oldParent, out var map)) continue;
            if (!map.TryGetValue(segment.Name, out long oldSize)) continue;   // 그때는 없던 항목

            result[SunburstLayout.KeyOf(segment.Kind, segment.Id)] = oldSize;
        }

        return result;
    }
}
