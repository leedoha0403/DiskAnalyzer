using System.Diagnostics;
using DiskAnalyzer.Core.Services;
using Xunit;

namespace DiskAnalyzer.Tests;

public class ProcessIgnoreAndLockTests
{
    static ProcessIgnoreAndLockTests()
    {
        // 사용자의 실제 무시 목록을 건드리지 않게, 이 형식이 처음 쓰이기 전에 임시 경로로 돌린다.
        Environment.SetEnvironmentVariable("DISKANALYZER_PROCESS_IGNORE",
            Path.Combine(Path.GetTempPath(), $"da-ignore-{Guid.NewGuid():N}.json"));
    }

    [Fact]
    public void IgnoreList_AddRemove_IsCaseAndExtensionInsensitive()
    {
        int changes = 0;
        void OnChanged() => changes++;
        ProcessIgnoreList.Changed += OnChanged;
        try
        {
            Assert.True(ProcessIgnoreList.Add("Node.exe"));
            Assert.False(ProcessIgnoreList.Add("node"));
            Assert.True(ProcessIgnoreList.Contains("NODE"));
            Assert.Contains("Node", ProcessIgnoreList.Names, StringComparer.OrdinalIgnoreCase);

            Assert.True(ProcessIgnoreList.Remove("node"));
            Assert.False(ProcessIgnoreList.Contains("node"));
            Assert.Equal(2, changes);
        }
        finally
        {
            ProcessIgnoreList.Changed -= OnChanged;
            ProcessIgnoreList.Clear();
        }
    }

    [Fact]
    public void FileLockFinder_FindsThisProcess_HoldingAnExclusiveFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"da-lock-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x");
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var lockers = FileLockFinder.Find(new[] { path });
            Assert.Contains(lockers, l => l.Pid == Environment.ProcessId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileLockFinder_UnlockedFile_HasNoLockers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"da-free-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x");
        try
        {
            Assert.DoesNotContain(FileLockFinder.Find(new[] { path }), l => l.Pid == Environment.ProcessId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
