using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    internal static class EngineChecks
    {
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static bool Exited(int id) {
            try { using (var process = Process.GetProcessById(id)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        private static async Task Rejected(Func<Task> action) {
            bool rejected = false; try { await action(); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Malformed configuration/request was accepted");
        }
        private static async Task Run(string executable, string folder)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Config.ini");
            var store = new LegacyConfigurationStore(path); store.Load(); store.Save(SessionDraft.CreatePreview());
            byte[] before = File.ReadAllBytes(path);
            var request = new AuthenticationRequest { AccountKey = "Account0", AdapterKey = "\\Device\\NPF_{00000000-0000-0000-0000-000000000001}" };
            var states = new List<AuthenticationEvent>();
            var client = new ProcessEngineClient(executable, path, true);
            client.StateChanged += delegate(object sender, AuthenticationEvent e) { lock (states) states.Add(e); };
            await client.ConnectAsync(CancellationToken.None); int id = client.ChildId;
            Require(!client.IsAvailable, "Offline backend must never be available for authentication");
            await client.ValidateAsync(request, CancellationToken.None);
            await Rejected(delegate { return client.ValidateAsync(new AuthenticationRequest { AccountKey = "../Account0", AdapterKey = request.AdapterKey }, CancellationToken.None); });
            await Rejected(delegate { return client.ValidateAsync(new AuthenticationRequest { AccountKey = "Account0", AdapterKey = "bad" }, CancellationToken.None); });
            await Rejected(delegate { return client.ValidateAsync(new AuthenticationRequest { AccountKey = "Account999", AdapterKey = request.AdapterKey }, CancellationToken.None); });
            var unicode = store.Load(); unicode.Accounts[0].Username = "校园测试账号"; unicode.Accounts[0].Password = "校园测试";
            unicode.PackagePath = "C:\\测试目录\\包.mpf"; store.Save(unicode);
            await client.ValidateAsync(request, CancellationToken.None);
            var invalid = store.Load(); invalid.Accounts[0].Ip = "bad"; store.Save(invalid);
            await Rejected(delegate { return client.ValidateAsync(request, CancellationToken.None); });
            File.WriteAllBytes(path, before); // Restore this test's original bytes only.
            bool blocked = false; try { await client.StartAsync(request, CancellationToken.None); } catch (NotSupportedException) { blocked = true; }
            Require(blocked, "Offline start wasn't blocked");
            await client.StopAsync(CancellationToken.None); await client.StopAsync(CancellationToken.None);
            await client.ShutdownAsync(); Require(Exited(id), "Graceful close left engine running");
            lock (states) {
                Require(states.Count >= 2 && states.All(e => e.State == AuthenticationState.Disconnected), "Offline mode manufactured an authentication state");
                Require(states.Zip(states.Skip(1), (a,b) => b.Sequence > a.Sequence).All(x => x), "State sequence not ordered");
            }
            Require(before.SequenceEqual(File.ReadAllBytes(path)), "Read-only validation or idle stop changed configuration");
            // Crash-like Dispose closes the job. Never target a process outside the returned child ID.
            var abrupt = new ProcessEngineClient(executable, path, true);
            await abrupt.ConnectAsync(CancellationToken.None); id = abrupt.ChildId; abrupt.Dispose();
            for (int i = 0; i < 50 && !Exited(id); i++) await Task.Delay(20);
            Require(Exited(id), "Job teardown left engine running");
            var failed = new ProcessEngineClient(executable, path, true);
            var failure = new TaskCompletionSource<bool>();
            failed.StateChanged += delegate(object sender, AuthenticationEvent e) { if (e.State == AuthenticationState.Failed) failure.TrySetResult(true); };
            await failed.ConnectAsync(CancellationToken.None); id = failed.ChildId;
            using (var ownChild = Process.GetProcessById(id)) ownChild.Kill();
            Require(await Task.WhenAny(failure.Task, Task.Delay(3000)) == failure.Task && !failed.IsAvailable, "Unexpected engine exit was not propagated");
            failed.Dispose();
            using (var canceled = new ProcessEngineClient(executable, path, true)) {
                bool canceledBeforeLaunch = false;
                try { await canceled.ConnectAsync(new CancellationToken(true)); } catch (OperationCanceledException) { canceledBeforeLaunch = true; }
                Require(canceledBeforeLaunch && canceled.ChildId == 0, "Canceled startup launched a child");
            }
            // Native START is independently exercised with --offline, so it cannot send packets even if the UI guard fails.
            var info = new ProcessStartInfo(executable, "--config \""+path+"\" --offline") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var raw = Process.Start(info)) {
                try {
                    Require(raw.StandardOutput.ReadLine() == "READY\t1\t0", "Handshake mismatch");
                    raw.StandardInput.WriteLine("1\tSTART\t"+Convert.ToBase64String(Encoding.UTF8.GetBytes(request.AccountKey))+"\t"+Convert.ToBase64String(Encoding.UTF8.GetBytes(request.AdapterKey))); raw.StandardInput.Flush();
                    string reply = raw.StandardOutput.ReadLine(); Require(reply.StartsWith("REPLY\t1\tERROR\t"), "Native offline start accepted authentication");
                    if (Process.GetProcessesByName("MentoHUST").Length > 0) {
                        string message = Encoding.UTF8.GetString(Convert.FromBase64String(reply.Split('\t')[3]));
                        Require(message.Contains("旧版 MentoHUST 正在运行"), "Parallel legacy client guard did not reject START");
                    }
                    raw.StandardInput.Close(); Require(raw.WaitForExit(3000), "Pipe EOF did not exit native host");
                } finally { if (!raw.HasExited) raw.Kill(); }
            }
            Require(before.SequenceEqual(File.ReadAllBytes(path)), "Native offline START wrote configuration");
            Console.WriteLine("ENGINE_CHECKS_PASS: native config validation; malformed requests; ordered states; offline START rejection; idle stop; graceful close; job teardown; unexpected exit; stdin EOF.");
            Console.WriteLine("NO_AUTHENTICATION_PACKETS; TEMPORARY_DUMMY_CONFIG_ONLY");
        }
        public static int Main(string[] args) {
            try { Run(Path.GetFullPath(args[0]), Path.Combine(Path.GetFullPath(args[1]), "engine-"+Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
