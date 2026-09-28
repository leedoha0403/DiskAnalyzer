using System.ComponentModel;
using System.Diagnostics;

namespace DiskAnalyzer.Core.Services;

public sealed record ProcessInfo(
    int Pid,
    string Name,
    string WindowTitle,
    bool Responding,
    long MemoryBytes,
    DateTime? StartTime);

public enum KillOutcome { Success, AccessDenied, NotFound, Error }

/// <summary>
/// 게임 등이 죽다 만 채로 남는 좀비/응답 없음 프로세스를 정리한다.
/// 앱은 asInvoker 로 뜨므로 다른 사용자 소유이거나 보호된 프로세스는 TryKill 이 AccessDenied 를 돌려준다 —
/// 그럴 때만 <see cref="TryKillElevated"/> 로 같은 exe 를 관리자 권한으로 다시 띄워 그 프로세스 하나만 죽이고 빠지게 한다
/// (App.xaml.cs 의 --kill-pid 처리와 짝을 이룬다). 항상 관리자 권한을 요구하지 않는 이유는 app.manifest 참고.
/// </summary>
public static class ProcessCleanerService
{
    public static IReadOnlyList<ProcessInfo> ListProcesses(bool notRespondingOnly)
    {
        int selfPid = Environment.ProcessId;
        var result = new List<ProcessInfo>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == selfPid) continue;

                bool responding = SafeResponding(p);
                if (notRespondingOnly && responding) continue;

                result.Add(new ProcessInfo(
                    p.Id, p.ProcessName, SafeWindowTitle(p), responding, SafeMemory(p), SafeStartTime(p)));
            }
        }

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
    /// 같은 실행 파일을 --kill-pid=&lt;pid&gt; 인자로 UAC 상승 재실행한다. 그 인스턴스는 창을 띄우지 않고
    /// 그 프로세스 하나만 죽인 뒤 즉시 종료한다(App.xaml.cs 참고). 사용자가 UAC 를 취소해도 실패로만 처리한다.
    /// </summary>
    public static bool TryKillElevated(int pid)
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;

            using var proc = Process.Start(new ProcessStartInfo(exe, $"--kill-pid={pid}")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc == null) return false;

            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false; // UAC 취소 포함
        }
    }
}
