using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IStripperQuickPlayer;

internal sealed class ProcessCancellationScope : IAsyncDisposable
{
    const uint KillOnJobClose = 0x00002000;
    readonly Process process;
    readonly CancellationToken token;
    readonly CancellationTokenRegistration registration;
    SafeFileHandle? job;

    internal ProcessCancellationScope(Process process, CancellationToken token)
    {
        this.process = process;
        this.token = token;
        job = CreateKillOnCloseJob(process);
        registration = token.Register(static state =>
            ((ProcessCancellationScope)state!).Terminate(), this);
    }

    internal static async Task<bool> VerifyAsync()
    {
        HashSet<int> existingChildren = Process.GetProcessesByName("ping")
            .Select(value => { int id = value.Id; value.Dispose(); return id; }).ToHashSet();
        ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "/d", "/c", "ping.exe", "-t", "127.0.0.1"
        }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("Cancellation test process did not start.");
        int parentId = process.Id;
        int[] children = [];
        try
        {
            using CancellationTokenSource cancellation = new();
            await using (new ProcessCancellationScope(process, cancellation.Token))
            {
                await Task.Delay(500);
                children = Process.GetProcessesByName("ping")
                    .Select(value => { int id = value.Id; value.Dispose(); return id; })
                    .Where(id => !existingChildren.Contains(id)).ToArray();
                if (children.Length == 0) return false;
                cancellation.Cancel();
            }
            return !IsRunning(parentId) && children.All(id => !IsRunning(id));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            foreach (int childId in children)
                try { Process.GetProcessById(childId).Kill(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        registration.Dispose();
        if (token.IsCancellationRequested)
        {
            Terminate();
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
        }
        job?.Dispose();
        job = null;
    }

    void Terminate()
    {
        SafeFileHandle? assignedJob = Interlocked.Exchange(ref job, null);
        if (assignedJob != null)
        {
            try { TerminateJobObject(assignedJob, 1); } catch { }
            finally { assignedJob.Dispose(); }
        }
        try { if (!process.HasExited) process.Kill(true); }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }

    static SafeFileHandle? CreateKillOnCloseJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) return null;
        JobObjectExtendedLimitInformation information = new();
        information.BasicLimitInformation.LimitFlags = KillOnJobClose;
        int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!SetInformationJobObject(job, 9, buffer, (uint)length) ||
                !AssignProcessToJobObject(job, process.SafeHandle))
            {
                job.Dispose();
                return null;
            }
            return job;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static bool IsRunning(int processId)
    {
        try
        {
            using Process value = Process.GetProcessById(processId);
            return !value.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
