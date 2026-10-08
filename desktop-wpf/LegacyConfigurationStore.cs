using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MentoHUST.Desktop
{
    // Line-preserving editor: unknown settings, sections and comments survive a save.
    internal sealed class IniDocument
    {
        internal readonly List<string> Lines;
        internal IniDocument(string text) { Lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList(); }
        internal IniDocument Clone() { return new IniDocument(string.Join("\n", Lines)); }
        private static bool Section(string line, out string name)
        {
            line = line.Trim(); name = null;
            if (!line.StartsWith("[") || !line.EndsWith("]")) return false;
            name = line.Substring(1, line.Length - 2).Trim(); return true;
        }
        private static bool Key(string line, out string key, out string value)
        {
            key = value = null; string trimmed = line.TrimStart();
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) return false;
            int split = line.IndexOf('='); if (split < 0) return false;
            key = line.Substring(0, split).Trim(); value = line.Substring(split + 1).Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                (value[0] == '\'' && value[value.Length - 1] == '\''))) value = value.Substring(1, value.Length - 2);
            return true;
        }
        internal string Get(string section, string key, string fallback)
        {
            string current = null, name, found, value;
            foreach (string line in Lines) {
                if (Section(line, out name)) current = name;
                else if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase) && Key(line, out found, out value) &&
                    string.Equals(found, key, StringComparison.OrdinalIgnoreCase)) return value;
            }
            return fallback;
        }
        internal void Set(string section, string key, string value)
        {
            if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("配置内容不能包含换行或空字符。");
            int start = -1, end = Lines.Count; string name, found, old;
            for (int i = 0; i < Lines.Count; i++) if (Section(Lines[i], out name)) {
                if (start >= 0) { end = i; break; }
                if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) start = i;
            }
            if (start < 0) { Lines.Add("[" + section + "]"); Lines.Add(key + "=" + value); return; }
            for (int i = start + 1; i < end; i++) if (Key(Lines[i], out found, out old) && string.Equals(found, key, StringComparison.OrdinalIgnoreCase)) {
                int split = Lines[i].IndexOf('='); Lines[i] = Lines[i].Substring(0, split + 1) + value; return;
            }
            Lines.Insert(end, key + "=" + value);
        }
        internal List<string> SectionLines(string section)
        {
            var result = new List<string>(); bool inside = false; string name;
            foreach (string line in Lines) {
                if (Section(line, out name)) { if (inside) break; inside = string.Equals(name, section, StringComparison.OrdinalIgnoreCase); }
                else if (inside) result.Add(line);
            }
            return result;
        }
        internal bool HasSection(string section)
        {
            string name; return Lines.Any(line => Section(line, out name) && string.Equals(name, section, StringComparison.OrdinalIgnoreCase));
        }
        internal void RemoveAccountSections(int count)
        {
            bool remove = false; string name; int index;
            for (int i = 0; i < Lines.Count;) {
                if (Section(Lines[i], out name)) remove = name.StartsWith("Account", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.Substring(7), out index) && index >= 0 && index < count;
                if (remove) Lines.RemoveAt(i); else i++;
            }
        }
        internal string Text { get { return string.Join("\r\n", Lines); } }
        internal static IniDocument Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path); string text;
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            else text = LegacyCodec.FromAnsi(bytes);
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("配置文件含有无效的空字符。");
            return new IniDocument(text);
        }
    }

    public sealed class LegacyConfigurationStore
    {
        public string FilePath { get; private set; }
        private IniDocument document;
        private byte[] baseline;
        public LegacyConfigurationStore(string path) { FilePath = Path.GetFullPath(path); }
        private static int Bit(char value, int range) { return Math.Max(0, value - '0') % range; }
        private static int Digit(char value) { return Math.Max(0, value - '0'); }
        private static int Number(string text, int fallback) { int number; return int.TryParse(text, out number) ? number : fallback; }

        private static SessionDraft Parse(IniDocument ini)
        {
            string flags = ini.Get("Parameters", "CertFlag", "001110080200000").PadRight(15, '0');
            var result = new SessionDraft {
                AutoRun = Bit(flags[0], 2) != 0, AutoAuthenticate = Bit(flags[1], 2) != 0,
                AutoMinimize = Bit(flags[2], 2) != 0, BindMac = Bit(flags[3], 2) != 0,
                Multicast = Bit(flags[4], 2), Dhcp = Bit(flags[5], 4),
                Timeout = (Digit(flags[6]) * 10 + Digit(flags[7])) % 100,
                EchoTime = (Digit(flags[8]) * 100 + Digit(flags[9]) * 10 + Digit(flags[10])) % 1000,
                Reconnect = (Digit(flags[11]) * 100 + Digit(flags[12]) * 10 + Digit(flags[13])) % 1000,
                UsePackage = Bit(flags[14], 2) != 0,
                PackagePath = ini.Get("Parameters", "PackagePath", ""),
                DefaultAccount = ini.Get("Parameters", "DefaultAccount", ""), DefaultAdapter = ini.Get("Parameters", "DefaultAdapter", ""),
                HotspotAfterSuccess = ini.Get("WpfOptions", "HotspotAfterSuccess", "0") == "1"
            };
            int count = Number(ini.Get("Parameters", "AccountCount", "0"), 0);
            if (count < 0 || count > 10000) throw new InvalidDataException("账号数量无效。");
            for (int i = 0; i < count; i++) {
                string section = "Account" + i; string username = ini.Get(section, "Username", "");
                if (username.Length == 0) continue;
                string stored = ini.Get(section, "Password", ""), password;
                bool readable = LegacyCodec.TryDecode(stored, out password);
                result.Accounts.Add(new AccountDraft { SourceSection = section, Username = username,
                    Password = password, OriginalPassword = password, OriginalEncodedPassword = stored,
                    PasswordReadable = readable, Ip = ini.Get(section, "IP", "0.0.0.0") });
            }
            return result;
        }

        public SessionDraft Load()
        {
            document = File.Exists(FilePath) ? IniDocument.Read(FilePath) : new IniDocument("[Parameters]\r\n");
            baseline = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
            return Parse(document);
        }

        public SessionDraft Import(string sourcePath)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            if (string.Equals(sourcePath, FilePath, StringComparison.OrdinalIgnoreCase)) return Load();
            var incoming = IniDocument.Read(sourcePath); var parsed = Parse(incoming);
            WriteAtomic(incoming); document = incoming; baseline = File.ReadAllBytes(FilePath);
            return parsed;
        }

        public SessionDraft Save(SessionDraft draft)
        {
            if (document == null) Load();
            bool exists = File.Exists(FilePath);
            if ((baseline == null && exists) || (baseline != null && (!exists || !File.ReadAllBytes(FilePath).SequenceEqual(baseline))))
                throw new IOException("配置已被其他程序修改，请重新载入后再保存。");
            if (draft.Timeout < 0 || draft.Timeout > 99 || draft.EchoTime < 0 || draft.EchoTime > 999 ||
                draft.Reconnect < 0 || draft.Reconnect > 999 || draft.Multicast < 0 || draft.Multicast > 1 || draft.Dhcp < 0 || draft.Dhcp > 3)
                throw new ArgumentException("参数超出旧客户端允许的范围。");
            var edited = document.Clone();
            edited.Set("Parameters", "CertFlag", string.Format("{0}{1}{2}{3}{4}{5}{6:00}{7:000}{8:000}{9}",
                draft.AutoRun ? 1 : 0, draft.AutoAuthenticate ? 1 : 0, draft.AutoMinimize ? 1 : 0, draft.BindMac ? 1 : 0,
                draft.Multicast, draft.Dhcp, draft.Timeout, draft.EchoTime, draft.Reconnect, draft.UsePackage ? 1 : 0));
            edited.Set("Parameters", "PackagePath", draft.PackagePath ?? "");
            edited.Set("WpfOptions", "HotspotAfterSuccess", draft.HotspotAfterSuccess ? "1" : "0");
            var accountData = draft.Accounts.Select(a => document.SectionLines(a.SourceSection ?? "")).ToList();
            int oldCount = Number(document.Get("Parameters", "AccountCount", "0"), 0);
            for (int i = oldCount; i < draft.Accounts.Count; i++)
                if (document.HasSection("Account" + i)) throw new InvalidDataException("新增账号与旧文件中未管理的账号段冲突，请先整理配置。");
            edited.RemoveAccountSections(oldCount);
            edited.Set("Parameters", "AccountCount", draft.Accounts.Count.ToString());
            int selected = -1;
            for (int i = 0; i < draft.Accounts.Count; i++) {
                var account = draft.Accounts[i]; string section = "Account" + i;
                if (account.SourceSection == draft.DefaultAccount) selected = i;
                edited.Lines.Add("[" + section + "]"); edited.Lines.AddRange(accountData[i]);
                string encoded = account.OriginalEncodedPassword != null && account.Password == account.OriginalPassword ?
                    account.OriginalEncodedPassword : LegacyCodec.Encode(account.Password);
                edited.Set(section, "Username", account.Username); edited.Set(section, "Password", encoded); edited.Set(section, "IP", account.Ip);
            }
            if (selected < 0 && draft.Accounts.Count != 0) selected = 0;
            edited.Set("Parameters", "DefaultAccount", selected < 0 ? "" : "Account" + selected);
            if (!string.IsNullOrEmpty(draft.DefaultAdapter)) edited.Set("Parameters", "DefaultAdapter", draft.DefaultAdapter);
            WriteAtomic(edited); document = edited; baseline = File.ReadAllBytes(FilePath);
            return Parse(document);
        }

        private void WriteAtomic(IniDocument edited)
        {
            string directory = Path.GetDirectoryName(FilePath); Directory.CreateDirectory(directory);
            string staging = Path.Combine(directory, ".config-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                // UTF-16 LE BOM is supported by both GetPrivateProfileStringA and W.
                File.WriteAllText(staging, edited.Text, new UnicodeEncoding(false, true));
                Parse(IniDocument.Read(staging));
                if (File.Exists(FilePath)) File.Replace(staging, FilePath, FilePath + ".previous", true);
                else File.Move(staging, FilePath);
            } finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
}
