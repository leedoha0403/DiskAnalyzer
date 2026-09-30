using System.Diagnostics;
using DiskAnalyzer.Core.Services;
using Xunit;

namespace DiskAnalyzer.Tests;

public class ProcessCleanerTests
{
    /// <summary>cmd → (ping 이 자식) 구조의 프로세스 트리를 띄운다.</summary>
    private static Process StartTree()
        => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

    private static ProcessInfo? WaitFor(Func<ProcessInfo, bool> match)
    {
        for (int i = 0; i < 30; i++)
        {
            var hit = ProcessCleanerService.ListProcesses().FirstOrDefault(match);
            if (hit != null) return hit;
            Thread.Sleep(200);
        }
        return null;
    }

    [Fact]
    public void List_ReportsParentAndRootApp()
    {
        using var cmd = StartTree();
        try
        {
            var child = WaitFor(p => p.Name.Equals("PING", StringComparison.OrdinalIgnoreCase) && p.ParentPid == cmd.Id);
            Assert.NotNull(child);
            Assert.Equal("cmd", child!.RootApp, ignoreCase: true);
            Assert.Contains("cmd", child.Ancestors, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ProcessCleanerService.TryKill(cmd.Id);
        }
    }

    [Fact]
    public void TryKill_EndsWholeTree()
    {
        using var cmd = StartTree();
        var child = WaitFor(p => p.Name.Equals("PING", StringComparison.OrdinalIgnoreCase) && p.ParentPid == cmd.Id);
        Assert.NotNull(child);

        Assert.Equal(KillOutcome.Success, ProcessCleanerService.TryKill(cmd.Id));
        Assert.Equal(KillOutcome.NotFound, ProcessCleanerService.TryKill(child!.Pid));
    }

    [Fact]
    public void TryKill_MissingPid_IsNotFound()
        => Assert.Equal(KillOutcome.NotFound, ProcessCleanerService.TryKill(int.MaxValue - 1));
}
