using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

internal static class NativeTerminationChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try {
            using (var fixture = new Process()) {
                fixture.StartInfo = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                fixture.Start();
                try {
                    Require(fixture.StandardOutput.ReadLine() == "READY", "Fixture could not set its own restricted DACL");
                    bool queryDenied = false;
                    using (var fresh = Process.GetProcessById(fixture.Id)) {
                        try { bool ignored = fresh.HasExited; }
                        catch (Win32Exception error) { queryDenied = error.NativeErrorCode == 5; }
                    }
                    Require(queryDenied, "Fixture must reproduce broad-query Access denied");
                    using (var native = MentoProcessAccess.Open(fixture.Id)) {
                        Require(native != null && string.Equals(Path.GetFullPath(native.ImagePath), Path.GetFullPath(args[0]), StringComparison.OrdinalIgnoreCase), "Native handle must identify our exact isolated child");
                        Require(native.TerminateOnce(), "Native termination must succeed despite denied broad-query access");
                        bool repeatBlocked = false;
                        try { native.TerminateOnce(); } catch (InvalidOperationException) { repeatBlocked = true; }
                        Require(repeatBlocked, "Second termination must be blocked");
                    }
                    Require(fixture.WaitForExit(5000), "Only our fixture child must have exited");
                } finally {
                    // This handle belongs to a fixture created above, never an enumerated system process.
                    if (!fixture.WaitForExit(0)) { fixture.Kill(); fixture.WaitForExit(5000); }
                }
            }
            bool wrongName = false;
            try { using (var native = MentoProcessAccess.Open(Process.GetCurrentProcess().Id)) {} }
            catch (InvalidOperationException) { wrongName = true; }
            Require(wrongName, "Non-8021x executable must be refused before termination");
            Console.WriteLine("NATIVE_TERMINATION_PASS: restricted child reproduces query denial; exact path verified; native termination and wait succeed; duplicate and wrong-name calls blocked.");
            Console.WriteLine("ISOLATED_CHILD_ONLY: no real 8021x, service, authentication or hotspot changed.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
