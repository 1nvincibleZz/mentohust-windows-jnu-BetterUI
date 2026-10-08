using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MentoHUST.Desktop
{
    internal static class ConfigurationChecks
    {
        [DllImport("legacy_codec_oracle.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int EncodeRuijie(StringBuilder output, byte[] input);
        [DllImport("legacy_codec_oracle.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int DecodeRuijie(byte[] output, string input);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPrivateProfileStringW")]
        private static extern uint ReadNative(string section, string key, string fallback, StringBuilder output, uint capacity, string file);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetPrivateProfileStringA")]
        private static extern uint ReadNativeAnsi(string section, string key, string fallback, StringBuilder output, uint capacity, string file);
        private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
        private static string Native(string file, string section, string key)
        { var output = new StringBuilder(1024); ReadNative(section, key, "", output, 1024, file); return output.ToString(); }
        private static bool Same(byte[] a, byte[] b) { return a.SequenceEqual(b); }

        private static void CheckCodec()
        {
            var samples = new[] { "", "a", "ab", "abc", "test", "preview_only", "校园测试", new string('x', 63) }.ToList();
            var random = new Random(1729);
            for (int i = 0; i < 60; i++) samples.Add(new string(Enumerable.Range(0, random.Next(1, 64)).Select(n => (char)random.Next(33, 127)).ToArray()));
            foreach (string password in samples) {
                byte[] input = LegacyCodec.ToAnsi(password).Concat(new byte[] { 0 }).ToArray();
                var encoded = new StringBuilder(128);
                Require(EncodeRuijie(encoded, input) == 1, "Native encoder failed");
                Require(encoded.ToString() == LegacyCodec.Encode(password), "C# encoder differs from original C++");
                string decoded; Require(LegacyCodec.TryDecode(encoded.ToString(), out decoded) && decoded == password, "C# decoding mismatch at sample " + samples.IndexOf(password));
                var nativeDecoded = new byte[128]; Require(DecodeRuijie(nativeDecoded, LegacyCodec.Encode(password)) == 1, "Original decoder rejects C# output");
                int length = Array.IndexOf(nativeDecoded, (byte)0);
                Require(LegacyCodec.FromAnsi(nativeDecoded.Take(length).ToArray()) == password, "Original decoded value differs");
            }
            foreach (string invalid in new[] { "bad@@@", "a===", " abc", "abc" }) {
                string decoded; Require(!LegacyCodec.TryDecode(invalid, out decoded), "Invalid credential should remain opaque");
            }
            Console.WriteLine("CODEC_CPP_PARITY_PASS " + samples.Count);
        }

        public static int Main(string[] args)
        {
            try {
                CheckCodec();
                string folder = Path.Combine(Path.GetFullPath(args[0]), "store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                string source = Path.Combine(folder, "legacy-source.ini"), target = Path.Combine(folder, "wpf-copy.ini");
                string sourceText = "; 原配置注释\r\n[Parameters]\r\nCertFlag=101011129990451\r\nAccountCount=2\r\nDefaultAccount=Account1\r\n" +
                    "DefaultAdapter=\\Device\\NPF_{00000000-0000-0000-0000-000000000001}\r\nExtraFlag=15021\r\nRuijie6Mode=1\r\nVendorOption=keep\r\nPackagePath=C:\\原数据\\payload.bin\r\n" +
                    "[Account0]\r\nUsername=demo_student\r\nPassword=" + LegacyCodec.Encode("preview_only") + "\r\nIP=0.0.0.0\r\nVendorHint=row-zero\r\n" +
                    "[Account1]\r\nUsername=test_account\r\nPassword=bad@@@\r\nIP=192.0.2.8\r\nVendorHint=row-one\r\n" +
                    "[Custom]\r\nKeep=原值\r\n[Account999]\r\nOrphan=do-not-remove\r\n";
                File.WriteAllBytes(source, LegacyCodec.ToAnsi(sourceText)); byte[] original = File.ReadAllBytes(source);
                var store = new LegacyConfigurationStore(target); var loaded = store.Import(source);
                Require(loaded.Accounts.Count == 2 && loaded.Accounts[0].Password == "preview_only" && !loaded.Accounts[1].PasswordReadable, "Account import mismatch");
                Require(loaded.AutoRun && !loaded.AutoAuthenticate && loaded.AutoMinimize && !loaded.BindMac && loaded.Multicast == 1 && loaded.Dhcp == 1 &&
                    loaded.Timeout == 12 && loaded.EchoTime == 999 && loaded.Reconnect == 45 && loaded.UsePackage, "CertFlag mapping mismatch");
                Require(loaded.DefaultAccount == "Account1" && loaded.PackagePath == "C:\\原数据\\payload.bin", "Default and path import mismatch");
                var changed = loaded.Clone(); changed.Timeout = 8; var saved = store.Save(changed);
                Require(Native(target, "Parameters", "CertFlag") == "101011089990451", "Native reader sees wrong flags");
                Require(Native(target, "Account1", "Password") == "bad@@@", "Opaque password was changed");
                Require(Native(target, "Parameters", "ExtraFlag") == "15021" && Native(target, "Parameters", "Ruijie6Mode") == "1" &&
                    Native(target, "Parameters", "VendorOption") == "keep" && Native(target, "Custom", "Keep") == "原值" &&
                    Native(target, "Account999", "Orphan") == "do-not-remove", "Unknown data was lost");
                var ansi = new StringBuilder(1024); ReadNativeAnsi("Parameters", "PackagePath", "", ansi, 1024, target);
                Require(ansi.ToString() == "C:\\原数据\\payload.bin", "Old ANSI reader cannot read saved Unicode config");
                Require(File.ReadAllText(target).Contains("; 原配置注释"), "Comment was lost");
                saved.Accounts.RemoveAt(0); saved = store.Save(saved);
                Require(saved.DefaultAccount == "Account0" && saved.Accounts.Count == 1 && saved.Accounts[0].Username == "test_account", "Default account did not track renumbering");
                Require(Native(target, "Account0", "VendorHint") == "row-one", "Account metadata did not move with account");
                Require(Native(target, "Account1", "Username") == "", "Deleted account remains visible to old reader");
                saved.Accounts.Add(new AccountDraft { Username = "校园账号", Password = "dummy_123", Ip = "0.0.0.0" });
                saved.Timeout = 99; saved.EchoTime = 999; saved.Reconnect = 999; saved = store.Save(saved);
                Require(saved.Accounts.Count == 2 && saved.Accounts[1].Password == "dummy_123" && saved.Timeout == 99 && saved.Reconnect == 999, "Added account or boundary values failed");
                Require(Native(target, "Account1", "Username") == "校园账号", "Native reader lost non-ASCII username");
                Require(Native(target, "Account1", "Password") != "dummy_123", "Plaintext password written");
                byte[] snapshot = File.ReadAllBytes(target); var invalid = saved.Clone(); invalid.Timeout = 100;
                bool rejected = false; try { store.Save(invalid); } catch (ArgumentException) { rejected = true; }
                Require(rejected && Same(snapshot, File.ReadAllBytes(target)), "Invalid parameters changed config");
                invalid = saved.Clone(); invalid.Accounts[0].Username = "bad\nname"; rejected = false;
                try { store.Save(invalid); } catch (ArgumentException) { rejected = true; }
                Require(rejected && Same(snapshot, File.ReadAllBytes(target)), "Failed serialization changed config");
                File.AppendAllText(target, "; external change\r\n", Encoding.Unicode); snapshot = File.ReadAllBytes(target); rejected = false;
                try { store.Save(saved); } catch (IOException) { rejected = true; }
                Require(rejected && Same(snapshot, File.ReadAllBytes(target)), "External change was overwritten");
                Require(Same(original, File.ReadAllBytes(source)), "Original imported file was modified");
                Require(File.Exists(target + ".previous"), "Previous-copy backup missing");
                string utf8 = Path.Combine(folder, "utf8.ini"); File.WriteAllText(utf8, sourceText, new UTF8Encoding(true));
                store.Import(utf8); Require(store.Load().Accounts.Count == 2, "UTF-8 BOM import failed");
                Require(Native(target, "Custom", "Keep") == "原值", "Imported UTF-8 was not saved in legacy-readable format");
                Console.WriteLine("CONFIGURATION_COMPATIBILITY_PASS; SOURCE_UNCHANGED; UNKNOWN_DATA_PRESERVED; CONFLICT_REJECTED");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
