using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>53~66. 정리 추천 엔진 검증.</summary>
public class CleanupTests
{
    private const long Mb = 1024L * 1024L;

    private static long DaysAgo(int days)
        => DateTime.UtcNow.AddDays(-days).ToFileTimeUtc();

    /// <summary>
    /// C:\
    ///  ├ Windows\System32        (보호 경로, 큰 파일 있음)
    ///  ├ Users\me\Downloads      (오래된 설치 파일 / ZIP)
    ///  ├ Users\me\Documents      (오래된 대용량 문서)
    ///  ├ Work\ProjectA\x64\Debug (빌드 산출물)
    ///  ├ Work\ProjectA\.git      (Git)
    ///  └ Temp                    (임시 파일)
    /// </summary>
    private static NodeStore BuildTree()
    {
        var store = new NodeStore("C:\\");
        int id = 1;

        int windows = id++; store.SetDirectory(windows, NodeStore.RootId, "Windows", DaysAgo(10), 0);
        int system32 = id++; store.SetDirectory(system32, windows, "System32", DaysAgo(10), 0);

        int users = id++; store.SetDirectory(users, NodeStore.RootId, "Users", DaysAgo(10), 0);
        int me = id++; store.SetDirectory(me, users, "me", DaysAgo(10), 0);
        int downloads = id++; store.SetDirectory(downloads, me, "Downloads", DaysAgo(400), 0);
        int documents = id++; store.SetDirectory(documents, me, "Documents", DaysAgo(400), 0);

        int work = id++; store.SetDirectory(work, NodeStore.RootId, "Work", DaysAgo(30), 0);
        int projectA = id++; store.SetDirectory(projectA, work, "ProjectA", DaysAgo(30), 0);
        int x64 = id++; store.SetDirectory(x64, projectA, "x64", DaysAgo(500), 0);
        int debug = id++; store.SetDirectory(debug, x64, "Debug", DaysAgo(500), 0);
        int git = id++; store.SetDirectory(git, projectA, ".git", DaysAgo(30), 0);

        int temp = id++; store.SetDirectory(temp, NodeStore.RootId, "Temp", DaysAgo(200), 0);

        store.AddFile(system32, "huge.dll", 900 * Mb, DaysAgo(900), DaysAgo(900), DaysAgo(900), 0);
        store.AddFile(downloads, "setup_old.msi", 800 * Mb, DaysAgo(500), DaysAgo(500), DaysAgo(500), 0);
        store.AddFile(downloads, "archive.zip", 700 * Mb, DaysAgo(500), DaysAgo(500), DaysAgo(500), 0);
        store.AddFile(documents, "thesis.mp4", 600 * Mb, DaysAgo(800), DaysAgo(800), DaysAgo(800), 0);
        store.AddFile(debug, "app.pdb", 500 * Mb, DaysAgo(500), DaysAgo(500), DaysAgo(500), 0);
        store.AddFile(debug, "app.obj", 400 * Mb, DaysAgo(500), DaysAgo(500), DaysAgo(500), 0);
        store.AddFile(git, "pack.pack", 300 * Mb, DaysAgo(30), DaysAgo(30), DaysAgo(30), 0);
        store.AddFile(temp, "scratch.tmp", 250 * Mb, DaysAgo(200), DaysAgo(200), DaysAgo(200), 0);
        store.AddFile(work, "game_2024.log", 900 * Mb, DaysAgo(700), DaysAgo(700), DaysAgo(700), 0);
        store.AddFile(work, "recent.log", 120 * Mb, DaysAgo(3), DaysAgo(3), DaysAgo(3), 0);

        store.Seal();
        return store;
    }

    private static CleanupReport Analyze(int minAgeDays = 180, long minSize = 100 * Mb)
        => new CleanupAnalyzer().Analyze(BuildTree(), new CleanupOptions
        {
            MinAgeDays = minAgeDays,
            MinFileSize = minSize,
            MinFolderSize = minSize,
        });

    [Fact]
    public void Every_Candidate_Has_A_Reason()
    {
        var report = Analyze();
        Assert.NotEmpty(report.Candidates);
        Assert.All(report.Candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
    }

    [Fact]
    public void Protected_Paths_Are_Excluded_But_Reported()
    {
        var report = Analyze();

        Assert.DoesNotContain(report.Candidates, c => c.FullPath.Contains("System32", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Candidates, c => c.FullPath.Contains(@"C:\Windows", StringComparison.OrdinalIgnoreCase));

        // 61. 정보는 제공한다.
        Assert.Equal(1, report.ProtectedCount);
        Assert.Equal(900 * Mb, report.ProtectedSize);
    }

    [Fact]
    public void Git_Folder_Is_Never_Recommended()
    {
        var report = Analyze();
        Assert.DoesNotContain(report.Candidates, c => c.FullPath.Contains(@"\.git", StringComparison.OrdinalIgnoreCase));

        // 63. 대신 크기 정보는 개발자 분석에 나온다.
        Assert.Contains(report.Developer.GitRepos, g => g.RepoName == "ProjectA" && g.GitSize == 300 * Mb);
    }

    [Fact]
    public void BuildOutput_Is_HighlyCleanable_And_Folder_Level()
    {
        var report = Analyze();

        var build = report.Candidates.Single(c =>
            c.Category == CleanupCategory.VisualStudioBuild && c.IsDirectory);

        Assert.EndsWith(@"ProjectA\x64", build.FullPath);        // 가장 바깥 빌드 폴더 하나만
        Assert.Equal(900 * Mb, build.Size);                       // pdb + obj
        Assert.Equal(CleanupGrade.HighlyCleanable, build.Grade);
        Assert.Equal(CleanupRisk.Low, build.Risk);

        // 폴더로 잡혔으므로 하위 파일은 중복으로 올라오지 않는다.
        Assert.DoesNotContain(report.Candidates, c => c.Name == "app.pdb");
    }

    [Fact]
    public void UserData_Is_Downgraded_To_NeedsReview()
    {
        var report = Analyze();
        var doc = report.Candidates.Single(c => c.Name == "thesis.mp4");

        Assert.Equal(CleanupGrade.NeedsReview, doc.Grade);
        Assert.Equal(CleanupRisk.Medium, doc.Risk);
    }

    [Fact]
    public void Old_Downloads_Are_Classified()
    {
        var report = Analyze();

        var installer = report.Candidates.Single(c => c.Name == "setup_old.msi");
        Assert.Equal(CleanupCategory.OldInstaller, installer.Category);

        var archive = report.Candidates.Single(c => c.Name == "archive.zip");
        Assert.Equal(CleanupCategory.OldArchive, archive.Category);
    }

    [Fact]
    public void Old_Log_Is_Recommended_But_Recent_Log_Is_Not()
    {
        var report = Analyze();

        var old = report.Candidates.Single(c => c.Name == "game_2024.log");
        Assert.Equal(CleanupCategory.OldLog, old.Category);
        Assert.Equal(CleanupGrade.HighlyCleanable, old.Grade);
        Assert.Equal(ProtectionLevel.P2, old.Level);       // 재생성 가능 + 30일 이상 -> P2
        Assert.Contains("로그", old.Reason);

        // 3일 전 로그는 보호 등급으로는 삭제 가능(P3)하지만 정리할 가치가 낮아 목록에 오르지 않는다.
        Assert.DoesNotContain(report.Candidates, c => c.Name == "recent.log");
    }

    [Fact]
    public void Temp_Folder_Is_Recommended()
    {
        var report = Analyze();
        var temp = report.Candidates.Single(c => c.Category == CleanupCategory.Temp && c.IsDirectory);

        Assert.Equal(@"C:\Temp", temp.FullPath);
        Assert.Equal(CleanupGrade.HighlyCleanable, temp.Grade);
    }

    [Fact]
    public void Default_Sort_Prefers_Regenerable_Over_Merely_Big()
    {
        var report = Analyze();

        int buildRank = report.Candidates.ToList().FindIndex(c => c.Category == CleanupCategory.VisualStudioBuild);
        int docRank = report.Candidates.ToList().FindIndex(c => c.Name == "thesis.mp4");

        // 65. 크기만 보면 비슷하지만 재생성 가능한 쪽이 위로 온다.
        Assert.True(buildRank < docRank);
    }

    [Fact]
    public void Sort_By_Largest_Puts_Biggest_First()
    {
        var store = BuildTree();
        var report = new CleanupAnalyzer().Analyze(store, new CleanupOptions
        {
            MinAgeDays = 180,
            MinFileSize = 100 * Mb,
            MinFolderSize = 100 * Mb,
            Sort = CleanupSort.Largest,
        });

        for (int i = 1; i < report.Candidates.Count; i++)
            Assert.True(report.Candidates[i - 1].Size >= report.Candidates[i].Size);
    }

    [Fact]
    public void Category_Filter_Limits_Results()
    {
        var store = BuildTree();
        var report = new CleanupAnalyzer().Analyze(store, new CleanupOptions
        {
            MinAgeDays = 180,
            MinFileSize = 100 * Mb,
            MinFolderSize = 100 * Mb,
            CategoryFilter = CleanupCategory.OldLog,
        });

        Assert.NotEmpty(report.Candidates);
        Assert.All(report.Candidates, c => Assert.Equal(CleanupCategory.OldLog, c.Category));
    }

    [Fact]
    public void Developer_Report_Groups_Logs_By_Age()
    {
        var report = Analyze();
        var buckets = report.Developer.LogBuckets;

        Assert.Equal(5, buckets.Count);
        Assert.Equal(120 * Mb, buckets[0].Size);      // 최근 7일: recent.log
        Assert.Equal(900 * Mb, buckets[4].Size);      // 180일 이상: game_2024.log
        Assert.Equal(1020 * Mb, report.Developer.TotalLogSize);
        Assert.Equal(900 * Mb, report.Developer.OldLogSize);
    }

    [Fact]
    public void Developer_Report_Aggregates_Build_Output_Per_Project()
    {
        var report = Analyze();
        var project = report.Developer.Projects.Single();

        Assert.Equal("ProjectA", project.ProjectName);
        Assert.Equal(900 * Mb, project.BuildOutputSize);
        Assert.Equal(900 * Mb, report.Developer.TotalBuildOutput);
    }

    [Fact]
    public void Age_Filter_Is_Respected_For_UserFiles()
    {
        // 2년 이상 기준이면 500일짜리 다운로드는 후보에서 빠진다(재생성 가능 산출물은 예외).
        var report = Analyze(minAgeDays: 730);

        Assert.DoesNotContain(report.Candidates, c => c.Name == "setup_old.msi");
        Assert.Contains(report.Candidates, c => c.Category == CleanupCategory.VisualStudioBuild);
    }

    [Fact]
    public void Creation_Date_Is_Used_For_Downloads()
    {
        // 53. 생성일도 신호로 쓴다. 최근에 건드렸지만 2년 전에 받아 둔 설치 파일.
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "Users", DaysAgo(10), 0);
        store.SetDirectory(2, 1, "me", DaysAgo(10), 0);
        store.SetDirectory(3, 2, "Downloads", DaysAgo(10), 0);
        store.AddFile(3, "old_setup.msi", 900 * Mb,
            modifiedTime: DaysAgo(120), createdTime: DaysAgo(800), accessedTime: DaysAgo(120), attributes: 0);
        store.Seal();

        var report = new CleanupAnalyzer().Analyze(store, new CleanupOptions
        {
            MinAgeDays = 180,
            MinFileSize = 100 * Mb,
            MinFolderSize = 100 * Mb,
        });

        var candidate = report.Candidates.Single();
        Assert.Equal(CleanupCategory.OldInstaller, candidate.Category);
        Assert.Contains("2년", candidate.Reason);   // 보관 기간이 이유에 드러난다
    }

    [Fact]
    public void Summaries_Cover_All_Candidates()
    {
        var report = Analyze();

        Assert.Equal(report.TotalCandidates, report.Summaries.Sum(s => s.Count));
        Assert.Equal(report.TotalCandidateSize, report.Summaries.Sum(s => s.Size));
    }

    [Fact]
    public void Possible_Duplicates_Are_Flagged_Without_Reading_Content()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", DaysAgo(600), 0);
        store.SetDirectory(2, NodeStore.RootId, "B", DaysAgo(600), 0);
        store.AddFile(1, "movie.mkv", 800 * Mb, DaysAgo(600), DaysAgo(600), DaysAgo(600), 0);
        store.AddFile(2, "movie.mkv", 800 * Mb, DaysAgo(600), DaysAgo(600), DaysAgo(600), 0);
        store.Seal();

        // "2년 이상" 기준에서는 600일짜리 파일이 일반 후보로는 안 잡히지만,
        // 같은 이름+크기가 두 곳에 있으므로 중복 가능 후보로는 올라온다.
        var report = new CleanupAnalyzer().Analyze(store, new CleanupOptions
        {
            MinAgeDays = 730,
            MinFileSize = 100 * Mb,
            MinFolderSize = 100 * Mb,
        });

        var dups = report.Candidates.Where(c => c.Category == CleanupCategory.PossibleDuplicate).ToList();
        Assert.Equal(2, dups.Count);
        Assert.All(dups, d => Assert.Contains("내용은 비교하지 않았", d.Reason));
        Assert.All(dups, d => Assert.Equal(CleanupGrade.NeedsReview, d.Grade));
        Assert.All(dups, d => Assert.Equal(ProtectionLevel.P3, d.Level));
    }

    [Fact]
    public void Recently_Created_Files_Are_Not_Recommended()
    {
        // 6. "오늘 생성한 ZIP" 은 P3(삭제 가능)지만 정리 추천 점수가 낮아 목록에 오르지 않는다.
        //    보호 등급과 정리 추천은 별개 축이라는 것을 확인한다.
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "Project", DaysAgo(2), 0);
        store.AddFile(1, "release_backup.zip", 900 * Mb, DaysAgo(2), DaysAgo(2), DaysAgo(2), 0);
        store.Seal();

        var report = new CleanupAnalyzer().Analyze(store, new CleanupOptions
        {
            MinAgeDays = 180,
            MinFileSize = 100 * Mb,
            MinFolderSize = 100 * Mb,
        });

        Assert.DoesNotContain(report.Candidates, c => c.Name == "release_backup.zip");

        // 그래도 "삭제 불가"는 아니다.
        var verdict = ProtectionEvaluator.Evaluate(@"C:\Project\release_backup.zip", false, 0, CategoryFlags.Archive);
        Assert.Equal(ProtectionLevel.P3, verdict.Level);
    }
}
