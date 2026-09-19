using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// 1/2/12. 스캔 결과와 <strong>실제 파일 시스템</strong>을 대조한다.
///
/// 전체 재스캔은 드라이브 크기에 비례하지만, 이미 화면에 올라와 있는 항목(수십~수천 개)의
/// 존재 여부와 크기만 확인하는 것은 항목 수에만 비례한다.
/// 그래서 새로고침은 "① 즉시 대조(빠름) → ② 전체 재스캔(정확)" 2단계로 동작한다.
///
/// 파일 하나당 <c>GetFileAttributesEx</c> 1회만 호출한다. 파일을 열지 않으므로
/// 사용 중인 파일이어도 잠기지 않고, 내용도 읽지 않는다(26).
/// </summary>
public static class FileVerifier
{
    public readonly record struct Target(RowKind Kind, int Id, string Path, long KnownSize);

    /// <summary>
    /// 대조를 병렬로 수행한다. 워커 수를 제한하는 이유는 스캔과 동일하다 -
    /// 메타데이터 조회도 I/O 라서 무한정 늘리면 오히려 느려진다(22).
    /// </summary>
    public static async Task<List<VerifyOutcome>> VerifyAsync(
        IReadOnlyList<Target> targets,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<VerifyOutcome>();
        if (targets.Count == 0) return new List<VerifyOutcome>();

        int workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        int done = 0;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            (target, token) =>
            {
                token.ThrowIfCancellationRequested();
                results.Add(Verify(target));

                int n = Interlocked.Increment(ref done);
                if ((n & 0x3F) == 0) progress?.Report(n);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        progress?.Report(targets.Count);
        return results.ToList();
    }

    public static VerifyOutcome Verify(Target target)
    {
        if (target.Kind == RowKind.Directory)
        {
            bool exists = Directory.Exists(target.Path);
            return new VerifyOutcome
            {
                Kind = target.Kind,
                Id = target.Id,
                Path = target.Path,
                State = exists ? VerifyState.Unchanged : VerifyState.Missing,
                OldSize = target.KnownSize,
                NewSize = target.KnownSize,
            };
        }

        if (!Win32.GetFileAttributesEx(LongPath(target.Path), 0, out var data))
        {
            int err = Marshal.GetLastWin32Error();
            var state = err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND
                ? VerifyState.Missing
                : VerifyState.Inaccessible;

            return new VerifyOutcome
            {
                Kind = target.Kind,
                Id = target.Id,
                Path = target.Path,
                State = state,
                OldSize = target.KnownSize,
                NewSize = state == VerifyState.Missing ? 0 : target.KnownSize,
            };
        }

        long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

        return new VerifyOutcome
        {
            Kind = target.Kind,
            Id = target.Id,
            Path = target.Path,
            State = size == target.KnownSize ? VerifyState.Unchanged : VerifyState.Changed,
            OldSize = target.KnownSize,
            NewSize = size,
            NewModified = data.ftLastWriteTime,
            NewAccessed = data.ftLastAccessTime,
        };
    }

    /// <summary>MAX_PATH 를 넘는 경로도 확인할 수 있게 접두사를 붙인다.</summary>
    private static string LongPath(string path)
        => path.Length < 240 || path.StartsWith(@"\\", StringComparison.Ordinal)
            ? path
            : @"\\?\" + path;
}
