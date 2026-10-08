using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using MentoHUST.Desktop;

internal static class StartupChecks
{
    private sealed class FailingStartup : IStartupRegistration
    {
        public bool IsEnabled { get { return false; } }
        public Action Apply(bool enabled) { throw new UnauthorizedAccessException("isolated permission failure"); }
    }
    private sealed class FailingUndoStartup : IStartupRegistration
    {
        public bool IsEnabled { get { return false; } }
        public Action Apply(bool enabled) { return delegate { throw new IOException("isolated rollback failure"); }; }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        // Test registry data cannot execute at logon: this branch is outside every Windows Run key.
        string testKey = @"Software\MentoHUST.Wpf\Tests\Startup_" + Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(args[0]);
            string testDirectory = Path.Combine(args[0], "会话 with spaces " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            string executable = Path.Combine(testDirectory, "MentoHUST.Desktop.Preview.exe");
            File.WriteAllText(executable, "test path only; never launched");
            var startup = new WindowsStartupRegistration(executable, testKey);
            var store = new LegacyConfigurationStore(Path.Combine(testDirectory, "Config.ini"));
            var draft = store.Load(); store.Save(draft);
            byte[] baseline = File.ReadAllBytes(store.FilePath);
            using (var key = Registry.CurrentUser.CreateSubKey(testKey)) {
                key.SetValue("OtherApplication", "preserved"); key.SetValue("MentoHUST", "legacy-preserved");
            }
            Require(!startup.IsEnabled, "Absent entry must be off");
            draft.AutoRun = true;
            var saved = StartupSettingsPersistence.Save(store, draft, startup);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey)) {
                Require((string)key.GetValue("MentoHUST.Wpf") == "\"" + executable + "\" --startup", "Executable with spaces must be fully quoted");
                Require(key.GetValueKind("MentoHUST.Wpf") == RegistryValueKind.String, "Run command must be REG_SZ");
            }
            Require(startup.IsEnabled && saved.AutoRun && new LegacyConfigurationStore(store.FilePath).Load().AutoRun, "Enable must persist system and config state");
            saved.AutoRun = false; StartupSettingsPersistence.Save(store, saved, startup);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey)) {
                Require(key.GetValue("MentoHUST.Wpf") == null, "Disable must remove own value");
                Require((string)key.GetValue("OtherApplication") == "preserved" && (string)key.GetValue("MentoHUST") == "legacy-preserved", "Disable must preserve unrelated and legacy startup entries");
                key.Dispose();
            }
            Require(!new LegacyConfigurationStore(store.FilePath).Load().AutoRun, "Disable must survive config reload");
            using (var key = Registry.CurrentUser.OpenSubKey(testKey, true))
                key.SetValue("MentoHUST.Wpf", @"%TEMP%\older.exe", RegistryValueKind.ExpandString);
            File.AppendAllText(store.FilePath, "\r\n; isolated external edit");
            byte[] externallyEdited = File.ReadAllBytes(store.FilePath);
            bool rejected = false;
            try { draft.AutoRun = true; StartupSettingsPersistence.Save(store, draft, startup); } catch (IOException) { rejected = true; }
            Require(rejected && File.ReadAllBytes(store.FilePath).SequenceEqual(externallyEdited), "External config changes must be rejected without overwrite");
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Require((string)key.GetValue("MentoHUST.Wpf", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == @"%TEMP%\older.exe" &&
                    key.GetValueKind("MentoHUST.Wpf") == RegistryValueKind.ExpandString, "Failed save must restore previous command and registry kind");
            store.Load(); baseline = File.ReadAllBytes(store.FilePath);
            bool permissionRejected = false;
            try { StartupSettingsPersistence.Save(store, draft, new FailingStartup()); } catch (UnauthorizedAccessException) { permissionRejected = true; }
            Require(permissionRejected && File.ReadAllBytes(store.FilePath).SequenceEqual(baseline), "Startup permission failure must leave config untouched");
            File.AppendAllText(store.FilePath, "\r\n; another isolated external edit");
            bool undoReported = false;
            try { StartupSettingsPersistence.Save(store, draft, new FailingUndoStartup()); }
            catch (IOException error) { undoReported = error.Message.Contains("配置保存失败") && error.Message.Contains("启动项回滚失败"); }
            Require(undoReported, "Both config and rollback failures must be reported");
            using (var key = Registry.CurrentUser.OpenSubKey(testKey, true)) key.SetValue("MentoHUST.Wpf", new byte[] { 1, 2 }, RegistryValueKind.Binary);
            bool formatRejected = false;
            try { startup.Apply(true); } catch (InvalidOperationException) { formatRejected = true; }
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Require(formatRejected && key.GetValueKind("MentoHUST.Wpf") == RegistryValueKind.Binary, "Unexpected registry format must not be overwritten");
            Console.WriteLine("STARTUP_CHECKS_PASS: quoted Chinese/space path; enable and disable; reload; other entries preserved; permission failure; config-conflict rollback with original registry kind; rollback failure reported; unexpected format refused.");
            Console.WriteLine("ISOLATED_HKCU_BRANCH_ONLY: no Windows Run entry, real configuration, authentication or hotspot changed; no fixture executable launched.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
    }
}
