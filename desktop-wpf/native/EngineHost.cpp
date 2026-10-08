#include "stdafx.h"
#include "EngineHost.h"
#include <mutex>
#include <stdexcept>
#include <algorithm>
#include <shellapi.h>
#include <tlhelp32.h>
#include <wincrypt.h>
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "delayimp.lib")
#pragma comment(lib, "shell32.lib")

namespace {
std::mutex outputLock;
unsigned long eventNumber = 0;
CMentoHUSTDlg* host = nullptr;
std::string ToBytes(const std::wstring& value, UINT page) {
    if (value.empty()) return {};
    int n = WideCharToMultiByte(page, 0, value.data(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    std::string result(n, 0); WideCharToMultiByte(page, 0, value.data(), (int)value.size(), &result[0], n, nullptr, nullptr); return result;
}
std::wstring FromBytes(const std::string& value, UINT page) {
    if (value.empty()) return {};
    int n = MultiByteToWideChar(page, MB_ERR_INVALID_CHARS, value.data(), (int)value.size(), nullptr, 0);
    if (!n) throw std::runtime_error("Invalid text encoding");
    std::wstring result(n, 0); MultiByteToWideChar(page, MB_ERR_INVALID_CHARS, value.data(), (int)value.size(), &result[0], n); return result;
}
std::string Encode(const std::string& value) {
    DWORD n = 0; CryptBinaryToStringA((const BYTE*)value.data(), (DWORD)value.size(), CRYPT_STRING_BASE64|CRYPT_STRING_NOCRLF, nullptr, &n);
    std::string result(n, 0); CryptBinaryToStringA((const BYTE*)value.data(), (DWORD)value.size(), CRYPT_STRING_BASE64|CRYPT_STRING_NOCRLF, &result[0], &n);
    if (!result.empty() && result.back() == 0) result.pop_back(); return result;
}
std::string Decode(const std::string& value) {
    if (value.empty()) return {};
    DWORD n = 0;
    if (!CryptStringToBinaryA(value.c_str(), (DWORD)value.size(), CRYPT_STRING_BASE64|CRYPT_STRING_STRICT, nullptr, &n, nullptr, nullptr) || n > 4096)
        throw std::runtime_error("Invalid command data");
    std::string result(n, 0);
    if (n) CryptStringToBinaryA(value.c_str(), (DWORD)value.size(), CRYPT_STRING_BASE64|CRYPT_STRING_STRICT, (BYTE*)&result[0], &n, nullptr, nullptr);
    if (result.find('\0') != std::string::npos) throw std::runtime_error("Invalid command text"); return result;
}
void WriteLine(const std::string& line) {
    DWORD written; std::string data = line + "\n";
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), data.data(), (DWORD)data.size(), &written, nullptr);
}
void Reply(const std::string& id, bool ok, const std::wstring& message) {
    std::lock_guard<std::mutex> guard(outputLock); WriteLine("REPLY\t" + id + (ok ? "\tOK\t" : "\tERROR\t") + Encode(ToBytes(message, CP_UTF8)));
}
void Event(int state, const std::wstring& message) {
    std::lock_guard<std::mutex> guard(outputLock);
    WriteLine("EVENT\t" + std::to_string(++eventNumber) + "\t" + std::to_string(state) + "\t" + Encode(ToBytes(message, CP_UTF8)));
}
std::wstring Read(const std::wstring& path, const wchar_t* section, const wchar_t* key, const wchar_t* fallback = L"") {
    wchar_t value[4096]; DWORD length = GetPrivateProfileStringW(section, key, fallback, value, 4096, path.c_str());
    if (length >= 4094) throw std::runtime_error("Configuration value too long"); return value;
}
bool LegacyRunning() {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot inspect running clients");
    PROCESSENTRY32W entry = {}; entry.dwSize = sizeof(entry); bool found = false;
    if (Process32FirstW(snapshot, &entry)) do { if (_wcsicmp(entry.szExeFile, L"MentoHUST.exe") == 0) { found = true; break; } } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot); return found;
}
void LoadRequest(CMentoHUSTDlg& owner, const std::string& accountUtf8, const std::string& adapter) {
    if (accountUtf8.size() < 8 || accountUtf8.compare(0, 7, "Account") || !std::all_of(accountUtf8.begin()+7, accountUtf8.end(), [](char c) { return c >= '0' && c <= '9'; }))
        throw std::runtime_error("Invalid account key");
    // Original getAddress expects a GUID suffix and a NUL-terminated m_nic[60].
    if (adapter.size() != 50 || adapter.compare(0, 12, "\\Device\\NPF_") || adapter[12] != '{' || adapter[49] != '}')
        throw std::runtime_error("Invalid pcap adapter key");
    GUID guid; if (CLSIDFromString(FromBytes(adapter.substr(12), CP_UTF8).c_str(), &guid) != S_OK) throw std::runtime_error("Invalid adapter GUID");
    const auto account = FromBytes(accountUtf8, CP_UTF8);
    const auto username = ToBytes(Read(owner.configPath, account.c_str(), L"Username"), CP_UTF8);
    const auto encoded = ToBytes(Read(owner.configPath, account.c_str(), L"Password"), CP_ACP);
    if (username.empty() || username.size() >= sizeof(owner.process.m_userName) || encoded.empty() || encoded.size() > 88)
        throw std::runtime_error("Invalid or oversized account credential");
    char decoded[128] = {}; bool ok = DecodeRuijie(decoded, encoded.c_str()) == 1;
    std::string password;
    try { if (ok) password = ToBytes(FromBytes(decoded, CP_ACP), CP_UTF8); }
    catch (...) { SecureZeroMemory(decoded, sizeof(decoded)); throw; }
    SecureZeroMemory(decoded, sizeof(decoded));
    if (!ok || password.empty() || password.size() >= sizeof(owner.process.m_password)) { if (!password.empty()) SecureZeroMemory(&password[0], password.size()); throw std::runtime_error("Invalid saved password"); }
    const auto ip = ToBytes(Read(owner.configPath, account.c_str(), L"IP", L"0.0.0.0"), CP_UTF8);
    const auto address = inet_addr(ip.c_str());
    if (address == INADDR_NONE && ip != "255.255.255.255") { SecureZeroMemory(&password[0], password.size()); throw std::runtime_error("Invalid account IP"); }
    std::wstring flags = Read(owner.configPath, L"Parameters", L"CertFlag", L"001110080200000");
    if (flags.size() != 15 || !std::all_of(flags.begin(), flags.end(), [](wchar_t c){ return c >= '0' && c <= '9'; })) { SecureZeroMemory(&password[0], password.size()); throw std::runtime_error("Invalid CertFlag; save settings first"); }
    auto digit = [&](int i) { return flags[i]-L'0'; };
    auto& p = owner.process;
    memcpy(p.m_userName, username.c_str(), username.size()+1); memcpy(p.m_password, password.c_str(), password.size()+1);
    SecureZeroMemory(&password[0], password.size()); memcpy(p.m_nic, adapter.c_str(), adapter.size()+1); p.m_ip = address;
    p.m_autoMin = digit(2)%2; p.m_bandMac = digit(3)%2; p.m_startMode = digit(4)%2; p.m_dhcpMode = digit(5)%4;
    p.m_timeout = (digit(6)*10 + digit(7))*1000; p.m_echoTime = (digit(8)*100+digit(9)*10+digit(10))*1000;
    p.m_autoReconnect = (digit(11)*100+digit(12)*10+digit(13))*60000; p.m_usePackage = digit(14)%2;
    const auto package = Read(owner.configPath, L"Parameters", L"PackagePath");
    if (package.size() >= MAX_PATH) throw std::runtime_error("Package path too long"); wcscpy_s(p.m_package, package.c_str());
    std::wstring extra = Read(owner.configPath, L"Parameters", L"ExtraFlag", L"15021");
    if (extra.size() < 2 || extra[0] < '0' || extra[0] > '9' || extra[1] < '0' || extra[1] > '9') throw std::runtime_error("Invalid ExtraFlag");
    p.m_restart = ((extra[0]-'0')*10+extra[1]-'0')*1000;
    p.m_bCernet = GetPrivateProfileIntW(L"Parameters", L"ClientType", 0, owner.configPath.c_str()) != 0;
    p.m_ruijie6Mode = GetPrivateProfileIntW(L"Parameters", L"Ruijie6Mode", 1, owner.configPath.c_str()) != 0;
}
bool CaptureExists(const std::string& adapter) {
    pcap_if_t* list = nullptr; char error[PCAP_ERRBUF_SIZE];
    if (pcap_findalldevs(&list, error) != 0) return false;
    bool found = false; for (auto item = list; item; item = item->next) if (adapter == item->name) { found = true; break; }
    pcap_freealldevs(list); return found;
}
DWORD WINAPI ReadCommands(LPVOID context) {
    HWND window = static_cast<HWND>(context);
    std::string line; char bytes[512]; DWORD count;
    while (ReadFile(GetStdHandle(STD_INPUT_HANDLE), bytes, sizeof(bytes), &count, nullptr) && count) {
        for (DWORD i = 0; i < count; i++) {
            if (bytes[i] == '\n') { auto command = new std::string(line); line.clear(); if (!::PostMessage(window, HOST_COMMAND, 0, (LPARAM)command)) delete command; }
            else { if (line.size() >= 8192 || bytes[i] == '\0') { ::PostMessage(window, HOST_EXIT, 0, 0); return 0; } if (bytes[i] != '\r') line.push_back(bytes[i]); }
        }
    }
    ::PostMessage(window, HOST_EXIT, 0, 0); // Parent closed its inherited pipe.
    return 0;
}
}

CString LoadString(UINT id) { CString result; result.LoadString(id); return result; }
CString GetAppPath() { CString result(host->configPath.c_str()); int slash = result.ReverseFind('\\'); return result.Left(slash+1); }

bool CMentoHUSTDlg::Initialize() {
    if (!CreateEx(0, AfxRegisterWndClass(0), _T("MentoHUST.Engine.Hidden"), 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr)) return false;
    for (int i = 0; i < 3; i++) controls[i].CreateEx(0, _T("STATIC"), _T(""), WS_CHILD, 0, 0, 0, 0, m_hWnd, (HMENU)(INT_PTR)(i+1));
    return true;
}
CWnd* CMentoHUSTDlg::GetDlgItem(int id) const { return const_cast<CWnd*>(&controls[id == IDC_CB_ACCOUNT ? 1 : id == IDC_CB_ADAPTER ? 2 : 0]); }
void CMentoHUSTDlg::Output(LPCTSTR text, int type) {
    CString value(text);
    if (value == LoadString(IDS_STATE_SUCCESS)) state = 5;
    else if (value == LoadString(IDS_STATE_DISCONNECT)) state = 0;
    else if (value == LoadString(IDS_STATE_START)) state = 1;
    else if (value == LoadString(IDS_STATE_USERNAME)) state = 2;
    else if (value == LoadString(IDS_STATE_PASSWORD)) state = 3;
    else if (value == LoadString(IDS_DHCP_START)) { authenticatedBeforeDhcp = state == 5; state = 4; }
    else if (value == LoadString(IDS_DHCP_END)) state = authenticatedBeforeDhcp ? 5 : 1;
    else if (value == LoadString(IDS_CERT_FAILED) || value == LoadString(IDS_CERT_DOWN) || value == LoadString(IDS_START_ERROR) || value == LoadString(IDS_USER_ERROR) || value == LoadString(IDS_PASS_ERROR)) state = 6;
    // Old code prints an MD5 value; omit this derived credential material from IPC/logs.
    if (value.Left(LoadString(IDS_MD5_STRING).GetLength()) == LoadString(IDS_MD5_STRING)) return;
    Event(state.load(), std::wstring((LPCTSTR)value));
}
int CMentoHUSTDlg::MsgBox(LPCTSTR text, UINT) {
    // openAdapter already closed this handle on a filter error; avoid a second close on host exit.
    if (CString(text) == LoadString(IDS_FILTER_ERROR)) process.m_hPcap = nullptr;
    state = 6; Output(text); return IDOK;
}
void CMentoHUSTDlg::ChangeTrayIcon() {} // State/log events replace native tray operations.
void CMentoHUSTDlg::Stop() {
    KillTimer(SEND_TIMER); KillTimer(TEST_TIMER);
    if (active) { process.endCert(); active = false; }
    SecureZeroMemory(process.m_userName, sizeof(process.m_userName)); SecureZeroMemory(process.m_password, sizeof(process.m_password));
    state = 0;
}
void CMentoHUSTDlg::Command(const std::string& line) {
    std::vector<std::string> parts; size_t start = 0, tab;
    do { tab = line.find('\t', start); parts.push_back(line.substr(start, tab == std::string::npos ? tab : tab-start)); start = tab+1; } while (tab != std::string::npos);
    std::string id = parts.empty() ? "0" : parts[0];
    if (id.empty() || id.size() > 10 || !std::all_of(id.begin(), id.end(), [](char c){return c >= '0' && c <= '9';})) { Reply("0", false, L"Invalid sequence"); return; }
    try {
        if (parts.size() == 2 && parts[1] == "PING") { Reply(id, true, L"Engine protocol 1"); return; }
        if (parts.size() == 2 && parts[1] == "STOP") { Stop(); Event(0, L"认证已停止"); Reply(id, true, L""); return; }
        if (parts.size() == 2 && parts[1] == "QUIT") {
            // Match legacy exit: end this process without sending an explicit EAP Logoff.
            // Only STOP calls endCert. OS teardown closes capture/thread handles.
            Reply(id, true, L""); if (active) ExitProcess(0);
            Stop(); PostMessage(HOST_EXIT, 0, 0); return;
        }
        if (parts.size() != 4 || (parts[1] != "START" && parts[1] != "VALIDATE")) throw std::runtime_error("Unknown command");
        if (active) throw std::runtime_error("Authentication is already active");
        std::string account = Decode(parts[2]), adapter = Decode(parts[3]);
        LoadRequest(*this, account, adapter);
        if (parts[1] == "VALIDATE") { SecureZeroMemory(process.m_password, sizeof(process.m_password)); Reply(id, true, L"配置与请求格式检查通过"); return; }
        if (LegacyRunning()) { SecureZeroMemory(process.m_password, sizeof(process.m_password)); Reply(id, false, L"旧版 MentoHUST 正在运行，请先自行退出旧版再使用 WPF 认证。"); return; }
        if (offline) throw std::runtime_error("Offline checks cannot authenticate");
        if (!captureReady || !CaptureExists(adapter)) throw std::runtime_error("Selected capture device is unavailable");
        state = 1; Event(1, L"正在启动认证"); process.startCert();
        // startCert disables the account control only after it has created its thread.
        active = !GetDlgItem(IDC_CB_ACCOUNT)->IsWindowEnabled();
        if (!active) throw std::runtime_error("Authentication could not start; see engine log");
        Reply(id, true, L"");
    } catch (const std::exception& error) {
        if (!active) SecureZeroMemory(process.m_password, sizeof(process.m_password));
        Reply(id, false, FromBytes(error.what(), CP_UTF8));
    }
}
LRESULT CMentoHUSTDlg::WindowProc(UINT message, WPARAM w, LPARAM l) {
    if (message == HOST_COMMAND) { auto line = reinterpret_cast<std::string*>(l); Command(*line); delete line; return 0; }
    if (message == HOST_EXIT) { if (active) ExitProcess(0); Stop(); PostQuitMessage(0); return 0; }
    if (message == WM_SYSCOMMAND) {
        if (w == END_CERT) Stop(); // Ignore core SC_MINIMIZE; WPF handles success preference.
        return 0;
    }
    if (message == WM_TIMER) {
        if (active && w == SEND_TIMER && process.switchState(process.m_state) == -1) { Stop(); MsgBox(LoadString(IDS_SEND_ERROR)); }
        else if (active && w == TEST_TIMER) process.sendTestPacket();
        return 0;
    }
    return CWnd::WindowProc(message, w, l);
}

class EngineApp : public CWinApp {
public:
    CMentoHUSTDlg window;
    BOOL InitInstance() override {
        CWinApp::InitInstance(); SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
        if (LoadString(IDS_STATE_SUCCESS).IsEmpty() || LoadString(IDS_MD5_STRING).IsEmpty()) return FALSE;
        int count; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
        if (count != 3 && count != 4) { LocalFree(args); return FALSE; }
        if (wcscmp(args[1], L"--config") || (count == 4 && wcscmp(args[3], L"--offline"))) { LocalFree(args); return FALSE; }
        window.configPath = args[2]; window.offline = count == 4; LocalFree(args);
        if (window.configPath.empty() || window.configPath[0] == '\\' || (window.configPath.size() < 3 || window.configPath[1] != ':')) return FALSE;
        wchar_t system[MAX_PATH]; GetSystemDirectoryW(system, MAX_PATH);
        std::wstring dll = std::wstring(system) + L"\\Npcap\\wpcap.dll";
        HMODULE capture = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!capture) { dll = std::wstring(system) + L"\\wpcap.dll"; capture = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH); }
        window.captureReady = capture != nullptr && !window.offline;
        host = &window; m_pMainWnd = &window;
        if (!window.Initialize()) return FALSE;
        { std::lock_guard<std::mutex> guard(outputLock); WriteLine(std::string("READY\t1\t") + (window.captureReady ? "1" : "0")); }
        HANDLE reader = ::CreateThread(nullptr, 0, ReadCommands, window.m_hWnd, 0, nullptr);
        if (!reader) return FALSE; CloseHandle(reader); return TRUE;
    }
    int ExitInstance() override { window.Stop(); return CWinApp::ExitInstance(); }
} engineApp;
