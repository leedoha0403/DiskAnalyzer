using System.Diagnostics;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 53~66. 정리 추천 엔진.
///
/// [원칙 - 66]
/// 이 엔진의 목표는 "삭제해야 할 파일을 결정하는 것"이 아니라
/// "사용자가 확인할 가치가 높은 정리 후보를 우선 보여주는 것"이다.
/// 따라서 모든 후보에는 선정 이유 / 등급 / 위험도가 반드시 붙고,
/// 보호 경로(61)와 사용자 중요 폴더(62)는 절대 "정리 가능성 높음"으로 올라가지 않는다.
///
/// [판단에 쓰는 신호 - 53]
/// 마지막 수정일, 마지막 접근일, 생성일, 크기, 파일 유형, 위치, 이름,
/// 시스템 경로 여부, 개발 산출물 여부, 임시/캐시/로그 여부를 조합한다.
///
/// [성능]
/// 스캔이 끝난 NodeStore 를 파일 1회 + 폴더 2회 순회로 분석한다.
/// 폴더 카테고리 상속(BuildInheritedCategories)을 미리 계산해 두기 때문에
/// 파일마다 부모 체인을 거슬러 올라가지 않는다 - O(파일 수 + 폴더 수).
/// </summary>
public sealed class CleanupAnalyzer
{
    private const long Mb = 1024L * 1024L;

    public CleanupReport Analyze(NodeStore store, CleanupOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var inherited = store.BuildInheritedCategories();
        int dirCount = store.DirectorySlots;
        int fileCount = store.FileSlots;
        int today = NodeStore.TodayDay;

        var dirParents = store.DirParentsRaw;
        var dirCats = store.DirCategoriesRaw;
        var dirSizes = store.DirTotalSizesRaw;
        var dirFiles = store.DirTotalFilesRaw;
        var dirTimes = store.DirTimesRaw;
        var dirAttrs = store.DirAttributesRaw;
        var fileAttrs = store.FileAttributesRaw;

        var fileParents = store.FileParentsRaw;
        var fileSizes = store.FileSizesRaw;
        var fileTimes = store.FileTimesRaw;
        var fileCreated = store.FileCreatedDaysRaw;
        var fileAccessed = store.FileAccessedDaysRaw;
        var fileExts = store.FileExtIdsRaw;

        bool accessReliable = DetectAccessTimeReliability(fileTimes, fileAccessed, fileCount);

        var candidates = new List<CleanupCandidate>(1024);
        long protectedSize = 0;
        int protectedCount = 0;

        // ---------------------------------------------------------- 1) 폴더 단위 후보
        // 캐시/임시/빌드 폴더는 파일 하나하나가 아니라 "폴더 통째로" 보는 것이 사용자에게 훨씬 유용하다.
        // 같은 성격의 폴더가 중첩되어 있으면 가장 바깥 폴더 하나만 후보로 올린다.
        var emitted = new bool[dirCount];

        for (int i = 1; i < dirCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var own = (CategoryFlags)dirCats[i];
            int parent = dirParents[i];
            if ((uint)parent >= (uint)dirCount) continue;

            var parentInherited = (CategoryFlags)inherited[parent];
            var selfInherited = (CategoryFlags)inherited[i];

            // 61. 시스템 보호 경로는 후보로 만들지 않고 정보만 모은다.
            if ((selfInherited & CategoryFlags.System) != 0) continue;

            // 63. .git 은 크기만 알려 주고 캐시처럼 자동 정리 추천하지 않는다.
            if ((selfInherited & CategoryFlags.Git) != 0) continue;

            var kind = ClassifyFolder(own, parentInherited);
            if (kind == null) continue;

            long size = dirSizes[i];
            if (size < options.MinFolderSize) continue;

            emitted[i] = true;

            int ageDays = DaysSince(today, NodeStore.ToDay(dirTimes[i]));
            var (category, grade) = kind.Value;

            // 62. 사용자 중요 폴더 / 소스 트리 안이라면 등급을 낮춘다.
            if (grade == CleanupGrade.HighlyCleanable && IsUserSensitive(selfInherited) && category is not
                (CleanupCategory.VisualStudioBuild or CleanupCategory.Cache or CleanupCategory.Temp))
            {
                grade = CleanupGrade.NeedsReview;
            }

            string path = store.GetDirectoryPath(i);

            // ===== 보호 등급을 먼저 결정한다. 정리 가능 규칙은 보호 규칙을 덮어쓸 수 없다. =====
            var verdict = ProtectionEvaluator.Evaluate(path, true, (uint)dirAttrs[i], selfInherited);
            if (verdict.Level >= ProtectionLevel.P1)
            {
                protectedSize += size;
                protectedCount++;
                emitted[i] = false;
                continue;
            }

            var level = ResolveP2(verdict, ageDays);
            int score = CleanupScorer.Score(new CleanupScorer.Input(
                size, ageDays, ageDays, accessReliable,
                (selfInherited & (CategoryFlags.Cache | CategoryFlags.Temp)) != 0,
                verdict.RegenerableLocation));

            var tier = CleanupScorer.TierOf(score);
            if (tier == RecommendTier.None) { emitted[i] = false; continue; }

            grade = GradeFor(level);
            candidates.Add(new CleanupCandidate
            {
                Kind = RowKind.Directory,
                Id = i,
                Name = new string(store.DirNameSpan(i)),
                FullPath = path,
                Size = size,
                Modified = NodeStore.ToDateTime(dirTimes[i]),
                FileCount = dirFiles[i],    // 3-1. 그룹 보기에서 파일 개수를 보여 주기 위해 필요
                Category = category,
                Grade = grade,
                Risk = CleanupCandidate.RiskOf(grade),
                Reason = FolderReason(category, ageDays, size),
                Protection = verdict,
                Level = level,
                Score = score,
                Tier = tier,
                AgeDays = ageDays,
                UnusedDays = ageDays,
            });
        }

        // 폴더 후보 하위의 파일은 중복 계산하지 않도록 "덮인 폴더"를 아래로 전파한다(O(폴더 수)).
        var covered = new bool[dirCount];
        for (int i = 1; i < dirCount; i++)
        {
            int p = dirParents[i];
            bool parentCovered = (uint)p < (uint)dirCount && covered[p];
            covered[i] = emitted[i] || parentCovered;
        }

        // ---------------------------------------------------------- 2) 파일 단위 후보
        var emittedFiles = new HashSet<int>();

        for (int f = 0; f < fileCount; f++)
        {
            if ((f & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            long size = fileSizes[f];
            int parent = fileParents[f];
            if ((uint)parent >= (uint)dirCount) continue;

            var folderFlags = (CategoryFlags)inherited[parent];

            if ((folderFlags & CategoryFlags.System) != 0)
            {
                // 61. 보호 경로 - 정보만 집계한다.
                if (size >= options.MinFileSize) { protectedSize += size; protectedCount++; }
                continue;
            }

            if (size < options.MinFileSize) continue;
            if (covered[parent]) continue;

            var extFlags = store.Extensions.CategoryOf(fileExts[f]);
            var flags = extFlags | folderFlags;

            int modifiedDay = NodeStore.ToDay(fileTimes[f]);
            int ageDays = DaysSince(today, modifiedDay);
            int accessDay = fileAccessed[f];
            int unusedDays = accessReliable && accessDay > 0
                ? Math.Min(ageDays, DaysSince(today, accessDay))
                : ageDays;

            // 53. 생성일도 신호로 쓴다.
            // "이 자리에 얼마나 오래 있었는가"는 다운로드/설치 파일 판단에서 수정일보다 정확하다.
            int retainedDays = DaysSince(today, fileCreated[f]);

            var classified = ClassifyFile(flags, ageDays, unusedDays, retainedDays, options.MinAgeDays);
            if (classified == null) continue;

            var (category, grade) = classified.Value;

            // 62. Documents / Pictures / Videos / Desktop / 소스 / Git 안이면 날짜만으로 삭제를 권하지 않는다.
            if (grade == CleanupGrade.HighlyCleanable && IsUserSensitive(folderFlags))
                grade = CleanupGrade.NeedsReview;

            string name = new string(store.FileNameSpan(f));
            string filePath = store.GetFilePath(f);

            // ===== 보호 등급 우선 =====
            var verdict = ProtectionEvaluator.Evaluate(filePath, false, (uint)fileAttrs[f], flags);
            if (verdict.Level >= ProtectionLevel.P1)
            {
                protectedSize += size;
                protectedCount++;
                continue;
            }

            var level = ResolveP2(verdict, ageDays);
            int score = CleanupScorer.Score(new CleanupScorer.Input(
                size, Math.Max(ageDays, retainedDays / 2), unusedDays, accessReliable,
                (flags & (CategoryFlags.Cache | CategoryFlags.Temp)) != 0,
                verdict.RegenerableLocation));

            var tier = CleanupScorer.TierOf(score);
            if (tier == RecommendTier.None) continue;

            grade = GradeFor(level);
            candidates.Add(new CleanupCandidate
            {
                Kind = RowKind.File,
                Id = f,
                Name = name,
                FullPath = filePath,
                Size = size,
                Modified = NodeStore.ToDateTime(fileTimes[f]),
                Created = NodeStore.FromDay(fileCreated[f]),
                Accessed = accessReliable ? NodeStore.FromDay(accessDay) : default,
                Extension = store.Extensions.NameOf(fileExts[f]),
                Category = category,
                Grade = grade,
                Risk = CleanupCandidate.RiskOf(grade),
                Reason = FileReason(category, flags, ageDays, unusedDays, retainedDays, size, accessReliable),
                Protection = verdict,
                Level = level,
                Score = score,
                Tier = tier,
                AgeDays = ageDays,
                UnusedDays = unusedDays,
            });
            emittedFiles.Add(f);
        }

        // ---------------------------------------------------------- 3) 중복 가능 파일
        AddPossibleDuplicates(store, options, inherited, covered, emittedFiles, candidates, today, ct);

        // ---------------------------------------------------------- 4) 요약 / 정렬 / 상한
        var summaries = candidates
            .GroupBy(c => c.Category)
            .Select(g => new CleanupCategorySummary
            {
                Category = g.Key,
                Size = g.Sum(c => c.Size),
                Count = g.Count(),
            })
            .OrderByDescending(s => s.Size)
            .ToList();

        long totalSize = candidates.Sum(c => c.Size);
        int totalCount = candidates.Count;

        IEnumerable<CleanupCandidate> view = candidates;
        if (options.CategoryFilter is { } filter) view = view.Where(c => c.Category == filter);
        view = Sort(view, options.Sort);

        var final = view.Take(Math.Max(1, options.MaxResults)).ToList();

        return new CleanupReport
        {
            Candidates = final,
            Summaries = summaries,
            Developer = new DeveloperAnalyzer().Analyze(store, inherited, today, ct),
            TotalCandidates = totalCount,
            TotalCandidateSize = totalSize,
            ProtectedCount = protectedCount,
            ProtectedSize = protectedSize,
            AccessTimeReliable = accessReliable,
            Notice = accessReliable
                ? string.Empty
                : "이 시스템은 마지막 접근 시간(Last Access Time) 기록이 꺼져 있거나 신뢰할 수 없어 수정일 기준으로 판단했습니다.",
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    // ------------------------------------------------------------------ 분류

    private static (CleanupCategory, CleanupGrade)? ClassifyFolder(CategoryFlags own, CategoryFlags parentInherited)
    {
        // 같은 성격의 폴더가 중첩된 경우 가장 바깥 폴더만 후보로 삼는다.
        if ((own & CategoryFlags.Temp) != 0 && (parentInherited & CategoryFlags.Temp) == 0)
            return (CleanupCategory.Temp, CleanupGrade.HighlyCleanable);

        if ((own & CategoryFlags.Cache) != 0 && (parentInherited & CategoryFlags.Cache) == 0)
            return (CleanupCategory.Cache, CleanupGrade.HighlyCleanable);

        if ((own & (CategoryFlags.Build | CategoryFlags.VisualStudio)) != 0 &&
            (parentInherited & (CategoryFlags.Build | CategoryFlags.VisualStudio)) == 0)
            return (CleanupCategory.VisualStudioBuild, CleanupGrade.HighlyCleanable);

        if ((own & CategoryFlags.Log) != 0 && (parentInherited & CategoryFlags.Log) == 0)
            return (CleanupCategory.OldLog, CleanupGrade.HighlyCleanable);

        // node_modules / packages 는 재설치로 복구할 수 있지만 오프라인 환경이나
        // 잠긴 버전 문제가 있을 수 있어 "확인 필요" 로 둔다.
        if ((own & CategoryFlags.Package) != 0 && (parentInherited & CategoryFlags.Package) == 0)
            return (CleanupCategory.Package, CleanupGrade.NeedsReview);

        return null;
    }

    private static (CleanupCategory, CleanupGrade)? ClassifyFile(
        CategoryFlags flags, int ageDays, int unusedDays, int retainedDays, int minAgeDays)
    {
        // 수정일 / 접근일 / 생성일 중 하나라도 기준을 넘으면 "오래되었다"고 본다.
        // 생성일만 오래된 경우(계속 갱신되는 파일)는 아래 분류에서 등급이 내려간다.
        bool old = ageDays >= minAgeDays || unusedDays >= minAgeDays
                   || (retainedDays >= minAgeDays && ageDays >= minAgeDays / 2);

        // 재생성 가능한 산출물은 나이 조건을 완화한다(빌드/캐시/임시는 언제든 다시 만들어진다).
        if ((flags & (CategoryFlags.VisualStudio | CategoryFlags.Build)) != 0)
            return (CleanupCategory.VisualStudioBuild, CleanupGrade.HighlyCleanable);

        if ((flags & CategoryFlags.Temp) != 0)
            return (CleanupCategory.Temp, CleanupGrade.HighlyCleanable);

        if ((flags & CategoryFlags.Cache) != 0)
            return (CleanupCategory.Cache, CleanupGrade.HighlyCleanable);

        if ((flags & CategoryFlags.Log) != 0)
            return old
                ? (CleanupCategory.OldLog, CleanupGrade.HighlyCleanable)
                : (CleanupCategory.OldLog, CleanupGrade.NeedsReview);

        if (!old) return null;

        if ((flags & CategoryFlags.Installer) != 0)
            return ((flags & CategoryFlags.Download) != 0
                ? CleanupCategory.OldInstaller
                : CleanupCategory.OldInstaller,
                (flags & CategoryFlags.Download) != 0 ? CleanupGrade.HighlyCleanable : CleanupGrade.NeedsReview);

        if ((flags & CategoryFlags.Archive) != 0)
            return (CleanupCategory.OldArchive,
                (flags & CategoryFlags.Download) != 0 ? CleanupGrade.HighlyCleanable : CleanupGrade.NeedsReview);

        if ((flags & CategoryFlags.Download) != 0)
            return (CleanupCategory.OldDownload, CleanupGrade.NeedsReview);

        return (CleanupCategory.OldLargeFile, CleanupGrade.NeedsReview);
    }

    /// <summary>62. 사용자가 직접 만든 데이터일 가능성이 높은 영역인지.</summary>
    private static bool IsUserSensitive(CategoryFlags flags)
        => (flags & (CategoryFlags.UserData | CategoryFlags.SourceCode | CategoryFlags.Git)) != 0;

    // ------------------------------------------------------------------ 58. 이유 문장

    private static string FolderReason(CleanupCategory category, int ageDays, long size)
    {
        string age = ageDays > 0 ? RelativeTime.DescribeDuration(ageDays) : null! ?? string.Empty;
        string sizeText = SizeFormatter.Format(size);

        return category switch
        {
            CleanupCategory.Temp =>
                $"임시 파일 폴더입니다. 마지막 변경 후 {age} 지났고 {sizeText} 를 차지하고 있습니다.",
            CleanupCategory.Cache =>
                $"캐시 폴더입니다. 프로그램이 필요할 때 다시 생성합니다. 마지막 변경 후 {age}, {sizeText}.",
            CleanupCategory.VisualStudioBuild =>
                $"빌드 시 다시 생성되는 Visual Studio 산출물 폴더입니다. 마지막 빌드 후 {age}, {sizeText}.",
            CleanupCategory.OldLog =>
                $"로그 폴더입니다. 마지막 기록 후 {age} 지났고 {sizeText} 를 차지하고 있습니다.",
            CleanupCategory.Package =>
                $"패키지 매니저가 내려받은 의존성 폴더입니다({sizeText}). 다시 설치할 수 있지만 오프라인 환경에서는 주의가 필요합니다.",
            _ => $"{age} 동안 변경되지 않은 {sizeText} 폴더입니다.",
        };
    }

    private static string FileReason(CleanupCategory category, CategoryFlags flags,
        int ageDays, int unusedDays, int retainedDays, long size, bool accessReliable)
    {
        string sizeText = SizeFormatter.Format(size);
        string age = RelativeTime.DescribeDuration(ageDays);
        string retained = RelativeTime.DescribeDuration(retainedDays);
        string unusedNote = accessReliable && unusedDays > ageDays / 2 && unusedDays >= 30
            ? $" 마지막 사용은 {RelativeTime.DescribeDuration(unusedDays)} 전입니다."
            : string.Empty;

        string where = (flags & CategoryFlags.Download) != 0 ? "다운로드 폴더의 "
            : (flags & CategoryFlags.UserData) != 0 ? "사용자 문서 영역의 "
            : (flags & CategoryFlags.SourceCode) != 0 ? "소스 트리 안의 "
            : string.Empty;

        return category switch
        {
            CleanupCategory.VisualStudioBuild =>
                $"빌드 시 다시 생성되는 {sizeText} 개발 산출물입니다({age} 전 생성).",
            CleanupCategory.Temp =>
                $"임시 파일 영역의 {sizeText} 파일입니다. {age} 동안 변경되지 않았습니다.",
            CleanupCategory.Cache =>
                $"캐시 성격의 {sizeText} 파일입니다. 프로그램이 다시 만들 수 있습니다({age} 전 변경).",
            CleanupCategory.OldLog =>
                $"{age} 동안 수정되지 않은 {sizeText} 로그 파일입니다.{unusedNote}",
            // 다운로드/설치 파일은 "언제 받아 놓고 방치했는가"(생성일)가 가장 정확한 신호다.
            CleanupCategory.OldInstaller =>
                $"{where}{sizeText} 설치 파일이 {retained} 동안 그대로 남아 있습니다(마지막 변경 {age} 전).{unusedNote}",
            CleanupCategory.OldArchive =>
                $"{where}{sizeText} 압축 파일이 {retained} 동안 보관되어 있고 {age} 동안 변경되지 않았습니다.{unusedNote}",
            CleanupCategory.OldDownload =>
                $"다운로드 폴더에 {retained} 동안 남아 있는 {sizeText} 파일입니다.{unusedNote}",
            CleanupCategory.PossibleDuplicate =>
                $"같은 이름과 크기({sizeText})의 파일이 다른 위치에도 있습니다. 내용은 비교하지 않았습니다.",
            _ => $"{where}{sizeText} 파일이 {age} 동안 수정되지 않았습니다.{unusedNote}",
        };
    }

    // ------------------------------------------------------------------ 65. 정렬

    /// <summary>
    /// 기본 정렬은 "오래된 순"이 아니라 정리 효율이다.
    /// 확보 공간(로그 스케일) x 오래된 정도 x 재생성 가능 여부를 곱한다.
    /// 로그 스케일을 쓰는 이유: 선형으로 하면 초대형 파일 하나가 목록 전체를 지배해서
    /// "작지만 확실히 지워도 되는" 후보가 화면에서 밀려난다.
    /// </summary>
    /// <summary>
    /// 5. P2(정리 가능) 확정 조건.
    /// "알려진 Temp/Cache/Dump 위치" 만으로는 부족하고 "30일 이상 손대지 않았을 것"까지 만족해야 한다.
    /// 그래서 D:\DBBackup\important.tmp 같은 업무 데이터는 P2 로 내려오지 않는다.
    /// </summary>
    private static ProtectionLevel ResolveP2(ProtectionVerdict verdict, int ageDays)
        => verdict.RegenerableLocation && ageDays >= 30 ? ProtectionLevel.P2 : verdict.Level;

    /// <summary>7. 보호 등급 -> 화면 표시용 정리 등급. 두 축은 별개지만 표시는 하나로 합친다.</summary>
    private static CleanupGrade GradeFor(ProtectionLevel level) => level switch
    {
        ProtectionLevel.P2 => CleanupGrade.HighlyCleanable,
        ProtectionLevel.P3 => CleanupGrade.NeedsReview,
        _ => CleanupGrade.NotRecommended,
    };

    private static IEnumerable<CleanupCandidate> Sort(IEnumerable<CleanupCandidate> source, CleanupSort sort)
        => sort switch
        {
            CleanupSort.Largest => source.OrderByDescending(c => c.Size),
            CleanupSort.Oldest => source.OrderByDescending(c => c.AgeDays).ThenByDescending(c => c.Size),
            CleanupSort.LeastRecentlyUsed => source.OrderByDescending(c => c.UnusedDays).ThenByDescending(c => c.Size),
            CleanupSort.LowestRisk => source.OrderBy(c => c.Risk).ThenByDescending(c => c.Score),
            _ => source.OrderByDescending(c => c.Score),
        };

    // ------------------------------------------------------------------ 중복 가능 파일

    /// <summary>
    /// 57. "중복 가능 파일".
    /// 내용을 읽지 않는다(26. 파일 Metadata 만 사용). 이름 + 크기가 같은 파일을 묶어
    /// "중복일 수 있다"고만 알려 준다. 판단은 사용자가 한다.
    /// </summary>
    private static void AddPossibleDuplicates(NodeStore store, CleanupOptions options,
        uint[] inherited, bool[] covered, HashSet<int> alreadyEmitted,
        List<CleanupCandidate> candidates, int today, CancellationToken ct)
    {
        var fileSizes = store.FileSizesRaw;
        var fileParents = store.FileParentsRaw;
        var fileTimes = store.FileTimesRaw;
        var fileAttrs = store.FileAttributesRaw;
        int dirCount = store.DirectorySlots;
        int fileCount = store.FileSlots;

        var groups = new Dictionary<(string Name, long Size), List<int>>(256);

        for (int f = 0; f < fileCount; f++)
        {
            if ((f & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            long size = fileSizes[f];
            if (size < options.MinFileSize) continue;

            int parent = fileParents[f];
            if ((uint)parent >= (uint)dirCount) continue;
            if (((CategoryFlags)inherited[parent] & CategoryFlags.System) != 0) continue;
            if (covered[parent]) continue;

            string key = new string(store.FileNameSpan(f)).ToLowerInvariant();
            if (!groups.TryGetValue((key, size), out var list))
            {
                list = new List<int>(2);
                groups[(key, size)] = list;
            }
            list.Add(f);
        }

        foreach (var ((_, size), list) in groups)
        {
            if (list.Count < 2) continue;

            foreach (int f in list)
            {
                if (alreadyEmitted.Contains(f)) continue;

                int ageDays = DaysSince(today, NodeStore.ToDay(fileTimes[f]));
                string dupPath = store.GetFilePath(f);

                // 중복 후보에도 보호 등급을 먼저 적용한다.
                var dupVerdict = ProtectionEvaluator.Evaluate(dupPath, false, (uint)fileAttrs[f], CategoryFlags.None);
                if (dupVerdict.Level >= ProtectionLevel.P1) continue;

                int dupScore = CleanupScorer.Score(new CleanupScorer.Input(
                    size, ageDays, ageDays, false, false, dupVerdict.RegenerableLocation));
                var dupTier = CleanupScorer.TierOf(dupScore);
                if (dupTier == RecommendTier.None) continue;

                candidates.Add(new CleanupCandidate
                {
                    Kind = RowKind.File,
                    Id = f,
                    Name = new string(store.FileNameSpan(f)),
                    FullPath = dupPath,
                    Size = size,
                    Modified = NodeStore.ToDateTime(fileTimes[f]),
                    Category = CleanupCategory.PossibleDuplicate,
                    Grade = CleanupGrade.NeedsReview,
                    Risk = CleanupRisk.Medium,
                    Reason = $"이름과 크기({SizeFormatter.Format(size)})가 같은 파일이 {list.Count}곳에 있습니다. " +
                             "내용은 비교하지 않았으므로 직접 확인이 필요합니다.",
                    Protection = dupVerdict,
                    Level = dupVerdict.Level,
                    Score = dupScore,
                    Tier = dupTier,
                    AgeDays = ageDays,
                    UnusedDays = ageDays,
                });
            }
        }
    }

    // ------------------------------------------------------------------ 56. 접근 시간 신뢰도

    /// <summary>
    /// Windows 는 기본적으로 NTFS Last Access Time 갱신이 꺼져 있는 경우가 많다.
    /// 접근일이 수정일과 사실상 동일하다면 갱신이 안 되고 있다는 뜻이므로 단독 기준으로 쓰지 않는다.
    /// </summary>
    private static bool DetectAccessTimeReliability(long[] times, int[] accessedDays, int count)
    {
        if (count == 0) return false;

        int step = Math.Max(1, count / 20000);
        int sampled = 0, distinct = 0;

        for (int f = 0; f < count; f += step)
        {
            int modifiedDay = NodeStore.ToDay(times[f]);
            int accessDay = accessedDays[f];
            if (modifiedDay <= 0 || accessDay <= 0) continue;
            sampled++;
            if (accessDay > modifiedDay + 1) distinct++;
        }

        return sampled >= 50 && distinct / (double)sampled >= 0.05d;
    }

    private static int DaysSince(int today, int day) => day <= 0 ? 0 : Math.Max(0, today - day);
}
