#pragma once
#include "resource.h"
#include "Process.h"
#include <string>
#include <vector>
#include <atomic>

#define SEND_TIMER (WM_USER+102)
#define TEST_TIMER (WM_USER+103)
#define END_CERT (WM_USER+105)
#define HOST_COMMAND (WM_APP+1)
#define HOST_EXIT (WM_APP+2)

CString LoadString(UINT id);
CString GetAppPath();
int DecodeRuijie(char* dst, const char* src);

// Compatibility boundary for unchanged CProcess. This window is never displayed.
class CMentoHUSTDlg : public CWnd
{
public:
    CProcess process;
    CString m_sServerMsg;
    bool active = false, offline = false, captureReady = false;
    std::atomic<int> state{0};
    bool authenticatedBeforeDhcp = false;
    CWnd controls[3];
    std::wstring configPath;
    bool Initialize();
    CWnd* GetDlgItem(int id) const;
    void SetDlgItemText(int, LPCTSTR) {}
    void ShowWindow(int) {} // Core requests visibility; only WPF owns visible windows.
    void Output(LPCTSTR text, int type = 0);
    int MsgBox(LPCTSTR text, UINT type = MB_OK|MB_ICONWARNING);
    void ChangeTrayIcon();
    void Stop();
    void Command(const std::string& line);
protected:
    LRESULT WindowProc(UINT message, WPARAM w, LPARAM l) override;
};
