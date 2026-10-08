using System;
using System.IO;
using Microsoft.Win32;

namespace MentoHUST.Desktop
{
    public interface IStartupRegistration
    {
        bool IsEnabled { get; }
        // Apply only on explicit settings save; return an undo action if config save fails.
        Action Apply(bool enabled);
    }

    public sealed class MemoryStartupRegistration : IStartupRegistration
    {
        public bool IsEnabled { get; private set; }
        public int ApplyCount { get; private set; }
        public Action Apply(bool enabled)
        {
            bool previous = IsEnabled; IsEnabled = enabled; ApplyCount++;
            return delegate { IsEnabled = previous; };
        }
    }

    public sealed class WindowsStartupRegistration : IStartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MentoHUST.Wpf";
        private readonly string keyPath, executablePath;
        public string LaunchCommand { get; private set; }

        public WindowsStartupRegistration(string executablePath) : this(executablePath, RunKey) { }
        // Tests use a private HKCU branch, never a real Run key.
        internal WindowsStartupRegistration(string executablePath, string isolatedKeyPath)
        {
            this.executablePath = Path.GetFullPath(executablePath);
            if (this.executablePath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("启动路径含有无效字符。");
            keyPath = isolatedKeyPath;
            LaunchCommand = "\"" + this.executablePath + "\" --startup";
        }

        private sealed class Entry
        {
            internal string Command;
            internal RegistryValueKind Kind = RegistryValueKind.String;
        }

        private Entry Read()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false)) {
                if (key == null) return new Entry();
                object value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return new Entry();
                var kind = key.GetValueKind(ValueName);
                if (!(value is string) || (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
                    throw new InvalidOperationException("本客户端的启动项格式异常，请先检查 Windows 启动设置。");
                return new Entry { Command = (string)value, Kind = kind };
            }
        }

        private void Write(Entry entry)
        {
            if (entry.Command == null) {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    if (key != null) key.DeleteValue(ValueName, false);
            } else {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) {
                    if (key == null) throw new IOException("无法打开当前用户的 Windows 启动项。");
                    key.SetValue(ValueName, entry.Command, entry.Kind);
                }
            }
        }

        public bool IsEnabled { get { return !string.IsNullOrEmpty(Read().Command); } }
        public Action Apply(bool enabled)
        {
            if (enabled && !File.Exists(executablePath)) throw new FileNotFoundException("当前客户端已被移动，请从固定目录重新启动后保存。", executablePath);
            if (enabled && LaunchCommand.Length > 260) throw new InvalidOperationException("启动路径过长，请将客户端移至较短的固定目录后保存。");
            Entry previous = Read();
            var desired = new Entry { Command = enabled ? LaunchCommand : null };
            bool changed = previous.Command != desired.Command || (desired.Command != null && previous.Kind != desired.Kind);
            if (changed) Write(desired);
            return delegate {
                if (!changed) return;
                Entry current = Read();
                if (current.Command != desired.Command || (desired.Command != null && current.Kind != desired.Kind))
                    throw new IOException("启动项已被其他程序修改，无法回滚本次保存。");
                Write(previous);
            };
        }
    }

    public static class StartupSettingsPersistence
    {
        public static SessionDraft Save(LegacyConfigurationStore store, SessionDraft draft, IStartupRegistration startup)
        {
            if (store == null) return draft.Clone();
            if (startup == null) return store.Save(draft);
            Action undo = startup.Apply(draft.AutoRun);
            try { return store.Save(draft); }
            catch (Exception saveError) {
                try { undo(); }
                catch (Exception undoError) {
                    throw new IOException("配置保存失败：" + saveError.Message + "；启动项回滚失败：" + undoError.Message, saveError);
                }
                throw;
            }
        }
    }
}
