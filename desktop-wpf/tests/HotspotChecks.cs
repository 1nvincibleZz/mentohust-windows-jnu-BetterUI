using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    // Test executable resource provider; does not reference WPF or initialize authentication.
    internal static class Program
    {
        internal static Stream Resource(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream("MentoHUST.Desktop." + name); }
    }
    internal sealed class FakeHotspotActions : IHotspotActions
    {
        internal int Calls;
        internal Guid Adapter;
        internal CancellationToken Token;
        internal bool Fail, Wait;
        public async Task<string> RunAsync(Guid adapter, CancellationToken token)
        {
            Calls++; Adapter = adapter; Token = token;
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            if (Fail) throw new InvalidOperationException("test failure");
            return "test complete";
        }
    }
    internal static class HotspotChecks
    {
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static async Task CheckAsync()
        {
            var adapter = new Guid("00000000-0000-0000-0000-000000000001");
            string key = "\\Device\\NPF_" + adapter.ToString("B");
            var fake = new FakeHotspotActions(); var flow = new HotspotAfterAuthentication(fake);
            flow.BeginSession(false, key); await flow.HandleStateAsync(AuthenticationState.Connected);
            Require(fake.Calls == 0, "Disabled option must do nothing");
            flow.BeginSession(true, key);
            foreach (var state in new[] { AuthenticationState.Starting, AuthenticationState.Identity, AuthenticationState.Challenge, AuthenticationState.Dhcp })
                await flow.HandleStateAsync(state);
            Require(fake.Calls == 0, "No action before Connected");
            Require(await flow.HandleStateAsync(AuthenticationState.Connected) == "test complete", "Action result");
            await flow.HandleStateAsync(AuthenticationState.Connected);
            await flow.HandleStateAsync(AuthenticationState.Starting);
            await flow.HandleStateAsync(AuthenticationState.Connected);
            Require(fake.Calls == 1 && fake.Adapter == adapter, "Duplicate success and automatic reconnect must not repeat");
            flow.BeginSession(true, key); await flow.HandleStateAsync(AuthenticationState.Connected);
            Require(fake.Calls == 2, "A new manual session may attempt once");
            fake.Fail = true; flow.BeginSession(true, key);
            bool failed = false; try { await flow.HandleStateAsync(AuthenticationState.Connected); } catch (InvalidOperationException) { failed = true; }
            await flow.HandleStateAsync(AuthenticationState.Connected);
            Require(failed && fake.Calls == 3, "Failure reported with no automatic retries");
            fake.Fail = false; fake.Wait = true; flow.BeginSession(true, key);
            var running = flow.HandleStateAsync(AuthenticationState.Connected);
            await flow.HandleStateAsync(AuthenticationState.Connected);
            Require(fake.Calls == 4, "Concurrent success does not duplicate action");
            await flow.HandleStateAsync(AuthenticationState.Disconnected); await flow.CancelAndWaitAsync();
            Require(fake.Token.IsCancellationRequested && running.IsCanceled, "Disconnect cancels pending action");
            flow.BeginSession(true, key); await flow.HandleStateAsync(AuthenticationState.Failed);
            Require(fake.Calls == 4, "Authentication failure alone never starts actions");
            var recovered = flow.HandleStateAsync(AuthenticationState.Connected);
            Require(fake.Calls == 5, "First successful retry may attempt within the same active session");
            await flow.HandleStateAsync(AuthenticationState.Failed); await flow.CancelAndWaitAsync();
            Require(recovered.IsCanceled, "Failure during pending action cancels it");
            await flow.HandleStateAsync(AuthenticationState.Connected);
            Require(fake.Calls == 5, "Recovery after an attempt does not repeat actions");
            bool badKey = false; try { flow.BeginSession(true, "untrusted adapter"); } catch (ArgumentException) { badKey = true; }
            Require(badKey, "Only validated adapter GUIDs enter helper");
            // Isolated configuration with dummy credentials; no production file is read.
            string folder = Path.Combine(Path.GetTempPath(), "mentohust-hotspot-store-" + Guid.NewGuid().ToString("N"));
            var store = new LegacyConfigurationStore(Path.Combine(folder, "Config.ini"));
            Require(!store.Load().HotspotAfterSuccess, "Missing option defaults off");
            var draft = SessionDraft.CreatePreview(); draft.HotspotAfterSuccess = true;
            Require(draft.Clone().HotspotAfterSuccess, "Draft clone retains option");
            var saved = store.Save(draft); Require(saved.HotspotAfterSuccess && store.Load().HotspotAfterSuccess, "Option persisted independently from CertFlag");
            saved.HotspotAfterSuccess = false; store.Save(saved); Require(!store.Load().HotspotAfterSuccess, "Option can be disabled and reloaded");
            Console.WriteLine("HOTSPOT_FLOW_PASS: defaults off; Connected only; exact adapter; once per session; no retries; disconnect cancellation; failure; configuration round trip.");
            Console.WriteLine("MOCK_ACTIONS_ONLY: no authentication, process termination or hotspot changes.");
        }
        public static int Main(string[] args)
        {
            try {
                if (args.Length == 2 && args[0] == "--probe") {
                    Console.WriteLine(WindowsHotspotActions.RunHelperAsync(Guid.Parse(args[1]), true, CancellationToken.None).GetAwaiter().GetResult());
                } else CheckAsync().GetAwaiter().GetResult();
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}
