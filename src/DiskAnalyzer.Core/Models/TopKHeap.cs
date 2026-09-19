namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 35. Top-N 최적화.
///
/// 전체 파일을 정렬하면 O(N log N) + 전체 인덱스 배열 메모리가 필요하다.
/// 크기 K 의 최소 힙을 유지하면
///   - 새 파일 크기가 힙의 최소값보다 작으면 즉시 버림 (비교 1회)
///   - 크면 루트를 교체하고 sift-down: O(log K)
/// 따라서 O(N log K), 메모리는 K 고정.
/// K=1000 으로 상시 유지하고 UI 의 TOP 50/100/500/1000 은 이 결과를 잘라 쓴다.
/// </summary>
public sealed class TopKHeap
{
    private readonly long[] _size;
    private readonly int[] _index;
    private readonly int _k;
    private int _count;

    public TopKHeap(int k)
    {
        _k = Math.Max(1, k);
        _size = new long[_k];
        _index = new int[_k];
    }

    public int Count => _count;

    public void Offer(long size, int fileIndex)
    {
        if (_count < _k)
        {
            _size[_count] = size;
            _index[_count] = fileIndex;
            SiftUp(_count++);
            return;
        }

        // 힙이 가득 찬 뒤에는 절대 다수의 파일이 이 비교 한 번으로 걸러진다.
        if (size <= _size[0]) return;

        _size[0] = size;
        _index[0] = fileIndex;
        SiftDown(0);
    }

    public void Clear() => _count = 0;

    public int Capacity => _k;

    /// <summary>크기 내림차순 파일 인덱스 배열. 호출 시점에 한 번만 정렬한다(최대 K 개).</summary>
    public int[] ToSortedDescending(int take)
    {
        int n = Math.Min(_count, Math.Max(0, take));
        var pairs = new (long Size, int Index)[_count];
        for (int i = 0; i < _count; i++) pairs[i] = (_size[i], _index[i]);
        Array.Sort(pairs, static (a, b) => b.Size.CompareTo(a.Size));

        var result = new int[n];
        for (int i = 0; i < n; i++) result[i] = pairs[i].Index;
        return result;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (_size[parent] <= _size[i]) break;
            Swap(parent, i);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = (i << 1) + 1;
            if (left >= _count) break;
            int smallest = left;
            int right = left + 1;
            if (right < _count && _size[right] < _size[left]) smallest = right;
            if (_size[i] <= _size[smallest]) break;
            Swap(i, smallest);
            i = smallest;
        }
    }

    private void Swap(int a, int b)
    {
        (_size[a], _size[b]) = (_size[b], _size[a]);
        (_index[a], _index[b]) = (_index[b], _index[a]);
    }
}
