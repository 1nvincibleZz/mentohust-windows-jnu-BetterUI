using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace MentoHUST.Desktop
{
    // Distribution has one executable; the unchanged native engine runs privately from the user cache.
    internal static class EmbeddedEngine
    {
        internal static string Resolve(string applicationDirectory, string cacheRoot)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MentoHUST.Desktop.Engine.exe")) {
                if (resource == null) {
                    string developmentEngine = Path.Combine(applicationDirectory, "MentoHUST.Engine.exe");
                    if (!File.Exists(developmentEngine)) throw new FileNotFoundException("认证后台缺失，请使用完整发布版，或运行 build-single-file.ps1。", developmentEngine);
                    return developmentEngine;
                }
                byte[] bytes;
                using (var buffer = new MemoryStream()) { resource.CopyTo(buffer); bytes = buffer.ToArray(); }
                string hash = Hash(bytes);
                string directory = Path.Combine(Path.GetFullPath(cacheRoot), hash);
                Directory.CreateDirectory(directory);
                string destination = Path.Combine(directory, "MentoHUST.Engine.exe");
                if (Matches(destination, hash)) return destination;
                string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                try {
                    File.WriteAllBytes(temporary, bytes);
                    if (Matches(destination, hash)) return destination;
                    if (File.Exists(destination)) {
                        // Replacement is atomic; a partially written engine is never launched.
                        try { File.Replace(temporary, destination, null); }
                        catch (IOException) { if (!Matches(destination, hash)) throw; }
                    } else {
                        try { File.Move(temporary, destination); }
                        catch (IOException) { if (!Matches(destination, hash)) throw; }
                    }
                    if (!Matches(destination, hash)) throw new IOException("认证后台准备失败，请重新打开程序。");
                    return destination;
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static bool Matches(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(input)) == expected;
        }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
    }
}
