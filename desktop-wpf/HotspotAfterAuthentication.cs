using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    internal interface IHotspotActions
    {
        Task<string> RunAsync(Guid adapter, CancellationToken cancellation);
    }

    // One attempt per user-started authentication session, including automatic reconnects.
    internal sealed class HotspotAfterAuthentication
    {
        private readonly IHotspotActions actions;
        private CancellationTokenSource cancellation;
        private Guid adapter;
        private bool enabled, attempted;
        private Task<string> pending;
        internal HotspotAfterAuthentication(IHotspotActions actions) { this.actions = actions; }
        internal void BeginSession(bool enabled, string adapterKey)
        {
            Cancel();
            if (pending != null && !pending.IsCompleted) throw new InvalidOperationException("上次热点操作尚未结束。");
            if (cancellation != null) cancellation.Dispose();
            cancellation = new CancellationTokenSource(); attempted = false; this.enabled = enabled;
            const string prefix = "\\Device\\NPF_";
            if (enabled && (adapterKey == null || !adapterKey.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParse(adapterKey.Substring(prefix.Length), out adapter)))
                throw new ArgumentException("无法识别热点共享来源网卡。");
        }
        internal Task<string> HandleStateAsync(AuthenticationState state)
        {
            if (state == AuthenticationState.Disconnected || (state == AuthenticationState.Failed && attempted)) Cancel();
            if (state != AuthenticationState.Connected || !enabled || attempted || cancellation == null || cancellation.IsCancellationRequested)
                return Task.FromResult<string>(null);
            attempted = true;
            pending = actions.RunAsync(adapter, cancellation.Token);
            return pending;
        }
        internal void Cancel() { if (cancellation != null) cancellation.Cancel(); }
        internal async Task CancelAndWaitAsync()
        {
            Cancel();
            if (pending != null) try { await pending; } catch { /* The success handler reports the original failure. */ }
        }
    }

    internal sealed class WindowsHotspotActions : IHotspotActions
    {
        public Task<string> RunAsync(Guid adapter, CancellationToken cancellation)
        { return RunHelperAsync(adapter, false, cancellation); }

        internal static async Task<string> RunHelperAsync(Guid adapter, bool probeOnly, CancellationToken cancellation)
        {
            string script;
            using (var stream = Program.Resource("StartHotspot.ps1"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) script = reader.ReadToEnd();
            string command = "$MentoAdapterId=[Guid]'" + adapter.ToString("D") + "'; $MentoProbeOnly=$" +
                (probeOnly ? "true" : "false") + ";\n" + script;
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            using (var process = new Process()) {
                process.StartInfo = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(command))) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                cancellation.ThrowIfCancellationRequested(); process.Start();
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                try {
                    await Task.Run(delegate {
                        var watch = Stopwatch.StartNew();
                        while (!process.WaitForExit(100)) {
                            cancellation.ThrowIfCancellationRequested();
                            if (watch.Elapsed > TimeSpan.FromSeconds(80)) throw new TimeoutException("热点操作超时。");
                        }
                        cancellation.ThrowIfCancellationRequested();
                    });
                    string message = (await output).Trim(); string details = (await error).Trim();
                    if (process.ExitCode != 0) throw new InvalidOperationException(message.Length != 0 ? message : details);
                    return message;
                } finally {
                    // Kill only this invocation's helper, never a general PowerShell process.
                    if (!process.HasExited) {
                        try { process.Kill(); process.WaitForExit(1000); }
                        catch (InvalidOperationException) { /* It exited between the state check and Kill. */ }
                    }
                }
            }
        }
    }
}
