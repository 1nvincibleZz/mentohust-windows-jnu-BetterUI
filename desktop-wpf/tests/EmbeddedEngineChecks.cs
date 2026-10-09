using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MentoHUST.Desktop;

class EmbeddedEngineChecks
{
    static string Hash(string path) { using (var sha = SHA256.Create()) using (var input = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)); }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static int Main(string[] args)
    {
        try {
            string root = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
            string app = Path.Combine(root, "standalone app"); string cache = Path.Combine(root, "user cache");
            Directory.CreateDirectory(app);
            string extracted = EmbeddedEngine.Resolve(app, cache);
            Require(File.Exists(extracted) && !File.Exists(Path.Combine(app, "MentoHUST.Engine.exe")), "fresh extraction without a sibling executable");
            Require(Hash(extracted) == Hash(args[1]), "embedded native engine bytes unchanged");
            DateTime originalWrite = File.GetLastWriteTimeUtc(extracted);
            using (var held = File.OpenRead(extracted)) Require(EmbeddedEngine.Resolve(app, cache) == extracted, "valid cache reused while open");
            Require(File.GetLastWriteTimeUtc(extracted) == originalWrite, "valid cache not rewritten");
            File.WriteAllText(extracted, "damaged cache");
            Require(Hash(EmbeddedEngine.Resolve(app, cache)) == Hash(args[1]), "damaged cache restored from the embedded resource");
            string parallelCache = Path.Combine(root, "parallel cache");
            var jobs = Enumerable.Range(0, 8).Select(_ => Task.Run(() => EmbeddedEngine.Resolve(app, parallelCache))).ToArray();
            Task.WaitAll(jobs);
            Require(jobs.All(t => t.Result == jobs[0].Result) && Hash(jobs[0].Result) == Hash(args[1]), "concurrent first launches resolve the same complete file");
            Require(Directory.GetFiles(parallelCache, "*.tmp", SearchOption.AllDirectories).Length == 0, "temporary staging files cleaned");
            Console.WriteLine("EMBEDDED_ENGINE_CHECKS_PASS"); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
