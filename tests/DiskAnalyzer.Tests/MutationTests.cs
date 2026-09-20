using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 14. 삭제 / 새로고침을 전체 재스캔 없이 데이터 모델에 반영하는 로직 검증.
/// </summary>
public class MutationTests
{
    private const long Mb = 1024L * 1024L;

    /// <summary>
    /// C:\
    ///  ├ A (a1=100MB, a2=200MB)
    ///  │  └ B (b1=400MB)
    ///  └ C (c1=50MB)
    /// </summary>
    private static NodeStore Build()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.SetDirectory(2, 1, "B", 0, 0);
        store.SetDirectory(3, NodeStore.RootId, "C", 0, 0);

        store.AddFile(1, "a1.txt", 100 * Mb, 0, 0);
        store.AddFile(1, "a2.log", 200 * Mb, 0, 0);
        store.AddFile(2, "b1.pdb", 400 * Mb, 0, 0);
        store.AddFile(3, "c1.dat", 50 * Mb, 0, 0);

        store.Seal();
        return store;
    }

    [Fact]
    public void Removing_A_File_Updates_Sizes_Up_The_Tree()
    {
        var store = Build();
        Assert.Equal(750 * Mb, store.TotalSize);

        var result = store.RemoveNodes(new[] { (RowKind.File, 2) });   // b1.pdb

        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(400 * Mb, result.RemovedBytes);
        Assert.Equal(350 * Mb, store.TotalSize);
        Assert.Equal(300 * Mb, store.GetDirectorySize(1));   // A
        Assert.Equal(0, store.GetDirectorySize(2));          // B
        Assert.Equal(3, store.FileCount);
    }

    [Fact]
    public void Removing_A_Folder_Removes_Its_Whole_Subtree()
    {
        var store = Build();
        var result = store.RemoveNodes(new[] { (RowKind.Directory, 1) });   // A (B 포함)

        Assert.Equal(3, result.RemovedFiles);        // a1, a2, b1
        Assert.Equal(2, result.RemovedDirectories);  // A, B
        Assert.Equal(50 * Mb, store.TotalSize);    // C 만 남는다
        Assert.True(store.IsDirectoryDeleted(1));
        Assert.True(store.IsDirectoryDeleted(2));
        Assert.False(store.IsDirectoryDeleted(3));
    }

    [Fact]
    public void Deleted_Nodes_Disappear_From_Every_View()
    {
        var store = Build();
        store.RemoveNodes(new[] { (RowKind.Directory, 1) });

        Assert.DoesNotContain(store.GetChildren(NodeStore.RootId, includeFiles: true), r => r.Name == "A");
        Assert.DoesNotContain(store.Search("b1"), r => r.Name == "b1.pdb");
        Assert.DoesNotContain(store.GetTopFiles(100), r => r.Name == "a2.log");
        Assert.DoesNotContain(store.GetFilesByExtension(".pdb"), r => r.Name == "b1.pdb");
    }

    [Fact]
    public void Extension_Stats_Are_Updated_On_Delete()
    {
        var store = Build();
        Assert.Contains(store.GetExtensionRows(), e => e.Extension == ".pdb" && e.Size == 400 * Mb);

        store.RemoveNodes(new[] { (RowKind.File, 2) });

        var pdb = store.GetExtensionRows().FirstOrDefault(e => e.Extension == ".pdb");
        Assert.True(pdb == null || pdb.Size == 0);
        Assert.Equal(350 * Mb, store.GetExtensionRows().Sum(e => e.Size));
    }

    [Fact]
    public void TopFiles_Are_Rebuilt_After_Delete()
    {
        var store = Build();
        Assert.Equal("b1.pdb", store.GetTopFiles(1)[0].Name);

        store.RemoveNodes(new[] { (RowKind.File, 2) });

        var top = store.GetTopFiles(10);
        Assert.Equal("a2.log", top[0].Name);
        Assert.Equal(3, top.Count);
    }

    [Fact]
    public void Verification_Applies_Missing_And_Resized()
    {
        var store = Build();

        var outcomes = new List<VerifyOutcome>
        {
            // a1.txt 는 외부에서 삭제됨
            new() { Kind = RowKind.File, Id = 0, Path = @"C:\A\a1.txt",
                    State = VerifyState.Missing, OldSize = 100 * Mb },
            // a2.log 는 200MB -> 500MB 로 커짐
            new() { Kind = RowKind.File, Id = 1, Path = @"C:\A\a2.log",
                    State = VerifyState.Changed, OldSize = 200 * Mb, NewSize = 500 * Mb },
        };

        var mutation = store.ApplyVerification(outcomes);

        Assert.Equal(1, mutation.RemovedFiles);
        Assert.Equal(1, mutation.ResizedFiles);
        Assert.Equal(300 * Mb, mutation.ResizedDelta);

        // 750 - 100(삭제) + 300(증가) = 950
        Assert.Equal(950 * Mb, store.TotalSize);
        Assert.Equal(900 * Mb, store.GetDirectorySize(1));
        Assert.Equal(3, store.FileCount);
    }

    [Fact]
    public void Repeated_Delete_Is_Idempotent()
    {
        var store = Build();
        store.RemoveNodes(new[] { (RowKind.File, 2) });
        var second = store.RemoveNodes(new[] { (RowKind.File, 2) });

        Assert.Equal(0, second.RemovedFiles);
        Assert.Equal(350 * Mb, store.TotalSize);
    }

    [Fact]
    public void FindNodes_Resolves_Files_And_Folders_By_Path_Ignoring_Case()
    {
        var store = Build();

        var found = store.FindNodes(new[] { @"C:\A\B\b1.pdb", @"c:\c", @"C:\A\a1.txt" });

        Assert.Contains((RowKind.File, 2), found);        // b1.pdb
        Assert.Contains((RowKind.Directory, 3), found);   // C
        Assert.Contains((RowKind.File, 0), found);        // a1.txt
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void FindNodes_Skips_Unknown_OutOfScope_Root_And_Already_Removed()
    {
        var store = Build();
        store.RemoveNodes(new[] { (RowKind.File, 3) });   // c1.dat

        var found = store.FindNodes(new[]
        {
            @"C:\A\nope.txt", @"C:\Nope\x.txt", @"D:\A\a1.txt", @"C:\", @"C:\C\c1.dat", @"C:\AB\a1.txt", "",
        });

        Assert.Empty(found);
    }

    [Fact]
    public void Moved_Sources_Are_Removed_From_The_Store_Through_FindNodes()
    {
        var store = Build();

        store.RemoveNodes(store.FindNodes(new[] { @"C:\A\B", @"C:\A\a2.log" }));

        Assert.Equal(100 * Mb + 50 * Mb, store.TotalSize);   // a1 + c1 만 남는다
        Assert.Equal(2, store.FileCount);
    }

    [Fact]
    public void Deleting_Everything_Leaves_Zero()
    {
        var store = Build();
        store.RemoveNodes(new[]
        {
            (RowKind.Directory, 1),
            (RowKind.Directory, 3),
        });

        Assert.Equal(0, store.TotalSize);
        Assert.Equal(0, store.FileCount);
        Assert.Empty(store.GetChildren(NodeStore.RootId, includeFiles: true));
    }

    [Fact]
    public async Task Verifier_Detects_Real_Filesystem_State()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DAVerify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string kept = Path.Combine(dir, "kept.bin");
            string gone = Path.Combine(dir, "gone.bin");
            File.WriteAllBytes(kept, new byte[2048]);

            var targets = new[]
            {
                new FileVerifier.Target(RowKind.File, 0, kept, 2048),
                new FileVerifier.Target(RowKind.File, 1, gone, 1024),
                new FileVerifier.Target(RowKind.Directory, 2, dir, 0),
            };

            var outcomes = await FileVerifier.VerifyAsync(targets);

            Assert.Equal(VerifyState.Unchanged, outcomes.Single(o => o.Id == 0).State);
            Assert.Equal(VerifyState.Missing, outcomes.Single(o => o.Id == 1).State);
            Assert.Equal(VerifyState.Unchanged, outcomes.Single(o => o.Id == 2).State);

            // 크기가 달라지면 Changed 로 보고한다.
            var resized = await FileVerifier.VerifyAsync(
                new[] { new FileVerifier.Target(RowKind.File, 0, kept, 999) });
            Assert.Equal(VerifyState.Changed, resized[0].State);
            Assert.Equal(2048, resized[0].NewSize);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Deletion_Skips_Already_Missing_And_Continues()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DADelete_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string real = Path.Combine(dir, "real.bin");
            File.WriteAllBytes(real, new byte[4096]);

            var requests = new[]
            {
                new DeletionRequest { Kind = RowKind.File, Id = 0, Path = Path.Combine(dir, "ghost.bin"), Size = 10 },
                new DeletionRequest { Kind = RowKind.File, Id = 1, Path = real, Size = 4096 },
            };

            var summary = await DeletionService.DeleteAsync(requests, permanent: true);

            // 없는 파일 때문에 전체가 중단되지 않는다.
            Assert.Equal(1, summary.AlreadyGoneCount);
            Assert.Equal(1, summary.SucceededCount);
            Assert.False(File.Exists(real));
            Assert.Equal(2, summary.RemovedNodes.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Deletion_Refuses_P0()
    {
        // 9. 삭제 직전 재검증에서 P0 이면 실제 삭제를 시도하지 않는다.
        var requests = new[]
        {
            new DeletionRequest { Kind = RowKind.File, Id = 0, Path = @"C:\Windows\System32\kernel32.dll", Size = 1 },
        };

        var summary = await DeletionService.DeleteAsync(requests, permanent: true);
        var outcome = summary.Outcomes.Single();

        Assert.True(outcome.Kind is DeletionOutcomeKind.Protected or DeletionOutcomeKind.AlreadyGone);
        Assert.True(File.Exists(@"C:\Windows\System32\kernel32.dll"));
    }
}

/// <summary>3/4. 정리 후보 그룹화 검증.</summary>
public class CleanupGroupingTests
{
    private const long Mb = 1024L * 1024L;

    private static CleanupCandidate Make(string path, long size, CleanupCategory cat,
        CleanupGrade grade = CleanupGrade.HighlyCleanable, ProtectionLevel level = ProtectionLevel.P2)
        => new()
        {
            Kind = RowKind.File,
            Id = path.GetHashCode(),
            Name = Path.GetFileName(path),
            FullPath = path,
            Extension = Path.GetExtension(path),
            Size = size,
            Modified = new DateTime(2025, 1, 1),
            Category = cat,
            Grade = grade,
            Risk = CleanupCandidate.RiskOf(grade),
            Reason = "테스트",
            Level = level,
        };

    private static List<CleanupCandidate> Sample() => new()
    {
        Make(@"C:\Users\me\AppData\Local\Temp\a.tmp", 10 * Mb, CleanupCategory.Temp),
        Make(@"C:\Users\me\AppData\Local\Temp\b.tmp", 8 * Mb, CleanupCategory.Temp),
        Make(@"C:\Work\ProjectA\x64\Debug\app.pdb", 40 * Mb, CleanupCategory.VisualStudioBuild),
        Make(@"C:\Users\me\Downloads\setup.msi", 30 * Mb, CleanupCategory.OldInstaller,
             CleanupGrade.NeedsReview, ProtectionLevel.P3),
    };

    [Fact]
    public void ByPath_Groups_By_Parent_Folder()
    {
        var groups = CleanupGrouper.ByPath(Sample());

        var temp = groups.Single(g => g.Title.EndsWith(@"Local\Temp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(18 * Mb, temp.Size);
        Assert.Equal(2, temp.Items.Count);
        Assert.Equal(2, temp.FileCount);

        // 크기 내림차순
        Assert.Equal(40 * Mb, groups[0].Size);
    }

    [Fact]
    public void Group_Selection_Is_Tri_State()
    {
        var groups = CleanupGrouper.ByPath(Sample());
        var temp = groups.Single(g => g.Items.Count == 2);

        Assert.False(temp.IsSelected);

        temp.IsSelected = true;
        Assert.True(temp.IsSelected);
        Assert.All(temp.Items, i => Assert.True(i.IsSelected));

        // 5. 그룹 안에서 개별 제외 -> 부분 선택
        temp.Items[0].IsSelected = false;
        Assert.Null(temp.IsSelected);
    }

    [Fact]
    public void Group_Grade_Is_The_Most_Conservative()
    {
        var items = new List<CleanupCandidate>
        {
            Make(@"C:\X\a.tmp", Mb, CleanupCategory.Temp),
            Make(@"C:\X\b.zip", Mb, CleanupCategory.OldArchive, CleanupGrade.NeedsReview, ProtectionLevel.P3),
        };

        var group = CleanupGrouper.ByPath(items).Single();
        Assert.Equal(CleanupGrade.NeedsReview, group.Grade);
    }

    [Fact]
    public void ByType_Groups_By_Extension()
    {
        var groups = CleanupGrouper.ByType(Sample());
        Assert.Contains(groups, g => g.Title == ".tmp" && g.Items.Count == 2);
        Assert.Contains(groups, g => g.Title == ".pdb");
    }

    [Fact]
    public void ByReason_Groups_By_Category_And_Grade()
    {
        var groups = CleanupGrouper.ByReason(Sample());
        Assert.Contains(groups, g => g.Title.Contains("임시 파일"));
        Assert.Contains(groups, g => g.Title.Contains("오래된 설치 파일"));
    }

    [Fact]
    public void Tree_Aggregates_Sizes_Upward()
    {
        var roots = CleanupGrouper.BuildTree(Sample());

        // 모든 후보가 C:\ 아래 있으므로 루트는 하나
        var root = roots.Single();
        Assert.Equal(88 * Mb, root.Size);          // 10+8+40+30
        Assert.Equal(4, root.CandidateCount);

        // 트리 어딘가에 Temp 노드가 있고 그 크기는 18MB
        var temp = Find(root, "Temp");
        Assert.NotNull(temp);
        Assert.Equal(18 * Mb, temp!.Size);
    }

    [Fact]
    public void Tree_Selection_Cascades_To_Descendants()
    {
        var roots = CleanupGrouper.BuildTree(Sample());
        var root = roots.Single();

        root.IsSelected = true;
        Assert.True(root.IsSelected);

        var temp = Find(root, "Temp")!;
        Assert.All(temp.Items, i => Assert.True(i.IsSelected));

        temp.Items[0].IsSelected = false;
        Assert.Null(root.IsSelected);   // 일부만 선택
    }

    private static CleanupPathNode? Find(CleanupPathNode node, string name)
    {
        if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var c in node.Children)
        {
            var hit = Find(c, name);
            if (hit != null) return hit;
        }
        return null;
    }
}
