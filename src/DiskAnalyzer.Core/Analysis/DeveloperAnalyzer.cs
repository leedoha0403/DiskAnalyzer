using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 63. 개발자 정리 분석.
///  - Visual Studio: 프로젝트별 빌드 산출물(.vs / Debug / Release / x64 / x86 / bin / obj) 합계
///  - Git: .git 폴더 크기(크기만 알려 주고 정리 추천은 하지 않는다)
///  - 로그: 7 / 30 / 90 / 180일 이상으로 그룹화한 총 용량
/// </summary>
public sealed class DeveloperAnalyzer
{
    private const int MaxRows = 200;

    public DeveloperReport Analyze(NodeStore store, uint[] inherited, int today, CancellationToken ct = default)
    {
        int dirCount = store.DirectorySlots;
        var dirParents = store.DirParentsRaw;
        var dirCats = store.DirCategoriesRaw;
        var dirSizes = store.DirTotalSizesRaw;
        var dirTimes = store.DirTimesRaw;

        // 프로젝트 = "빌드 산출물 폴더의 부모". 여러 산출물 폴더가 한 프로젝트에 속하므로 합산한다.
        var projects = new Dictionary<int, (long Size, long Time, List<string> Folders)>();
        var gitRepos = new List<GitRepoInfo>();
        long totalBuild = 0, totalGit = 0;

        for (int i = 1; i < dirCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            int parent = dirParents[i];
            if ((uint)parent >= (uint)dirCount) continue;

            var own = (CategoryFlags)dirCats[i];
            var parentInherited = (CategoryFlags)inherited[parent];
            var selfInherited = (CategoryFlags)inherited[i];

            if ((selfInherited & CategoryFlags.System) != 0) continue;

            // Git: .git 폴더 자체
            if ((own & CategoryFlags.Git) != 0)
            {
                long gitSize = dirSizes[i];
                totalGit += gitSize;
                gitRepos.Add(new GitRepoInfo
                {
                    RepoName = new string(store.DirNameSpan(parent)),
                    RepoPath = store.GetDirectoryPath(parent),
                    GitSize = gitSize,
                    WorkingTreeSize = Math.Max(0, dirSizes[parent] - gitSize),
                });
                continue;
            }

            // 빌드 산출물: 중첩된 경우 가장 바깥 폴더만 센다(x64\Debug 처럼 겹치면 이중 계산이 된다).
            bool isBuildRoot = (own & (CategoryFlags.Build | CategoryFlags.VisualStudio)) != 0 &&
                               (parentInherited & (CategoryFlags.Build | CategoryFlags.VisualStudio)) == 0;
            if (!isBuildRoot) continue;
            if ((selfInherited & CategoryFlags.Git) != 0) continue;

            long size = dirSizes[i];
            if (size <= 0) continue;
            totalBuild += size;

            if (!projects.TryGetValue(parent, out var acc))
                acc = (0, 0, new List<string>(4));

            acc.Size += size;
            acc.Time = Math.Max(acc.Time, dirTimes[i]);
            if (acc.Folders.Count < 6) acc.Folders.Add(new string(store.DirNameSpan(i)));
            projects[parent] = acc;
        }

        var projectRows = projects
            .Select(kv => new ProjectBuildInfo
            {
                ProjectDirId = kv.Key,
                ProjectName = new string(store.DirNameSpan(kv.Key)),
                ProjectPath = store.GetDirectoryPath(kv.Key),
                BuildOutputSize = kv.Value.Size,
                LastModified = NodeStore.ToDateTime(kv.Value.Time),
                Folders = string.Join(", ", kv.Value.Folders),
            })
            .OrderByDescending(p => p.BuildOutputSize)
            .Take(MaxRows)
            .ToList();

        var gitRows = gitRepos
            .OrderByDescending(g => g.GitSize)
            .Take(MaxRows)
            .ToList();

        var (buckets, totalLog, oldLog) = BuildLogBuckets(store, inherited, today, ct);

        return new DeveloperReport
        {
            Projects = projectRows,
            GitRepos = gitRows,
            LogBuckets = buckets,
            TotalBuildOutput = totalBuild,
            TotalGitSize = totalGit,
            TotalLogSize = totalLog,
            OldLogSize = oldLog,
        };
    }

    private static (List<LogAgeBucket>, long Total, long Old) BuildLogBuckets(
        NodeStore store, uint[] inherited, int today, CancellationToken ct)
    {
        Span<long> sizes = stackalloc long[5];
        Span<int> counts = stackalloc int[5];

        var fileParents = store.FileParentsRaw;
        var fileSizes = store.FileSizesRaw;
        var fileTimes = store.FileTimesRaw;
        var fileExts = store.FileExtIdsRaw;
        int dirCount = store.DirectorySlots;
        int fileCount = store.FileSlots;

        long total = 0, old = 0;

        for (int f = 0; f < fileCount; f++)
        {
            if ((f & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int parent = fileParents[f];
            if ((uint)parent >= (uint)dirCount) continue;

            var flags = store.Extensions.CategoryOf(fileExts[f]) | (CategoryFlags)inherited[parent];
            if ((flags & CategoryFlags.Log) == 0) continue;
            if ((flags & CategoryFlags.System) != 0) continue;

            long size = fileSizes[f];
            int age = Math.Max(0, today - NodeStore.ToDay(fileTimes[f]));

            int bucket = age switch
            {
                <= 7 => 0,
                <= 30 => 1,
                <= 90 => 2,
                <= 180 => 3,
                _ => 4,
            };
            sizes[bucket] += size;
            counts[bucket]++;
            total += size;
            if (bucket >= 3) old += size;
        }

        string[] labels = { "최근 7일", "8 ~ 30일", "31 ~ 90일", "91 ~ 180일", "180일 이상" };
        var list = new List<LogAgeBucket>(5);
        for (int i = 0; i < 5; i++)
            list.Add(new LogAgeBucket { Label = labels[i], Size = sizes[i], Count = counts[i] });

        return (list, total, old);
    }
}
