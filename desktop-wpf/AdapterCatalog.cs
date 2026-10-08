using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace MentoHUST.Desktop
{
    public static class AdapterCatalog
    {
        [StructLayout(LayoutKind.Sequential)] private struct PcapInterface
        { public IntPtr Next, Name, Description, Addresses; public uint Flags; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindInterfaces(out IntPtr devices, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeInterfaces(IntPtr devices);

        public static IList<AdapterInfo> Enumerate()
        {
            NetworkInterface[] windows = NetworkInterface.GetAllNetworkInterfaces();
            var result = new List<AdapterInfo>();
            string[] candidates = { Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"), Path.Combine(Environment.SystemDirectory, "wpcap.dll") };
            foreach (string path in candidates) {
                if (!File.Exists(path)) continue;
                IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 8);
                if (module == IntPtr.Zero) continue;
                IntPtr devices = IntPtr.Zero; FreeInterfaces free = null;
                try {
                    IntPtr findAddress = GetProcAddress(module, "pcap_findalldevs"), freeAddress = GetProcAddress(module, "pcap_freealldevs");
                    if (findAddress == IntPtr.Zero || freeAddress == IntPtr.Zero) continue;
                    var find = (FindInterfaces)Marshal.GetDelegateForFunctionPointer(findAddress, typeof(FindInterfaces));
                    free = (FreeInterfaces)Marshal.GetDelegateForFunctionPointer(freeAddress, typeof(FreeInterfaces));
                    if (find(out devices, new StringBuilder(256)) != 0) continue;
                    IntPtr pointer = devices;
                    while (pointer != IntPtr.Zero) {
                        var item = (PcapInterface)Marshal.PtrToStructure(pointer, typeof(PcapInterface)); pointer = item.Next;
                        if ((item.Flags & 1) != 0 || item.Name == IntPtr.Zero) continue;
                        string id = Marshal.PtrToStringAnsi(item.Name), name = item.Description == IntPtr.Zero ? id : Marshal.PtrToStringAnsi(item.Description);
                        var nic = windows.FirstOrDefault(n => id.IndexOf(n.Id.Trim('{', '}'), StringComparison.OrdinalIgnoreCase) >= 0);
                        if (nic != null) name = nic.Name + " · " + nic.Description;
                        result.Add(new AdapterInfo { Id = id, Name = name, CaptureAvailable = true });
                    }
                    return result;
                } finally { if (devices != IntPtr.Zero && free != null) free(devices); FreeLibrary(module); }
            }
            // Display OS adapters when the capture API is unavailable, but do not manufacture pcap IDs.
            foreach (var nic in windows.Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up))
                result.Add(new AdapterInfo { Id = null, Name = nic.Name + " · " + nic.Description, CaptureAvailable = false });
            return result;
        }
    }
}
