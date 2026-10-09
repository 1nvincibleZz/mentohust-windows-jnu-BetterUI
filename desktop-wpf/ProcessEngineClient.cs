using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    // The inherited anonymous pipes are private to this child; no listening socket or shared named pipe.
    public sealed class ProcessEngineClient : IAuthenticationClient, IDisposable
    {
        private readonly string executable, configuration, workingDirectory;
        private readonly bool offline;
        private readonly object gate = new object();
        private readonly SemaphoreSlim commands = new SemaphoreSlim(1, 1);
        private readonly Dictionary<long, TaskCompletionSource<string>> replies = new Dictionary<long, TaskCompletionSource<string>>();
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();
        private Process process;
        private Task reader;
        private ChildProcessJob job;
        private long nextCommand, lastEvent;
        private bool disposed, shuttingDown;
        public bool IsAvailable { get; private set; }
        public event EventHandler<AuthenticationEvent> StateChanged;
        internal int ChildId { get { return process == null ? 0 : process.Id; } }

        public ProcessEngineClient(string executable, string configuration, bool offline = false, string workingDirectory = null)
        {
            this.executable = Path.GetFullPath(executable); this.configuration = Path.GetFullPath(configuration); this.offline = offline;
            this.workingDirectory = workingDirectory == null ? Path.GetDirectoryName(this.executable) : Path.GetFullPath(workingDirectory);
        }
        private static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
        private static string Decode(string value) { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); }

        public async Task ConnectAsync(CancellationToken cancellation)
        {
            if (disposed) throw new ObjectDisposedException("ProcessEngineClient");
            cancellation.ThrowIfCancellationRequested();
            if (process == null) {
                if (!File.Exists(executable)) throw new FileNotFoundException("认证后台尚未构建，请运行 build-engine.ps1。", executable);
                var info = new ProcessStartInfo(executable, "--config \"" + configuration + "\"" + (offline ? " --offline" : "")) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true), WorkingDirectory = workingDirectory
                };
                try {
                    job = new ChildProcessJob(); process = Process.Start(info); job.Assign(process);
                    process.StandardInput.AutoFlush = true;
                    // Do not route arbitrary native stderr to UI (it may contain internal data).
                    process.BeginErrorReadLine();
                    reader = Task.Run((Func<Task>)ReadEventsAsync);
                } catch { Dispose(); throw; }
            }
            await Bounded(ready.Task, cancellation, 5000);
        }

        private async Task ReadEventsAsync()
        {
            try {
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null) {
                    if (line.Length > 32768) throw new InvalidDataException("认证后台消息过长。");
                    var fields = line.Split('\t');
                    if (fields.Length == 3 && fields[0] == "READY" && fields[1] == "1" && (fields[2] == "0" || fields[2] == "1")) {
                        IsAvailable = fields[2] == "1"; ready.TrySetResult(IsAvailable);
                    } else if (fields.Length == 4 && fields[0] == "REPLY") {
                        long id; if (!long.TryParse(fields[1], out id)) throw new InvalidDataException("无效的后台命令序号。");
                        TaskCompletionSource<string> completion;
                        lock (gate) { replies.TryGetValue(id, out completion); replies.Remove(id); }
                        if (completion == null) continue;
                        if (fields[2] == "OK") completion.TrySetResult(Decode(fields[3]));
                        else if (fields[2] == "ERROR") completion.TrySetException(new InvalidOperationException(Decode(fields[3])));
                        else throw new InvalidDataException("无效的后台回复。");
                    } else if (fields.Length == 4 && fields[0] == "EVENT") {
                        long sequence; int state;
                        if (!long.TryParse(fields[1], out sequence) || sequence <= lastEvent || !int.TryParse(fields[2], out state) || state < 0 || state > 6)
                            throw new InvalidDataException("无效或乱序的认证状态。");
                        lastEvent = sequence; Publish((AuthenticationState)state, Decode(fields[3]), sequence);
                    } else throw new InvalidDataException("认证后台协议不匹配。");
                }
                if (!shuttingDown) throw new IOException("认证后台已退出。");
            } catch (Exception error) {
                IsAvailable = false; ready.TrySetException(error);
                lock (gate) { foreach (var item in replies.Values) item.TrySetException(error); replies.Clear(); }
                if (!shuttingDown) { Publish(AuthenticationState.Failed, error.Message, lastEvent + 1); Terminate(); }
            }
        }
        private void Publish(AuthenticationState state, string message, long sequence)
        {
            var handler = StateChanged;
            if (handler != null) handler(this, new AuthenticationEvent { State = state, Message = message, Timestamp = DateTime.Now, Sequence = sequence });
        }
        private async Task<T> Bounded<T>(Task<T> work, CancellationToken cancellation, int milliseconds)
        {
            var delay = Task.Delay(milliseconds, cancellation);
            if (await Task.WhenAny(work, delay) != work) {
                Terminate(); cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("认证后台响应超时，已结束本次后台进程。");
            }
            return await work;
        }
        private async Task<string> CommandAsync(string command, AuthenticationRequest request, CancellationToken cancellation)
        {
            if (disposed) throw new ObjectDisposedException("ProcessEngineClient");
            await commands.WaitAsync(cancellation);
            long id = 0;
            try {
                if (process == null || process.HasExited) throw new InvalidOperationException("认证后台没有运行。");
                var completion = new TaskCompletionSource<string>();
                lock (gate) { id = ++nextCommand; replies.Add(id, completion); }
                string line = id + "\t" + command;
                if (request != null) line += "\t" + Encode(request.AccountKey) + "\t" + Encode(request.AdapterKey);
                await process.StandardInput.WriteLineAsync(line);
                return await Bounded(completion.Task, cancellation, 15000);
            } finally { lock (gate) { replies.Remove(id); } commands.Release(); }
        }
        public async Task ValidateAsync(AuthenticationRequest request, CancellationToken cancellation)
        {
            if (request == null || string.IsNullOrEmpty(request.AccountKey) || string.IsNullOrEmpty(request.AdapterKey)) throw new ArgumentException("账号与网卡标识不能为空。");
            await CommandAsync("VALIDATE", request, cancellation);
        }
        public async Task StartAsync(AuthenticationRequest request, CancellationToken cancellation)
        {
            if (!IsAvailable) throw new NotSupportedException("认证后台或抓包驱动不可用。");
            if (request == null || string.IsNullOrEmpty(request.AccountKey) || string.IsNullOrEmpty(request.AdapterKey)) throw new ArgumentException("请选择已保存的账号与网卡。");
            await CommandAsync("START", request, cancellation);
        }
        public async Task StopAsync(CancellationToken cancellation) { await CommandAsync("STOP", null, cancellation); }
        public async Task ShutdownAsync()
        {
            shuttingDown = true;
            try {
                if (process != null && !process.HasExited) {
                    await CommandAsync("QUIT", null, CancellationToken.None);
                    process.StandardInput.Close();
                    await Task.Run(delegate { if (!process.WaitForExit(3000)) Terminate(); });
                }
            } finally { Dispose(); }
        }
        private void Terminate()
        {
            IsAvailable = false;
            lock (gate) { if (job != null) { job.Dispose(); job = null; } }
            try { if (process != null && !process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; shuttingDown = true; Terminate();
            if (process != null) { process.Dispose(); process = null; }
        }
    }

    // Closing the job kills only the engine descendants assigned to it, including on UI crash.
    internal sealed class ChildProcessJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
            public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
            public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private IntPtr handle;
        public ChildProcessJob() {
            handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000;
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits)))) {
                Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        public void Assign(Process process) {
            if (!AssignProcessToJobObject(handle, process.Handle)) { process.Kill(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
