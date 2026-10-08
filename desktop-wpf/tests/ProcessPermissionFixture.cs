using System;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

// Isolated child fixture. Changes only its own DACL, never a real 8021x process.
internal static class ProcessPermissionFixture
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(IntPtr handle, uint type, uint info,
        IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);
    private static int Main()
    {
        string sid = WindowsIdentity.GetCurrent().User.Value;
        var descriptor = new RawSecurityDescriptor("D:(D;;0x0400;;;WD)(A;;0x1FFFFF;;;" + sid + ")");
        var acl = new byte[descriptor.DiscretionaryAcl.BinaryLength];
        descriptor.DiscretionaryAcl.GetBinaryForm(acl, 0);
        uint error = SetSecurityInfo(GetCurrentProcess(), 6, 4, IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero);
        if (error != 0) { Console.WriteLine("DACL_ERROR=" + error); return 1; }
        Console.WriteLine("READY"); Console.Out.Flush();
        Thread.Sleep(30000); return 0;
    }
}
