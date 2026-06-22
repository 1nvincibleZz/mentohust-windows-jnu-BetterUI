from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
RC = ROOT / "VS2012" / "MentoHUST" / "MentoHUST.rc"
DLG = ROOT / "VS2012" / "MentoHUST" / "Source" / "MentoHUSTDlg.cpp"
PROCESS = ROOT / "VS2012" / "MentoHUST" / "Source" / "Process.cpp"
PROCESS_H = ROOT / "VS2012" / "MentoHUST" / "Source" / "Process.h"
PARAMETER = ROOT / "VS2012" / "MentoHUST" / "Source" / "ParameterPage.cpp"
MANIFEST = ROOT / "VS2012" / "MentoHUST" / "res" / "exe.manifest"
PROJECT = ROOT / "VS2012" / "MentoHUST" / "MentoHUST.vcxproj"
PROJECT_FILTERS = ROOT / "VS2012" / "MentoHUST" / "MentoHUST.vcxproj.filters"


def read(path: Path) -> str:
    data = path.read_bytes()
    for encoding in ("utf-8", "gbk"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace")


def block_between(text: str, start: str, end: str) -> str:
    pattern = re.compile(re.escape(start) + r"(.*?)" + re.escape(end), re.S)
    match = pattern.search(text)
    assert match, f"missing block {start!r}"
    return match.group(1)


def function_body(text: str, signature: str) -> str:
    start = text.find(signature)
    assert start != -1, f"missing function {signature}"
    brace = text.find("{", start)
    assert brace != -1, f"missing function body {signature}"
    depth = 0
    for index in range(brace, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return text[brace + 1:index]
    raise AssertionError(f"unterminated function {signature}")


def test_settings_controls_are_visible():
    rc = read(RC)
    account = block_between(rc, "IDD_CFG_ACCOUNT DIALOG", "IDD_CFG_PARAMETER DIALOG")
    parameter = block_between(rc, "IDD_CFG_PARAMETER DIALOG", "#ifndef _MAC")

    assert "NOT WS_VISIBLE" not in account
    assert "NOT WS_VISIBLE" not in parameter

    for control in [
        "IDC_ED_USERNAME",
        "IDC_ED_PASSWORD",
        "IDC_IPADDRESS",
        "IDC_BN_ADD",
        "IDC_BN_DEL",
        "IDC_LS_ACCOUNT",
    ]:
        line = next(line for line in account.splitlines() if control in line)
        assert "NOT WS_VISIBLE" not in line

    for control in [
        "IDC_CK_AUTORUN",
        "IDC_CK_AUTOCERT",
        "IDC_CK_AUTOMIN",
        "IDC_CK_BANDMAC",
        "IDC_CB_STARTMODE",
        "IDC_CB_DHCPMODE",
        "IDC_ED_TIMEOUT",
        "IDC_CK_PACKAGE",
        "IDC_BN_BROWSER",
        "IDC_ED_PATH",
        "IDC_ED_RECONNECT",
    ]:
        line = next(line for line in parameter.splitlines() if control in line)
        assert "NOT WS_VISIBLE" not in line

    assert 'IDS_BN_CONNECT          "认证"' in rc


def test_main_window_branding_has_no_legacy_logo_or_ruijie463_copy():
    rc = read(RC)
    dlg_cpp = read(DLG)
    dlg_h = read(ROOT / "VS2012" / "MentoHUST" / "Source" / "MentoHUSTDlg.h")
    main_dialog = block_between(rc, "IDD_MENTOHUST_DIALOG DIALOGEX", "IDD_CONFIGBOX DIALOG")
    init_interface = function_body(dlg_cpp, "void CMentoHUSTDlg::InitInterface()")

    assert 'CAPTION "暨南大学定制版MentoHUST"' in main_dialog
    assert "锐捷4.63" not in main_dialog
    assert "4.63" not in main_dialog
    assert "IDC_SC_LOGO" not in main_dialog
    assert "m_linkLogo" not in dlg_h
    assert "OnScLogo" not in dlg_h
    assert "IDC_SC_LOGO" not in dlg_cpp
    assert "m_linkLogo" not in init_interface
    assert "SetURL" not in init_interface
    assert "LinkURL" not in init_interface


def test_about_window_version_manifest_and_first_party_sources_have_no_author_signatures():
    rc = read(RC)
    dlg_cpp = read(DLG)
    manifest = read(MANIFEST)
    string_list = read(ROOT / "VS2012" / "MentoHUST" / "Source" / "StringList.cpp")
    process = read(PROCESS)
    project = read(PROJECT)
    project_filters = read(PROJECT_FILTERS)
    about_dialog = block_between(rc, "IDD_ABOUTBOX DIALOG", "IDD_MENTOHUST_DIALOG DIALOGEX")

    forbidden_terms = [
        "华梦",
        "华茗",
        "HustMoon",
        "HustMoon Studio",
        "HustMoon Software",
        "联系作者",
        "联系方式",
        "检查更新",
        "MentoHUST感言",
        "Athlonxeon",
        "Snowwings",
        "Soar",
        "freevanx",
        "JimmyKing",
        "HCNE",
        "BYHH",
        "bynix",
        "byunix",
        "code.google.com/p/mentohust",
    ]

    for text in [rc, dlg_cpp, manifest, string_list, process]:
        for term in forbidden_terms:
            assert term not in text

    assert "SS_BITMAP" not in about_dialog
    assert "IDB_LOGO" not in rc
    assert "IDC_SC_CONTRACT" not in about_dialog
    assert "IDC_SC_CHECKNEW" not in about_dialog
    assert "m_LinkContact" not in dlg_cpp
    assert "m_LinkUpdate" not in dlg_cpp
    assert "OnScContact" not in dlg_cpp
    assert "CompanyName" not in rc
    assert "LegalCopyright" not in rc
    assert "HyperLink.cpp" not in project
    assert "Hyperlink.h" not in project
    assert "HyperLink.cpp" not in project_filters
    assert "Hyperlink.h" not in project_filters
    assert not (ROOT / "VS2012" / "MentoHUST" / "Source" / "Other" / "HyperLink.cpp").exists()
    assert not (ROOT / "VS2012" / "MentoHUST" / "Source" / "Other" / "Hyperlink.h").exists()


def test_connect_requires_account_and_adapter():
    body = function_body(read(DLG), "void CMentoHUSTDlg::OnOK()")
    assert "if (!GetAccount() || !GetAdapter())" in body
    assert "m_Process.startCert();" in body


def test_ruijie6_mode_is_configurable_and_default_enabled():
    process_h = read(PROCESS_H)
    process_cpp = read(PROCESS)
    dlg_cpp = read(DLG)
    assert "m_ruijie6Mode" in process_h
    assert "m_ruijie6Mode = TRUE;" in process_cpp
    assert "Ruijie6Mode" in dlg_cpp
    assert '_T("Ruijie6Mode")' in dlg_cpp


def test_state_machine_restores_all_auth_steps():
    body = function_body(read(PROCESS), "int CProcess::switchState(int type)")
    expected = {
        "IDT_DHCP": "return getNewIP();",
        "IDT_START": "return sendStartPacket();",
        "IDT_IDENTITY": "return sendIdentityPacket();",
        "IDT_CHALLENGE": "return sendChallengePacket();",
        "IDT_WAITECHO": "return waitEchoPacket();",
        "IDT_ECHO": "return sendEchoPacket();",
        "IDT_DISCONNECT": "return sendLogoffPacket();",
    }
    for state, action in expected.items():
        assert f"case {state}:" in body
        assert action in body

    assert "IDS_START_ERROR" in body
    assert "IDS_USER_ERROR" in body
    assert "IDS_PASS_ERROR" in body


def test_identity_and_challenge_packets_send_eap_responses():
    process_cpp = read(PROCESS)
    identity = function_body(process_cpp, "int CProcess::sendIdentityPacket()")
    challenge = function_body(process_cpp, "int CProcess::sendChallengePacket()")

    assert "fillEtherAddr(0x888E0100)" in identity
    assert "m_sendPacket[0x12] = 2" in identity
    assert "m_sendPacket[0x16] = 1" in identity
    assert "pcap_sendpacket" in identity

    assert "fillMd5Packet" in challenge
    assert "checkPass" in challenge
    assert "m_sendPacket[0x16] = 4" in challenge
    assert "pcap_sendpacket" in challenge


def test_ruijie6_success_enters_authenticated_state_without_killing_client():
    process_cpp = read(PROCESS)
    success_section = block_between(process_cpp, "buf[0x12]==0x03", "else if (buf[0x0F]==0x00 && buf[0x12]==0x01")
    assert "switchState(IDT_ECHO)" in success_section
    assert "FindAndKillProcessByName" not in success_section

    echo = function_body(process_cpp, "int CProcess::sendEchoPacket()")
    assert "m_ruijie6Mode" in echo
    assert "pcap_sendpacket(m_hPcap, m_sendPacket, 0x2D)" in echo


def test_autorun_uses_current_user_run_key_without_admin_rights():
    body = function_body(read(PARAMETER), "void CParameterPage::SetAutoRun(BOOL bAutoRun)")

    assert "HKEY_CURRENT_USER" in body
    assert "HKEY_LOCAL_MACHINE" not in body
    assert "RegCreateKeyEx" in body
    assert "RegOpenKeyEx" not in body
    assert "CString strValue" in body
    assert 'strValue.Format(_T("\\"%s\\""), FileName)' in body
    assert "(strValue.GetLength() + 1) * sizeof(TCHAR)" in body
    assert "ERROR_FILE_NOT_FOUND" in body
