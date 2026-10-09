using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MentoHUST.Desktop;

public static class AutomaticAuthenticationChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Main() {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("AUTO_AUTH_CHECKS_PASS: disabled; delayed readiness; once; cancellation; timeout; blocked probe; invalid configuration; failed start without retry."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run()
    {
        int probes = 0, starts = 0;
        var messages = new List<string>();
        Func<CancellationToken, Task<bool>> ready = token => { probes++; return Task.FromResult(true); };
        Func<CancellationToken, Task> start = token => { starts++; return Task.FromResult(0); };
        var disabled = new StartupAuthentication(100, 1);
        await disabled.RunOnceAsync(false, ready, start, messages.Add);
        Require(probes == 0 && starts == 0 && messages.Count == 0, "Disabled setting must have no effects");

        var delayed = new StartupAuthentication(1000, 5);
        Func<CancellationToken, Task<bool>> later = token => Task.FromResult(++probes >= 3);
        Task waiting = delayed.RunOnceAsync(true, later, start, messages.Add);
        await delayed.RunOnceAsync(true, later, start, messages.Add);
        await waiting;
        Require(starts == 1 && probes == 3 && !delayed.IsPending, "Delayed readiness must start once");
        await delayed.RunOnceAsync(true, ready, start, messages.Add);
        Require(starts == 1, "Completed startup must never start again");

        var cancelled = new StartupAuthentication(1000, 5);
        var entered = new TaskCompletionSource<bool>();
        Task pending = cancelled.RunOnceAsync(true, async token => { entered.TrySetResult(true); await Task.Delay(1000, token); return true; }, start, messages.Add);
        await entered.Task; cancelled.Cancel(); await pending;
        Require(starts == 1 && !cancelled.IsPending, "Cancel while probing must prevent start");

        var timeout = new StartupAuthentication(30, 5);
        await timeout.RunOnceAsync(true, token => Task.FromResult(false), start, messages.Add);
        Require(starts == 1 && messages.Exists(m => m.Contains("超时")), "Unavailable link must time out without start");
        var blocked = new StartupAuthentication(30, 5);
        await blocked.RunOnceAsync(true, async token => { await Task.Delay(1000, token); return true; }, start, messages.Add);
        Require(starts == 1 && !blocked.IsPending, "A blocked probe must be bounded and cancelled");

        var invalid = new StartupAuthentication(100, 1);
        await invalid.RunOnceAsync(true, token => { throw new InvalidOperationException("missing saved account"); }, start, messages.Add);
        Require(starts == 1 && messages.Exists(m => m.Contains("missing saved account")), "Invalid config must log and never start");
        var failed = new StartupAuthentication(100, 1);
        await failed.RunOnceAsync(true, ready, token => { starts++; throw new InvalidOperationException("mock engine refused start"); }, messages.Add);
        await failed.RunOnceAsync(true, ready, start, messages.Add);
        Require(starts == 2 && messages.Exists(m => m.Contains("mock engine refused start")), "A refused start must never be retried automatically");
    }
}
