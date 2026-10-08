$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$taskVerified = @()
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]
    $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]
    $taskDeadline = [DateTime]::UtcNow.AddSeconds($(if ($MentoProbeOnly) { 0 } else { 30 }))
    do {
        $taskProfile = [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles() | Where-Object {
            $_.NetworkAdapter -and $_.NetworkAdapter.NetworkAdapterId -eq $MentoAdapterId -and
            $_.GetNetworkConnectivityLevel().ToString() -eq 'InternetAccess'
        } | Select-Object -First 1
        if ($taskProfile -or $MentoProbeOnly) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskProfile) { throw '所选认证网卡尚未获得 Internet 连接，未结束进程或开启热点。' }
    $taskCapability = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($taskProfile)
    if ($taskCapability.ToString() -ne 'Enabled') { throw "Windows 移动热点不可用：$taskCapability。未结束进程。" }
    $taskManager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($taskProfile)
    $taskResultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]
    $taskAsTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    if (-not $taskAsTask) { throw '当前 Windows 运行环境不支持热点异步接口；未结束进程。' }
    # Snapshot once; never loop-kill and never stop RJSuService or MentoHUST.
    $taskTargets = @(Get-Process -Name '8021x' -ErrorAction SilentlyContinue)
    if ($taskTargets.Count -gt 0) {
        # MainModule may be empty for this client. Query the image without reading process modules.
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public sealed class MentoProcessAccess : IDisposable {
    private IntPtr handle;
    private bool attempted;
    public string ImagePath { get; private set; }
    private MentoProcessAccess(IntPtr handle, string path) { this.handle = handle; ImagePath = path; }
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    public static MentoProcessAccess Open(int id) {
        // Query-limited + terminate + synchronize; no broad query-information rights.
        IntPtr handle = OpenProcess(0x101001, false, id);
        if (handle == IntPtr.Zero) {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) return null; // Snapshot target exited before it could be opened.
            throw new Win32Exception(error);
        }
        try {
            uint state = WaitForSingleObject(handle, 0);
            if (state == 0) return null;
            if (state == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = new StringBuilder(32768); int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            string path = buffer.ToString();
            if (!string.Equals(Path.GetFileName(path), "8021x.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标进程不是 8021x.exe，已取消操作。");
            var result = new MentoProcessAccess(handle, path);
            handle = IntPtr.Zero; return result;
        } finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }
    public bool TerminateOnce() {
        if (handle == IntPtr.Zero) throw new ObjectDisposedException("MentoProcessAccess");
        if (attempted) throw new InvalidOperationException("不会重复结束同一进程。");
        attempted = true;
        uint state = WaitForSingleObject(handle, 0);
        if (state == 0) return false;
        if (state == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!TerminateProcess(handle, 1)) {
            int error = Marshal.GetLastWin32Error();
            if (WaitForSingleObject(handle, 0) == 0) return false;
            throw new Win32Exception(error);
        }
        state = WaitForSingleObject(handle, 5000);
        if (state == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (state != 0) throw new TimeoutException("8021x.exe 未及时退出，未开启热点。");
        return true;
    }
    public void Dispose() {
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
    }
}
'@
    }
    foreach ($taskTarget in $taskTargets) {
        try { $taskProcessHandle = [MentoProcessAccess]::Open($taskTarget.Id) }
        catch { throw "无法查询或结束 8021x.exe；若为拒绝访问，请以管理员身份运行客户端。详情：$($_.Exception.Message)" }
        if ($null -ne $taskProcessHandle) { $taskVerified += $taskProcessHandle }
    }
    if ($MentoProbeOnly) {
        Write-Output "只读检查：移动热点可用，当前状态 $($taskManager.TetheringOperationalState)；已核对 $($taskVerified.Count) 个 8021x.exe 的路径、结束权限及等待权限。未结束进程或启动热点。"
        exit 0
    }
    if ($taskVerified.Count -gt 0) {
        $taskStopped = 0
        foreach ($taskProcessHandle in $taskVerified) {
            try { if ($taskProcessHandle.TerminateOnce()) { $taskStopped++ } }
            catch { throw "结束 8021x.exe 失败：$($_.Exception.Message)" }
        }
        if ($taskStopped -gt 0) { Write-Output '已结束 8021x.exe 一次。' }
        else { Write-Output '8021x.exe 已退出，跳过结束进程。' }
    } else { Write-Output '8021x.exe 未运行，跳过结束进程。' }
    if ($taskManager.TetheringOperationalState.ToString() -eq 'On') {
        Write-Output 'Windows 移动热点已经开启，保留当前热点。'
        exit 0
    }
    $taskOperation = $taskManager.StartTetheringAsync()
    $taskAwait = $taskAsTask.MakeGenericMethod($taskResultType).Invoke($null, @($taskOperation))
    if (-not $taskAwait.Wait(30000)) { throw 'Windows 移动热点启动超时，请在系统设置中检查状态。' }
    $taskResult = $taskAwait.Result
    if ($taskResult.Status.ToString() -ne 'Success') { throw "移动热点启动失败：$($taskResult.Status) $($taskResult.AdditionalErrorMessage)" }
    Write-Output 'Windows 移动热点已开启，名称和密码沿用系统设置。'
    exit 0
} catch {
    Write-Output $_.Exception.Message
    exit 1
} finally {
    foreach ($taskProcessHandle in $taskVerified) { $taskProcessHandle.Dispose() }
}
