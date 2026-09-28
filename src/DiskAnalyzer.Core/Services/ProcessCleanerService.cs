using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskAnalyzer.Core.Services;

public sealed record ProcessInfo(
    int Pid,
    string Name,
    string WindowTitle,
    bool Responding,
    long MemoryBytes,
    DateTime? StartTime,
    double CpuPercent,
    double DiskBytesPerSecond);

public enum KillOutcome { Success, AccessDenied, NotFound, Error }

/// <summary>
/// 게임 등이 죽다 만 채로 남는 좀비/응답 없음 프로세스를 정리한다.
/// 앱은 asInvoker 로 뜨므로 다른 사용자 소유이거나 보호된 프로세스는 TryKill 이 AccessDenied 를 돌려준다 —
/// 그럴 때만 <see cref="TryKillElevated"/> 로 같은 exe 를 관리자 권한으로 다시 띄워 그 프로세스 하나만 죽이고 빠지게 한다
/// (App.xaml.cs 의 --kill-pid 처리와 짝을 이룬다). 항상 관리자 권한을 요구하지 않는 이유는 app.manifest 참고.
/// </summary>
public static class ProcessCleanerService
{
    /// <summary>
    /// CPU%·디스크 속도는 누적값(총 CPU 시간, 총 입출력 바이트)의 두 시점 사이 차이로만 계산할 수 있다.
    /// 그래서 직전 호출의 누적값을 여기에 기억해 뒀다가 다음 호출에서 델타를 낸다 - ViewModel 이
    /// 창을 열어 둔 동안 주기적으로 ListProcesses 를 다시 부르는 것을 전제로 한다(작업 관리자와 같은 방식).
    /// 창이 닫혀 한동안 안 불리다 다시 열리면 그 첫 호출은 기준점이 없어 0%로 보인다.
    /// </summary>
    private static readonly Dictionary<int, (TimeSpan Cpu, long Io, DateTime At)> PrevSamples = new();

    public static IReadOnlyList<ProcessInfo> ListProcesses(bool notRespondingOnly)
    {
        int selfPid = Environment.ProcessId;
        var now = DateTime.UtcNow;
        var result = new List<ProcessInfo>();
        var current = new Dictionary<int, (TimeSpan Cpu, long Io, DateTime At)>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == selfPid) continue;

                bool responding = SafeResponding(p);
                if (notRespondingOnly && responding) continue;

                var cpuTime = SafeCpuTime(p);
                long io = SafeIoBytes(p);
                current[p.Id] = (cpuTime, io, now);

                double cpuPercent = 0, diskRate = 0;
                if (PrevSamples.TryGetValue(p.Id, out var prev))
                {
                    double elapsed = (now - prev.At).TotalSeconds;
                    if (elapsed > 0.05)
                    {
                        cpuPercent = Math.Max(0, (cpuTime - prev.Cpu).TotalMilliseconds
                            / (elapsed * 1000.0 * Environment.ProcessorCount) * 100.0);
                        diskRate = Math.Max(0, (io - prev.Io) / elapsed);
                    }
                }

                result.Add(new ProcessInfo(
                    p.Id, p.ProcessName, SafeWindowTitle(p), responding, SafeMemory(p), SafeStartTime(p),
                    cpuPercent, diskRate));
            }
        }

        PrevSamples.Clear();
        foreach (var kv in current) PrevSamples[kv.Key] = kv.Value;

        return result
            .OrderBy(r => r.Responding)
            .ThenByDescending(r => r.MemoryBytes)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SafeResponding(Process p) { try { return p.Responding; } catch { return true; } }
    private static string SafeWindowTitle(Process p) { try { return p.MainWindowTitle; } catch { return ""; } }
    private static long SafeMemory(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
    private static DateTime? SafeStartTime(Process p) { try { return p.StartTime; } catch { return null; } }
    private static TimeSpan SafeCpuTime(Process p) { try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; } }

    private static long SafeIoBytes(Process p)
    {
        try
        {
            return GetProcessIoCounters(p.Handle, out var io)
                ? (long)(io.ReadTransferCount + io.WriteTransferCount)
                : 0;
        }
        catch
        {
            return 0; // 다른 사용자 소유·보호된 프로세스는 핸들을 못 얻는다 - 속도 0 으로만 본다
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    /// <summary>
    /// 종료하면 세션·로그인 자체가 끊기는 핵심 시스템 프로세스. 이름만으로 거르므로 완벽하지는 않지만
    /// 실수로 explorer 나 csrss 를 눌러 세션을 날리는 사고는 막는다. UI 는 이 목록의 프로세스는 [종료] 버튼을 비활성화한다.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "taskhostw", "ctfmon",
    };

    public static bool IsProtected(string processName) => ProtectedNames.Contains(processName);

    /// <summary>지금 권한으로 바로 종료를 시도한다. 자식까지 함께 끝낸다.</summary>
    public static KillOutcome TryKill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return p.WaitForExit(3000) ? KillOutcome.Success : KillOutcome.Error;
        }
        catch (ArgumentException) { return KillOutcome.NotFound; }
        catch (InvalidOperationException) { return KillOutcome.NotFound; }
        catch (Win32Exception) { return KillOutcome.AccessDenied; }
        catch { return KillOutcome.Error; }
    }

    /// <summary>
    /// 같은 실행 파일을 --kill-pid=&lt;pid[,pid...]&gt; 인자로 UAC 상승 재실행해서 그 프로세스(들)만 죽이고
    /// 즉시 종료하게 한다(App.xaml.cs 참고). 반환값은 실패한 개수 - 0 이면 전부 성공, 음수면 상승 자체가
    /// 안 됐거나(exe 경로 확인 불가) 사용자가 UAC 를 취소한 것이다.
    /// </summary>
    public static int TryKillElevated(IReadOnlyList<int> pids)
    {
        try
        {
            // MainModule.FileName 이 아니라 Environment.ProcessPath 를 써야 한다 - 자체 포함(단일 파일) 배포에서는
            // MainModule.FileName 이 실행 중 풀린 내부 임시 경로를 가리킬 수 있고, 그걸로 재실행하면
            // ".NET 이 없습니다" 오류가 뜬다(MainWindow.xaml.cs 의 관리자 권한 재실행과 같은 방식으로 맞춘다).
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return -1;

            using var proc = Process.Start(new ProcessStartInfo(exe, $"--kill-pid={string.Join(",", pids)}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc == null) return -1;

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return -1; // UAC 취소 포함
        }
    }

    public static bool TryKillElevated(int pid) => TryKillElevated(new[] { pid }) == 0;
}
