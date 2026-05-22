#include <windows.h>
#include <shellapi.h>
#include <sddl.h>
#include <lm.h>

#include <algorithm>
#include <string>
#include <vector>

namespace {

constexpr wchar_t kWindowClassName[] = L"PowerMenuGuardWindow";
constexpr wchar_t kWindowTitle[] = L"Power Menu Guard";
constexpr wchar_t kPolicyPath[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer";
constexpr wchar_t kPolicyValue[] = L"NoClose";
constexpr wchar_t kProfileListPath[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList";

constexpr int kStatusId = 1001;
constexpr int kNoteId = 1002;
constexpr int kCurrentUserRadioId = 1003;
constexpr int kStandardUserRadioId = 1004;
constexpr int kUserLabelId = 1005;
constexpr int kUserComboId = 1006;
constexpr int kEnableId = 1007;
constexpr int kDisableId = 1008;
constexpr int kRefreshId = 1009;
constexpr int kCloseId = 1010;

enum class TargetMode {
    kCurrentUser,
    kStandardUser,
};

enum class CommandAction {
    kGui,
    kHelp,
    kStatus,
    kEnable,
    kDisable,
};

struct LocalUserInfo {
    std::wstring name;
    std::wstring sid;
    std::wstring profile_path;
    bool is_admin = false;
    bool has_profile = false;
};

struct TargetSpec {
    TargetMode mode = TargetMode::kCurrentUser;
    std::wstring user_name;
};

struct HiveContext {
    HKEY root = nullptr;
    std::wstring policy_path;
    std::wstring loaded_mount;
    bool unload_required = false;
};

struct ParsedCommand {
    CommandAction action = CommandAction::kGui;
    std::wstring user_name;
};

HWND g_status_label = nullptr;
HWND g_note_label = nullptr;
HWND g_current_user_radio = nullptr;
HWND g_standard_user_radio = nullptr;
HWND g_user_label = nullptr;
HWND g_user_combo = nullptr;
HWND g_enable_button = nullptr;
HWND g_disable_button = nullptr;
HWND g_refresh_button = nullptr;
HWND g_close_button = nullptr;
HFONT g_ui_font = nullptr;

std::wstring g_current_user_name;
std::vector<LocalUserInfo> g_standard_users;
TargetMode g_selected_mode = TargetMode::kCurrentUser;
std::wstring g_selected_standard_user_name;
bool g_is_elevated = false;

std::wstring FormatErrorMessage(DWORD error_code) {
    LPWSTR buffer = nullptr;
    const DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS;
    const DWORD size = FormatMessageW(
        flags,
        nullptr,
        error_code,
        MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
        reinterpret_cast<LPWSTR>(&buffer),
        0,
        nullptr);

    std::wstring message;
    if (size != 0 && buffer != nullptr) {
        message.assign(buffer, size);
        while (!message.empty() &&
               (message.back() == L'\r' || message.back() == L'\n' || message.back() == L' ' || message.back() == L'.')) {
            message.pop_back();
        }
        LocalFree(buffer);
    } else {
        message = L"Unknown error";
    }

    return message;
}

void WriteLine(const std::wstring& message) {
    HANDLE handle = GetStdHandle(STD_OUTPUT_HANDLE);
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE) {
        return;
    }

    const std::wstring line = message + L"\r\n";
    DWORD console_mode = 0;
    if (GetConsoleMode(handle, &console_mode) != 0) {
        DWORD written = 0;
        WriteConsoleW(handle, line.c_str(), static_cast<DWORD>(line.size()), &written, nullptr);
        return;
    }

    const int utf8_size = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
    if (utf8_size <= 0) {
        return;
    }

    std::string utf8(static_cast<size_t>(utf8_size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), utf8.data(), utf8_size, nullptr, nullptr);

    DWORD written = 0;
    WriteFile(handle, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
}

bool IsProcessElevated() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return false;
    }

    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
    CloseHandle(token);
    return ok != FALSE && elevation.TokenIsElevated != 0;
}

std::wstring GetCurrentUserNameSimple() {
    DWORD size = 256;
    std::wstring name(size, L'\0');
    if (!GetUserNameW(name.data(), &size)) {
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || size == 0) {
            return L"Current User";
        }

        name.assign(size, L'\0');
        if (!GetUserNameW(name.data(), &size) || size == 0) {
            return L"Current User";
        }
    }

    name.resize(size - 1);
    return name;
}

std::wstring GetExecutablePath() {
    std::wstring path(MAX_PATH, L'\0');
    DWORD size = 0;
    for (;;) {
        size = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (size == 0) {
            return L"";
        }
        if (size < path.size() - 1) {
            path.resize(size);
            return path;
        }
        path.resize(path.size() * 2);
    }
}

bool EnablePrivilege(const wchar_t* privilege_name, std::wstring* error_message) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) {
        *error_message = L"Failed to open process token: " + FormatErrorMessage(GetLastError());
        return false;
    }

    LUID luid{};
    if (!LookupPrivilegeValueW(nullptr, privilege_name, &luid)) {
        const DWORD error = GetLastError();
        CloseHandle(token);
        *error_message = L"Failed to lookup privilege: " + FormatErrorMessage(error);
        return false;
    }

    TOKEN_PRIVILEGES privileges{};
    privileges.PrivilegeCount = 1;
    privileges.Privileges[0].Luid = luid;
    privileges.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;

    if (!AdjustTokenPrivileges(token, FALSE, &privileges, sizeof(privileges), nullptr, nullptr)) {
        const DWORD error = GetLastError();
        CloseHandle(token);
        *error_message = L"Failed to adjust token privileges: " + FormatErrorMessage(error);
        return false;
    }

    const DWORD error = GetLastError();
    CloseHandle(token);
    if (error == ERROR_NOT_ALL_ASSIGNED) {
        *error_message = L"Privilege is not assigned to the current token.";
        return false;
    }

    return true;
}

bool FileExists(const std::wstring& path) {
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

std::wstring ExpandEnvironmentPath(const std::wstring& input) {
    const DWORD required = ExpandEnvironmentStringsW(input.c_str(), nullptr, 0);
    if (required == 0) {
        return input;
    }

    std::wstring expanded(required, L'\0');
    ExpandEnvironmentStringsW(input.c_str(), expanded.data(), required);
    if (!expanded.empty() && expanded.back() == L'\0') {
        expanded.pop_back();
    }
    return expanded;
}

bool ReadRegistryString(HKEY root, const std::wstring& subkey, const std::wstring& value_name, std::wstring* value) {
    HKEY key = nullptr;
    const LONG status = RegOpenKeyExW(root, subkey.c_str(), 0, KEY_QUERY_VALUE, &key);
    if (status != ERROR_SUCCESS) {
        return false;
    }

    DWORD type = 0;
    DWORD size = 0;
    LONG query_status = RegQueryValueExW(key, value_name.c_str(), nullptr, &type, nullptr, &size);
    if (query_status != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) || size == 0) {
        RegCloseKey(key);
        return false;
    }

    std::wstring buffer(size / sizeof(wchar_t), L'\0');
    query_status = RegQueryValueExW(
        key,
        value_name.c_str(),
        nullptr,
        &type,
        reinterpret_cast<LPBYTE>(buffer.data()),
        &size);
    RegCloseKey(key);

    if (query_status != ERROR_SUCCESS) {
        return false;
    }

    while (!buffer.empty() && buffer.back() == L'\0') {
        buffer.pop_back();
    }

    *value = (type == REG_EXPAND_SZ) ? ExpandEnvironmentPath(buffer) : buffer;
    return true;
}

bool LookupUserSidString(const std::wstring& user_name, std::wstring* sid_string, std::wstring* error_message) {
    DWORD sid_size = 0;
    DWORD domain_size = 0;
    SID_NAME_USE sid_use = SidTypeUnknown;

    LookupAccountNameW(nullptr, user_name.c_str(), nullptr, &sid_size, nullptr, &domain_size, &sid_use);
    const DWORD lookup_error = GetLastError();
    if (lookup_error != ERROR_INSUFFICIENT_BUFFER) {
        *error_message = L"Failed to lookup account: " + FormatErrorMessage(lookup_error);
        return false;
    }

    std::vector<BYTE> sid_buffer(sid_size);
    std::wstring domain_name(domain_size == 0 ? 1 : domain_size, L'\0');

    if (!LookupAccountNameW(
            nullptr,
            user_name.c_str(),
            sid_buffer.data(),
            &sid_size,
            domain_name.data(),
            &domain_size,
            &sid_use)) {
        *error_message = L"Failed to lookup account SID: " + FormatErrorMessage(GetLastError());
        return false;
    }

    LPWSTR sid_text = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<PSID>(sid_buffer.data()), &sid_text)) {
        *error_message = L"Failed to convert SID to string: " + FormatErrorMessage(GetLastError());
        return false;
    }

    *sid_string = sid_text;
    LocalFree(sid_text);
    return true;
}

bool GetProfilePathForSid(const std::wstring& sid, std::wstring* profile_path) {
    const std::wstring profile_key = std::wstring(kProfileListPath) + L"\\" + sid;
    return ReadRegistryString(HKEY_LOCAL_MACHINE, profile_key, L"ProfileImagePath", profile_path);
}

bool IsHiveLoaded(const std::wstring& hive_name) {
    HKEY key = nullptr;
    const LONG status = RegOpenKeyExW(HKEY_USERS, hive_name.c_str(), 0, KEY_QUERY_VALUE, &key);
    if (status == ERROR_SUCCESS) {
        RegCloseKey(key);
        return true;
    }
    return false;
}

std::vector<LocalUserInfo> EnumerateStandardUsers(std::wstring* warning_message) {
    std::vector<LocalUserInfo> users;
    LPBYTE buffer = nullptr;
    DWORD entries_read = 0;
    DWORD total_entries = 0;
    DWORD resume_handle = 0;

    do {
        entries_read = 0;
        total_entries = 0;
        buffer = nullptr;

        const NET_API_STATUS status = NetUserEnum(
            nullptr,
            4,
            FILTER_NORMAL_ACCOUNT,
            &buffer,
            MAX_PREFERRED_LENGTH,
            &entries_read,
            &total_entries,
            &resume_handle);

        if (status != NERR_Success && status != ERROR_MORE_DATA) {
            *warning_message = L"Failed to enumerate local users: " + FormatErrorMessage(status);
            if (buffer != nullptr) {
                NetApiBufferFree(buffer);
            }
            break;
        }

        const USER_INFO_4* entries = reinterpret_cast<USER_INFO_4*>(buffer);
        for (DWORD i = 0; i < entries_read; ++i) {
            const USER_INFO_4& entry = entries[i];
            if (entry.usri4_name == nullptr || entry.usri4_name[0] == L'\0') {
                continue;
            }
            if ((entry.usri4_flags & UF_ACCOUNTDISABLE) != 0) {
                continue;
            }

            const std::wstring user_name = entry.usri4_name;
            if (!user_name.empty() && user_name.back() == L'$') {
                continue;
            }

            const bool is_admin = entry.usri4_priv == USER_PRIV_ADMIN;
            if (is_admin) {
                continue;
            }

            LocalUserInfo user{};
            user.name = user_name;
            user.is_admin = false;

            std::wstring sid_error;
            if (!LookupUserSidString(user.name, &user.sid, &sid_error)) {
                continue;
            }

            user.has_profile = GetProfilePathForSid(user.sid, &user.profile_path);
            users.push_back(user);
        }

        if (buffer != nullptr) {
            NetApiBufferFree(buffer);
            buffer = nullptr;
        }

        if (status == NERR_Success) {
            break;
        }
    } while (true);

    std::sort(users.begin(), users.end(), [](const LocalUserInfo& left, const LocalUserInfo& right) {
        return _wcsicmp(left.name.c_str(), right.name.c_str()) < 0;
    });

    return users;
}

const LocalUserInfo* FindStandardUser(const std::wstring& user_name) {
    const auto it = std::find_if(g_standard_users.begin(), g_standard_users.end(), [&](const LocalUserInfo& user) {
        return _wcsicmp(user.name.c_str(), user_name.c_str()) == 0;
    });

    return it == g_standard_users.end() ? nullptr : &(*it);
}

std::wstring BuildMountName() {
    return L"PowerMenuGuardTemp_" + std::to_wstring(GetCurrentProcessId()) + L"_" + std::to_wstring(GetTickCount64());
}

bool PrepareHiveContext(const TargetSpec& target, HiveContext* context, std::wstring* error_message) {
    context->root = nullptr;
    context->policy_path.clear();
    context->loaded_mount.clear();
    context->unload_required = false;

    if (target.mode == TargetMode::kCurrentUser) {
        context->root = HKEY_CURRENT_USER;
        context->policy_path = kPolicyPath;
        return true;
    }

    if (target.user_name.empty()) {
        *error_message = L"No standard user selected.";
        return false;
    }

    std::wstring sid;
    if (const LocalUserInfo* user = FindStandardUser(target.user_name)) {
        sid = user->sid;
    } else {
        if (!LookupUserSidString(target.user_name, &sid, error_message)) {
            return false;
        }
    }

    if (IsHiveLoaded(sid)) {
        context->root = HKEY_USERS;
        context->policy_path = sid + L"\\" + kPolicyPath;
        return true;
    }

    std::wstring profile_path;
    if (const LocalUserInfo* user = FindStandardUser(target.user_name); user != nullptr && user->has_profile) {
        profile_path = user->profile_path;
    } else if (!GetProfilePathForSid(sid, &profile_path)) {
        *error_message = L"Profile user target belum ada. Login sekali dulu dengan user tersebut.";
        return false;
    }

    const std::wstring hive_path = profile_path + L"\\NTUSER.DAT";
    if (!FileExists(hive_path)) {
        *error_message = L"File profil user tidak ditemukan: " + hive_path;
        return false;
    }

    if (!g_is_elevated) {
        *error_message = L"Mode standard user butuh Run as administrator.";
        return false;
    }

    if (!EnablePrivilege(SE_BACKUP_NAME, error_message) || !EnablePrivilege(SE_RESTORE_NAME, error_message)) {
        return false;
    }

    const std::wstring mount_name = BuildMountName();
    const LONG load_status = RegLoadKeyW(HKEY_USERS, mount_name.c_str(), hive_path.c_str());
    if (load_status != ERROR_SUCCESS) {
        *error_message = L"Failed to load user profile hive: " + FormatErrorMessage(load_status);
        return false;
    }

    context->root = HKEY_USERS;
    context->policy_path = mount_name + L"\\" + kPolicyPath;
    context->loaded_mount = mount_name;
    context->unload_required = true;
    return true;
}

void CleanupHiveContext(HiveContext* context) {
    if (context->unload_required && !context->loaded_mount.empty()) {
        RegUnLoadKeyW(HKEY_USERS, context->loaded_mount.c_str());
    }
}

bool QueryPolicyEnabledAtPath(HKEY root, const std::wstring& subkey, bool* enabled, std::wstring* error_message) {
    *enabled = false;

    HKEY key = nullptr;
    const LONG status = RegOpenKeyExW(root, subkey.c_str(), 0, KEY_QUERY_VALUE, &key);
    if (status == ERROR_FILE_NOT_FOUND) {
        return true;
    }
    if (status != ERROR_SUCCESS) {
        *error_message = L"Failed to open registry key: " + FormatErrorMessage(status);
        return false;
    }

    DWORD value = 0;
    DWORD type = 0;
    DWORD size = sizeof(value);
    const LONG query_status = RegQueryValueExW(
        key,
        kPolicyValue,
        nullptr,
        &type,
        reinterpret_cast<LPBYTE>(&value),
        &size);
    RegCloseKey(key);

    if (query_status == ERROR_FILE_NOT_FOUND) {
        return true;
    }
    if (query_status != ERROR_SUCCESS) {
        *error_message = L"Failed to read registry value: " + FormatErrorMessage(query_status);
        return false;
    }
    if (type != REG_DWORD) {
        *error_message = L"Registry value exists with an unexpected type.";
        return false;
    }

    *enabled = (value != 0);
    return true;
}

bool SetPolicyEnabledAtPath(HKEY root, const std::wstring& subkey, bool enabled, std::wstring* error_message) {
    HKEY key = nullptr;
    DWORD disposition = 0;
    const LONG open_status = RegCreateKeyExW(
        root,
        subkey.c_str(),
        0,
        nullptr,
        REG_OPTION_NON_VOLATILE,
        KEY_SET_VALUE,
        nullptr,
        &key,
        &disposition);
    if (open_status != ERROR_SUCCESS) {
        *error_message = L"Failed to open registry key for writing: " + FormatErrorMessage(open_status);
        return false;
    }

    LONG status = ERROR_SUCCESS;
    if (enabled) {
        const DWORD value = 1;
        status = RegSetValueExW(
            key,
            kPolicyValue,
            0,
            REG_DWORD,
            reinterpret_cast<const BYTE*>(&value),
            sizeof(value));
    } else {
        status = RegDeleteValueW(key, kPolicyValue);
        if (status == ERROR_FILE_NOT_FOUND) {
            status = ERROR_SUCCESS;
        }
    }

    RegCloseKey(key);

    if (status != ERROR_SUCCESS) {
        *error_message = enabled
            ? L"Failed to enable the policy: " + FormatErrorMessage(status)
            : L"Failed to disable the policy: " + FormatErrorMessage(status);
        return false;
    }

    return true;
}

bool QueryPolicyEnabledForTarget(const TargetSpec& target, bool* enabled, std::wstring* error_message) {
    HiveContext context{};
    if (!PrepareHiveContext(target, &context, error_message)) {
        return false;
    }

    const bool ok = QueryPolicyEnabledAtPath(context.root, context.policy_path, enabled, error_message);
    CleanupHiveContext(&context);
    return ok;
}

bool SetPolicyEnabledForTarget(const TargetSpec& target, bool enabled, std::wstring* error_message) {
    HiveContext context{};
    if (!PrepareHiveContext(target, &context, error_message)) {
        return false;
    }

    const bool ok = SetPolicyEnabledAtPath(context.root, context.policy_path, enabled, error_message);
    CleanupHiveContext(&context);
    return ok;
}

std::wstring BuildRunAsArguments(CommandAction action, const std::wstring& user_name) {
    std::wstring arguments;
    switch (action) {
    case CommandAction::kEnable:
        arguments = L"--enable";
        break;
    case CommandAction::kDisable:
        arguments = L"--disable";
        break;
    case CommandAction::kStatus:
        arguments = L"--status";
        break;
    default:
        return L"";
    }

    if (!user_name.empty()) {
        arguments += L" --user \"";
        arguments += user_name;
        arguments += L"\"";
    }

    return arguments;
}

bool RunElevatedCommand(CommandAction action, const std::wstring& user_name, std::wstring* error_message) {
    const std::wstring exe_path = GetExecutablePath();
    if (exe_path.empty()) {
        *error_message = L"Failed to locate the current executable.";
        return false;
    }

    const std::wstring arguments = BuildRunAsArguments(action, user_name);
    SHELLEXECUTEINFOW execute_info{};
    execute_info.cbSize = sizeof(execute_info);
    execute_info.fMask = SEE_MASK_NOCLOSEPROCESS;
    execute_info.hwnd = nullptr;
    execute_info.lpVerb = L"runas";
    execute_info.lpFile = exe_path.c_str();
    execute_info.lpParameters = arguments.c_str();
    execute_info.nShow = SW_HIDE;

    if (!ShellExecuteExW(&execute_info)) {
        const DWORD error = GetLastError();
        if (error == ERROR_CANCELLED) {
            *error_message = L"UAC elevation dibatalkan.";
        } else {
            *error_message = L"Failed to relaunch elevated: " + FormatErrorMessage(error);
        }
        return false;
    }

    WaitForSingleObject(execute_info.hProcess, INFINITE);
    DWORD exit_code = 0;
    GetExitCodeProcess(execute_info.hProcess, &exit_code);
    CloseHandle(execute_info.hProcess);

    if (exit_code != 0) {
        *error_message = L"Elevated command failed with exit code " + std::to_wstring(exit_code) + L".";
        return false;
    }

    return true;
}

TargetSpec GetSelectedTarget() {
    TargetSpec target{};
    target.mode = g_selected_mode;

    if (g_selected_mode == TargetMode::kStandardUser && g_user_combo != nullptr) {
        const LRESULT index = SendMessageW(g_user_combo, CB_GETCURSEL, 0, 0);
        if (index != CB_ERR && index >= 0 && static_cast<size_t>(index) < g_standard_users.size()) {
            target.user_name = g_standard_users[static_cast<size_t>(index)].name;
        }
    }

    return target;
}

std::wstring DescribeTarget(const TargetSpec& target) {
    if (target.mode == TargetMode::kCurrentUser) {
        return L"Admin / current user: " + g_current_user_name;
    }

    if (target.user_name.empty()) {
        return L"Standard user: belum dipilih";
    }

    return L"Standard user target: " + target.user_name;
}

std::wstring BuildNoteText(const TargetSpec& target) {
    if (target.mode == TargetMode::kCurrentUser) {
        return L"Mode admin hanya mempengaruhi akun Windows yang sedang login sekarang.";
    }

    std::wstring note = L"Mode standard user menulis policy ke profil user target walau tombol ditekan dari akun admin.";
    if (!g_is_elevated) {
        note += L"\r\nUntuk user target yang profilnya tidak sedang aktif, jalankan app ini dengan Run as administrator.";
    } else {
        note += L"\r\nJika user target belum pernah login, login sekali dulu agar profil NTUSER.DAT dibuat.";
    }
    return note;
}

void ApplyControlFont(HWND control) {
    if (control != nullptr && g_ui_font != nullptr) {
        SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(g_ui_font), TRUE);
    }
}

void SyncModeFromUi() {
    if (g_standard_user_radio != nullptr &&
        SendMessageW(g_standard_user_radio, BM_GETCHECK, 0, 0) == BST_CHECKED) {
        g_selected_mode = TargetMode::kStandardUser;
    } else {
        g_selected_mode = TargetMode::kCurrentUser;
    }
}

void PopulateUserCombo() {
    if (g_user_combo == nullptr) {
        return;
    }

    SendMessageW(g_user_combo, CB_RESETCONTENT, 0, 0);

    int selected_index = -1;
    for (size_t index = 0; index < g_standard_users.size(); ++index) {
        const int combo_index = static_cast<int>(SendMessageW(
            g_user_combo,
            CB_ADDSTRING,
            0,
            reinterpret_cast<LPARAM>(g_standard_users[index].name.c_str())));
        if (!g_selected_standard_user_name.empty() &&
            _wcsicmp(g_selected_standard_user_name.c_str(), g_standard_users[index].name.c_str()) == 0) {
            selected_index = combo_index;
        }
    }

    if (selected_index < 0 && !g_standard_users.empty()) {
        selected_index = 0;
    }

    if (selected_index >= 0) {
        SendMessageW(g_user_combo, CB_SETCURSEL, selected_index, 0);
        g_selected_standard_user_name = g_standard_users[static_cast<size_t>(selected_index)].name;
    } else {
        g_selected_standard_user_name.clear();
    }
}

void RefreshUserList() {
    std::wstring warning_message;
    g_standard_users = EnumerateStandardUsers(&warning_message);
    PopulateUserCombo();
}

void UpdateModeControls() {
    const BOOL enable_standard_controls = (g_selected_mode == TargetMode::kStandardUser) ? TRUE : FALSE;
    EnableWindow(g_user_label, enable_standard_controls);
    EnableWindow(g_user_combo, enable_standard_controls);
    const BOOL has_target_user = !g_standard_users.empty();
    EnableWindow(g_enable_button, g_selected_mode == TargetMode::kCurrentUser || has_target_user);
    EnableWindow(g_disable_button, g_selected_mode == TargetMode::kCurrentUser || has_target_user);
}

void RefreshUi() {
    SyncModeFromUi();
    UpdateModeControls();

    TargetSpec target = GetSelectedTarget();
    std::wstring status_text = L"Target: " + DescribeTarget(target) + L"\r\n";

    if (target.mode == TargetMode::kStandardUser && target.user_name.empty()) {
        status_text += L"Status: tidak ada standard user yang bisa dipilih.";
        SetWindowTextW(g_status_label, status_text.c_str());
        SetWindowTextW(g_note_label, BuildNoteText(target).c_str());
        return;
    }

    bool enabled = false;
    std::wstring error_message;
    if (!QueryPolicyEnabledForTarget(target, &enabled, &error_message)) {
        status_text += L"Status: error\r\n" + error_message;
    } else if (enabled) {
        status_text += L"Status: enabled\r\nPerintah Shut Down/Restart/Sleep/Hibernate disembunyikan di UI Windows.";
    } else {
        status_text += L"Status: disabled\r\nUI Windows menampilkan perintah daya seperti biasa.";
    }

    SetWindowTextW(g_status_label, status_text.c_str());
    SetWindowTextW(g_note_label, BuildNoteText(target).c_str());
}

void LayoutControls(HWND window) {
    RECT client{};
    GetClientRect(window, &client);

    const int margin = 16;
    const int content_width = client.right - (margin * 2);
    const int button_width = 110;
    const int button_height = 34;
    const int gap = 10;

    MoveWindow(g_status_label, margin, margin, content_width, 72, TRUE);
    MoveWindow(g_current_user_radio, margin, margin + 82, 220, 24, TRUE);
    MoveWindow(g_standard_user_radio, margin + 230, margin + 82, 160, 24, TRUE);
    MoveWindow(g_user_label, margin, margin + 114, 92, 22, TRUE);
    MoveWindow(g_user_combo, margin + 96, margin + 110, content_width - 96, 320, TRUE);
    MoveWindow(g_note_label, margin, margin + 148, content_width, 70, TRUE);

    const int row_y = client.bottom - margin - button_height;
    MoveWindow(g_enable_button, margin, row_y, button_width, button_height, TRUE);
    MoveWindow(g_disable_button, margin + button_width + gap, row_y, button_width, button_height, TRUE);
    MoveWindow(g_refresh_button, margin + ((button_width + gap) * 2), row_y, button_width, button_height, TRUE);
    MoveWindow(g_close_button, client.right - margin - button_width, row_y, button_width, button_height, TRUE);
}

ParsedCommand ParseArguments(const std::vector<std::wstring>& arguments, std::wstring* error_message) {
    ParsedCommand command{};
    command.action = arguments.empty() ? CommandAction::kGui : CommandAction::kGui;

    for (size_t index = 0; index < arguments.size(); ++index) {
        const std::wstring& argument = arguments[index];
        if (argument == L"--help") {
            command.action = CommandAction::kHelp;
        } else if (argument == L"--status") {
            command.action = CommandAction::kStatus;
        } else if (argument == L"--enable") {
            command.action = CommandAction::kEnable;
        } else if (argument == L"--disable") {
            command.action = CommandAction::kDisable;
        } else if (argument == L"--user") {
            if (index + 1 >= arguments.size()) {
                *error_message = L"Missing username after --user.";
                command.action = CommandAction::kHelp;
                return command;
            }
            command.user_name = arguments[++index];
        } else {
            *error_message = L"Unknown argument: " + argument;
            command.action = CommandAction::kHelp;
            return command;
        }
    }

    if (arguments.empty()) {
        command.action = CommandAction::kGui;
    }

    return command;
}

int RunCommand(const ParsedCommand& command) {
    if (command.action == CommandAction::kHelp) {
        WriteLine(L"PowerMenuGuard commands:");
        WriteLine(L"  --status                  Show status for current user");
        WriteLine(L"  --enable                  Enable policy for current user");
        WriteLine(L"  --disable                 Disable policy for current user");
        WriteLine(L"  --status --user NAME      Show status for another local user");
        WriteLine(L"  --enable --user NAME      Enable policy for another local user");
        WriteLine(L"  --disable --user NAME     Disable policy for another local user");
        return 0;
    }

    TargetSpec target{};
    target.mode = command.user_name.empty() ? TargetMode::kCurrentUser : TargetMode::kStandardUser;
    target.user_name = command.user_name;

    if (command.action == CommandAction::kStatus) {
        bool enabled = false;
        std::wstring error_message;
        if (!QueryPolicyEnabledForTarget(target, &enabled, &error_message)) {
            WriteLine(error_message);
            return 1;
        }

        WriteLine(enabled ? L"enabled" : L"disabled");
        return 0;
    }

    if (command.action == CommandAction::kEnable || command.action == CommandAction::kDisable) {
        const bool enable = command.action == CommandAction::kEnable;
        std::wstring error_message;
        if (!SetPolicyEnabledForTarget(target, enable, &error_message)) {
            WriteLine(error_message);
            return 1;
        }

        WriteLine(enable ? L"enabled" : L"disabled");
        return 0;
    }

    return 0;
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM w_param, LPARAM l_param) {
    switch (message) {
    case WM_CREATE: {
        g_ui_font = static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        g_current_user_name = GetCurrentUserNameSimple();
        g_is_elevated = IsProcessElevated();

        g_status_label = CreateWindowExW(
            0,
            L"STATIC",
            L"",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kStatusId),
            nullptr,
            nullptr);

        g_current_user_radio = CreateWindowExW(
            0,
            L"BUTTON",
            L"Admin / current user",
            WS_CHILD | WS_VISIBLE | BS_AUTORADIOBUTTON | WS_GROUP,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kCurrentUserRadioId),
            nullptr,
            nullptr);

        g_standard_user_radio = CreateWindowExW(
            0,
            L"BUTTON",
            L"Standard user",
            WS_CHILD | WS_VISIBLE | BS_AUTORADIOBUTTON,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kStandardUserRadioId),
            nullptr,
            nullptr);

        g_user_label = CreateWindowExW(
            0,
            L"STATIC",
            L"Target user:",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kUserLabelId),
            nullptr,
            nullptr);

        g_user_combo = CreateWindowExW(
            0,
            L"COMBOBOX",
            L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kUserComboId),
            nullptr,
            nullptr);

        g_note_label = CreateWindowExW(
            0,
            L"STATIC",
            L"",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kNoteId),
            nullptr,
            nullptr);

        g_enable_button = CreateWindowExW(
            0,
            L"BUTTON",
            L"Enable",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kEnableId),
            nullptr,
            nullptr);

        g_disable_button = CreateWindowExW(
            0,
            L"BUTTON",
            L"Disable",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kDisableId),
            nullptr,
            nullptr);

        g_refresh_button = CreateWindowExW(
            0,
            L"BUTTON",
            L"Refresh",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kRefreshId),
            nullptr,
            nullptr);

        g_close_button = CreateWindowExW(
            0,
            L"BUTTON",
            L"Close",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0,
            0,
            0,
            0,
            window,
            reinterpret_cast<HMENU>(kCloseId),
            nullptr,
            nullptr);

        ApplyControlFont(g_status_label);
        ApplyControlFont(g_current_user_radio);
        ApplyControlFont(g_standard_user_radio);
        ApplyControlFont(g_user_label);
        ApplyControlFont(g_user_combo);
        ApplyControlFont(g_note_label);
        ApplyControlFont(g_enable_button);
        ApplyControlFont(g_disable_button);
        ApplyControlFont(g_refresh_button);
        ApplyControlFont(g_close_button);

        SendMessageW(g_current_user_radio, BM_SETCHECK, BST_CHECKED, 0);
        RefreshUserList();
        RefreshUi();
        return 0;
    }
    case WM_SIZE:
        LayoutControls(window);
        return 0;
    case WM_COMMAND: {
        const int control_id = LOWORD(w_param);

        if (control_id == kCurrentUserRadioId || control_id == kStandardUserRadioId) {
            RefreshUi();
            return 0;
        }

        if (control_id == kUserComboId && HIWORD(w_param) == CBN_SELCHANGE) {
            const LRESULT index = SendMessageW(g_user_combo, CB_GETCURSEL, 0, 0);
            if (index != CB_ERR && index >= 0 && static_cast<size_t>(index) < g_standard_users.size()) {
                g_selected_standard_user_name = g_standard_users[static_cast<size_t>(index)].name;
            }
            RefreshUi();
            return 0;
        }

        if (control_id == kEnableId || control_id == kDisableId) {
            const bool enable = (control_id == kEnableId);
            TargetSpec target = GetSelectedTarget();

            if (target.mode == TargetMode::kStandardUser && target.user_name.empty()) {
                MessageBoxW(window, L"Tidak ada standard user yang dipilih.", kWindowTitle, MB_OK | MB_ICONWARNING);
                return 0;
            }

            std::wstring error_message;
            if (target.mode == TargetMode::kStandardUser && !g_is_elevated) {
                if (!RunElevatedCommand(enable ? CommandAction::kEnable : CommandAction::kDisable, target.user_name, &error_message)) {
                    MessageBoxW(window, error_message.c_str(), kWindowTitle, MB_OK | MB_ICONERROR);
                    return 0;
                }
            } else if (!SetPolicyEnabledForTarget(target, enable, &error_message)) {
                MessageBoxW(window, error_message.c_str(), kWindowTitle, MB_OK | MB_ICONERROR);
                return 0;
            }

            RefreshUserList();
            RefreshUi();
            MessageBoxW(
                window,
                enable
                    ? L"Policy berhasil diaktifkan untuk target yang dipilih."
                    : L"Policy berhasil dimatikan untuk target yang dipilih.",
                kWindowTitle,
                MB_OK | MB_ICONINFORMATION);
            return 0;
        }

        if (control_id == kRefreshId) {
            RefreshUserList();
            RefreshUi();
            return 0;
        }

        if (control_id == kCloseId) {
            DestroyWindow(window);
            return 0;
        }

        return 0;
    }
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(window, message, w_param, l_param);
    }
}

int RunGui(HINSTANCE instance) {
    WNDCLASSEXW window_class{};
    window_class.cbSize = sizeof(window_class);
    window_class.lpfnWndProc = WindowProc;
    window_class.hInstance = instance;
    window_class.lpszClassName = kWindowClassName;
    window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    window_class.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    window_class.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    window_class.hIconSm = LoadIconW(nullptr, IDI_APPLICATION);

    if (RegisterClassExW(&window_class) == 0) {
        MessageBoxW(nullptr, L"Failed to register window class.", kWindowTitle, MB_OK | MB_ICONERROR);
        return 1;
    }

    HWND window = CreateWindowExW(
        0,
        kWindowClassName,
        kWindowTitle,
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        680,
        340,
        nullptr,
        nullptr,
        instance,
        nullptr);

    if (window == nullptr) {
        MessageBoxW(nullptr, L"Failed to create the main window.", kWindowTitle, MB_OK | MB_ICONERROR);
        return 1;
    }

    ShowWindow(window, SW_SHOWDEFAULT);
    UpdateWindow(window);

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    return static_cast<int>(message.wParam);
}

std::vector<std::wstring> GetArguments() {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::vector<std::wstring> arguments;
    if (argv == nullptr) {
        return arguments;
    }

    for (int i = 1; i < argc; ++i) {
        arguments.emplace_back(argv[i]);
    }

    LocalFree(argv);
    return arguments;
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    g_current_user_name = GetCurrentUserNameSimple();
    g_is_elevated = IsProcessElevated();

    const std::vector<std::wstring> arguments = GetArguments();
    std::wstring parse_error;
    const ParsedCommand command = ParseArguments(arguments, &parse_error);

    if (!parse_error.empty()) {
        WriteLine(parse_error);
        return 1;
    }

    if (command.action != CommandAction::kGui) {
        return RunCommand(command);
    }

    return RunGui(instance);
}
