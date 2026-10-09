using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    // One automatic attempt per process. Waiting is cancellable; failures never loop START.
    public sealed class StartupAuthentication : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly int timeoutMilliseconds, pollMilliseconds;
        private int claimed;
        public bool IsPending { get; private set; }

        public StartupAuthentication(int timeoutMilliseconds = 60000, int pollMilliseconds = 1000)
        {
            if (timeoutMilliseconds < 1 || pollMilliseconds < 1) throw new ArgumentOutOfRangeException();
            this.timeoutMilliseconds = timeoutMilliseconds; this.pollMilliseconds = pollMilliseconds;
        }

        public void Cancel() { cancellation.Cancel(); }
        public void Dispose() { Cancel(); }

        private static void ObserveFailure(Task pending)
        {
            pending.ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public async Task RunOnceAsync(bool enabled, Func<CancellationToken, Task<bool>> ready,
            Func<CancellationToken, Task> start, Action<string> log)
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0 || !enabled) return;
            IsPending = true;
            var timer = Stopwatch.StartNew();
            Task deadline = Task.Delay(timeoutMilliseconds, cancellation.Token);
            try {
                log("已启用自动认证，正在等待后台和已保存的网卡就绪");
                while (true) {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Task<bool> probe = ready(cancellation.Token);
                    if (await Task.WhenAny(probe, deadline) != probe) {
                        ObserveFailure(probe);
                        cancellation.Token.ThrowIfCancellationRequested();
                        throw new TimeoutException("等待网卡就绪超时，请检查网线与抓包驱动，或手动开始认证。");
                    }
                    bool available = await probe;
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (timer.ElapsedMilliseconds >= timeoutMilliseconds)
                        throw new TimeoutException("等待网卡就绪超时，请检查网线与抓包驱动，或手动开始认证。");
                    if (available) {
                        log("后台与所选网卡已就绪，正在自动开始认证");
                        await start(cancellation.Token);
                        return;
                    }
                    await Task.Delay(Math.Min(pollMilliseconds, Math.Max(1,
                        timeoutMilliseconds - (int)timer.ElapsedMilliseconds)), cancellation.Token);
                }
            } catch (OperationCanceledException) { log("已取消本次自动认证"); }
            catch (Exception error) { log("自动认证未启动：" + error.Message); }
            finally { IsPending = false; cancellation.Cancel(); }
        }
    }
}
