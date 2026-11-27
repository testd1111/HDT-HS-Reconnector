#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <metahost.h>
#include <stdio.h>
#include <wchar.h>
#include <string>
#include <vector>

namespace {

HMODULE g_module = nullptr;
volatile LONG g_started = 0;

typedef HRESULT (WINAPI *PFN_CLRCreateInstance)(REFCLSID clsid, REFIID riid, LPVOID *ppInterface);

std::wstring GetErrorLogPath() {
    DWORD needed = GetTempPathW(0, nullptr);
    if (needed == 0 || needed > 32768) {
        return std::wstring();
    }

    std::vector<wchar_t> tempDir(needed + 2, L'\0');
    DWORD len = GetTempPathW(static_cast<DWORD>(tempDir.size()), tempDir.data());
    if (len == 0 || len >= tempDir.size()) {
        return std::wstring();
    }

    wchar_t suffix[64] = {};
    swprintf_s(suffix, _countof(suffix), L"hdt-hs-reconnector-bootstrap-%lu.err", GetCurrentProcessId());
    return std::wstring(tempDir.data(), len) + suffix;
}

void ClearErrorLog() {
    std::wstring path = GetErrorLogPath();
    if (!path.empty()) {
        DeleteFileW(path.c_str());
    }
}

void WriteErrorLog(const char *stage, HRESULT hr, DWORD gle) {
    std::wstring path = GetErrorLogPath();
    if (path.empty()) {
        return;
    }

    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    char line[256] = {};
    int written = sprintf_s(line, sizeof(line), "stage=%s hr=0x%08lX gle=%lu\r\n", stage, static_cast<unsigned long>(hr), static_cast<unsigned long>(gle));
    if (written > 0) {
        DWORD bytesWritten = 0;
        WriteFile(file, line, static_cast<DWORD>(written), &bytesWritten, nullptr);
    }
    CloseHandle(file);
}

bool BuildSiblingPath(const wchar_t *fileName, std::wstring &dirOut, std::wstring &fileOut) {
    std::vector<wchar_t> buffer(512, L'\0');
    DWORD len = 0;
    while (buffer.size() <= 32768) {
        len = GetModuleFileNameW(g_module, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (len == 0) {
            return false;
        }
        if (len < buffer.size() - 1) {
            break;
        }
        buffer.resize(buffer.size() * 2, L'\0');
    }

    if (len == 0 || len >= buffer.size()) {
        return false;
    }

    std::wstring modulePath(buffer.data(), len);
    size_t lastSlash = modulePath.find_last_of(L"\\/");
    if (lastSlash == std::wstring::npos) {
        return false;
    }

    dirOut = modulePath.substr(0, lastSlash);
    fileOut = dirOut + L"\\" + fileName;
    return true;
}

ICLRRuntimeInfo *AcquireLoadedV4Runtime(ICLRMetaHost *metaHost) {
    IEnumUnknown *enumRuntimes = nullptr;
    HRESULT hr = metaHost->EnumerateLoadedRuntimes(GetCurrentProcess(), &enumRuntimes);
    if (SUCCEEDED(hr) && enumRuntimes) {
        IUnknown *unknown = nullptr;
        ULONG fetched = 0;
        ICLRRuntimeInfo *fallback = nullptr;
        while (enumRuntimes->Next(1, &unknown, &fetched) == S_OK && fetched == 1) {
            ICLRRuntimeInfo *info = nullptr;
            if (SUCCEEDED(unknown->QueryInterface(IID_ICLRRuntimeInfo, reinterpret_cast<void **>(&info))) && info) {
                wchar_t version[64] = {};
                DWORD versionLen = static_cast<DWORD>(_countof(version));
                if (SUCCEEDED(info->GetVersionString(version, &versionLen)) && wcsncmp(version, L"v4.", 3) == 0) {
                    unknown->Release();
                    if (fallback) {
                        fallback->Release();
                    }
                    enumRuntimes->Release();
                    return info;
                }
                if (!fallback) {
                    fallback = info;
                } else {
                    info->Release();
                }
            }
            unknown->Release();
        }
        enumRuntimes->Release();
        if (fallback) {
            return fallback;
        }
    }

    ICLRRuntimeInfo *v4Info = nullptr;
    if (SUCCEEDED(metaHost->GetRuntime(L"v4.0.30319", IID_ICLRRuntimeInfo, reinterpret_cast<void **>(&v4Info)))) {
        return v4Info;
    }
    return nullptr;
}

DWORD RunBootstrap() {
    ClearErrorLog();

    std::wstring baseDir;
    std::wstring bridgePath;
    if (!BuildSiblingPath(L"HdtBridge.dll", baseDir, bridgePath)) {
        WriteErrorLog("ResolveBridgePath", E_FAIL, GetLastError());
        return 1;
    }

    if (GetFileAttributesW(bridgePath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        WriteErrorLog("BridgeFileMissing", HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND), GetLastError());
        return 2;
    }

    HMODULE mscoree = GetModuleHandleW(L"mscoree.dll");
    if (!mscoree) {
        mscoree = LoadLibraryExW(L"mscoree.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    }
    if (!mscoree) {
        WriteErrorLog("LoadMscoree", HRESULT_FROM_WIN32(GetLastError()), GetLastError());
        return 3;
    }

    auto clrCreateInstance = reinterpret_cast<PFN_CLRCreateInstance>(GetProcAddress(mscoree, "CLRCreateInstance"));
    if (!clrCreateInstance) {
        WriteErrorLog("GetCLRCreateInstance", HRESULT_FROM_WIN32(GetLastError()), GetLastError());
        return 4;
    }

    ICLRMetaHost *metaHost = nullptr;
    HRESULT hr = clrCreateInstance(CLSID_CLRMetaHost, IID_ICLRMetaHost, reinterpret_cast<void **>(&metaHost));
    if (FAILED(hr) || !metaHost) {
        WriteErrorLog("CLRCreateInstance", hr, GetLastError());
        return 5;
    }

    ICLRRuntimeInfo *runtimeInfo = AcquireLoadedV4Runtime(metaHost);
    if (!runtimeInfo) {
        metaHost->Release();
        WriteErrorLog("AcquireLoadedV4Runtime", E_FAIL, GetLastError());
        return 6;
    }

    ICLRRuntimeHost *runtimeHost = nullptr;
    hr = runtimeInfo->GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost, reinterpret_cast<void **>(&runtimeHost));
    if (FAILED(hr) || !runtimeHost) {
        runtimeInfo->Release();
        metaHost->Release();
        WriteErrorLog("GetCLRRuntimeHost", hr, GetLastError());
        return 7;
    }

    DWORD retVal = 0;
    hr = runtimeHost->ExecuteInDefaultAppDomain(
        bridgePath.c_str(),
        L"HdtBridge.BridgeEntry",
        L"Initialize",
        baseDir.c_str(),
        &retVal);

    runtimeHost->Release();
    runtimeInfo->Release();
    metaHost->Release();

    if (FAILED(hr) || retVal != 0) {
        WriteErrorLog("ExecuteInDefaultAppDomain", FAILED(hr) ? hr : static_cast<HRESULT>(retVal), GetLastError());
        return 8;
    }

    return 0;
}

DWORD WINAPI BootstrapWorkerThread(LPVOID) {
    DWORD code = RunBootstrap();
    if (code != 0) {
        // Unload cleanly so a future LoadLibraryW loads a fresh image with g_started == 0.
        FreeLibraryAndExitThread(g_module, code);
    }
    return 0;
}

} // namespace

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = hModule;
        DisableThreadLibraryCalls(hModule);
        if (InterlockedCompareExchange(&g_started, 1, 0) == 0) {
            HANDLE thread = CreateThread(nullptr, 0, BootstrapWorkerThread, nullptr, 0, nullptr);
            if (thread) {
                CloseHandle(thread);
            } else {
                InterlockedExchange(&g_started, 0);
            }
        }
    }
    return TRUE;
}
