// SPDX-License-Identifier: GPL-3.0-or-later
#include <windows.h>
#include <dinput.h>
#include <dinputd.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <vector>
#include <TraceLoggingProvider.h>

#include "EffectMarshaller.h"
#include "../../LogiControl.SemanticIpc/MonotonicClock.h"
#include "../../LogiControl.SemanticIpc/SemanticClient.h"

TRACELOGGING_DEFINE_PROVIDER(
    g_providerTelemetry,
    "LogiControl-DirectInputProvider",
    (0x4531ad03, 0x0288, 0x4674, 0xa8, 0xef, 0x76, 0xf4, 0x6c, 0xaf, 0x88, 0xdf));

// {32FC17A4-0050-419A-BB41-59B228B5CFF4}
static constexpr GUID CLSID_LogiControlFfb{
    0x32fc17a4, 0x0050, 0x419a, {0xbb, 0x41, 0x59, 0xb2, 0x28, 0xb5, 0xcf, 0xf4}};

namespace {

using logicontrol::ipc::EffectUpdateMask;
using logicontrol::ipc::MessageType;
using logicontrol::ipc::Result;

std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};
std::atomic<long> g_profileDrivers{0};

constexpr DWORD kInvalidEffectHandle = 0;
constexpr DWORD kFirmwareRevision = 0x1322;
constexpr DWORD kDriverVersion = 0x00020000;

bool ProfileRequested() noexcept {
    wchar_t value[2]{};
    return GetEnvironmentVariableW(L"LOGICONTROL_FFB_PROFILE", value, 2) == 1 && value[0] == L'1';
}

class CallbackTrace final {
public:
    explicit CallbackTrace(const char* callback) noexcept
        : callback_(callback), enabled_(g_profileDrivers.load(std::memory_order_relaxed) > 0),
          started_(enabled_ ? logicontrol::ipc::detail::QpcMicroseconds() : 0) {}

    ~CallbackTrace() {
        if (!enabled_) return;
        TraceLoggingWrite(
            g_providerTelemetry,
            "ProviderCallback",
            TraceLoggingString(callback_, "Callback"),
            TraceLoggingUInt64(
                logicontrol::ipc::detail::QpcMicroseconds() - started_, "DurationMicroseconds"));
    }

private:
    const char* callback_;
    bool enabled_;
    std::uint64_t started_;
};

void LogDiagnostic(const char* format, ...) noexcept {
    wchar_t enabled[2]{};
    if (GetEnvironmentVariableW(
            L"LOGICONTROL_FFB_DIAGNOSTICS",
            enabled,
            static_cast<DWORD>(std::size(enabled))) == 0 ||
        enabled[0] != L'1') return;

    char line[320]{};
    va_list arguments;
    va_start(arguments, format);
    const auto written = vsprintf_s(line, format, arguments);
    va_end(arguments);
    if (written <= 0) return;
    strcat_s(line, "\r\n");

    wchar_t path[MAX_PATH]{};
    const auto pathLength = GetTempPathW(static_cast<DWORD>(std::size(path)), path);
    if (pathLength == 0 || pathLength >= std::size(path) ||
        wcscat_s(path, L"logicontrol-provider-diagnostic.log") != 0) return;
    const auto file = CreateFileW(
        path,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD bytesWritten{};
    WriteFile(file, line, static_cast<DWORD>(strlen(line)), &bytesWritten, nullptr);
    CloseHandle(file);
}

HRESULT MapResult(Result result) noexcept {
    switch (result) {
    case Result::Ok: return S_OK;
    case Result::InvalidArgument: return DIERR_INVALIDPARAM;
    case Result::Unsupported: return DIERR_UNSUPPORTED;
    case Result::DeviceFull: return DIERR_DEVICEFULL;
    case Result::OtherApplicationHasPriority: return DIERR_OTHERAPPHASPRIO;
    case Result::InputLost: return DIERR_INPUTLOST;
    case Result::NotFound: return DIERR_NOTDOWNLOADED;
    case Result::DeviceNotReady: return DIERR_INPUTLOST;
    case Result::ProtocolError:
    case Result::InternalError: return E_FAIL;
    }
    return E_FAIL;
}

void Write32(std::span<std::byte> destination, std::size_t offset, std::uint32_t value) noexcept {
    logicontrol::ipc::detail::Write32(destination, offset, value);
}

std::uint32_t Read32(std::span<const std::byte> source, std::size_t offset = 0) noexcept {
    return logicontrol::ipc::detail::Read32(source, offset);
}

class EffectDriver final : public IDirectInputEffectDriver {
public:
    EffectDriver() {
        profileEnabled_ = ProfileRequested();
        if (profileEnabled_ && g_profileDrivers.fetch_add(1) == 0) {
            TraceLoggingRegister(g_providerTelemetry);
        }
        ++g_objects;
    }

    ~EffectDriver() {
        client_.Close();
        --g_objects;
        if (profileEnabled_ && g_profileDrivers.fetch_sub(1) == 1) {
            TraceLoggingUnregister(g_providerTelemetry);
        }
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IDirectInputEffectDriver) {
            *object = static_cast<IDirectInputEffectDriver*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(++references_); }

    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --references_;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    HRESULT STDMETHODCALLTYPE DeviceID(
        DWORD,
        DWORD externalId,
        DWORD begin,
        DWORD,
        void* initInfo) override {
        CallbackTrace trace("DeviceID");
        LogDiagnostic("DeviceID begin=%lu externalId=%lu", begin, externalId);
        if (begin == 0) {
            client_.Close();
            std::scoped_lock lock(identityMutex_);
            devicePath_.clear();
            externalId_ = 0;
            return S_OK;
        }
        if (initInfo == nullptr) return E_INVALIDARG;
        const auto* info = static_cast<const DIHIDFFINITINFO*>(initInfo);
        if (info->dwSize < sizeof(DIHIDFFINITINFO) || info->pwszDeviceInterface == nullptr ||
            info->pwszDeviceInterface[0] == L'\0') return E_INVALIDARG;
        std::scoped_lock lock(identityMutex_);
        devicePath_ = info->pwszDeviceInterface;
        externalId_ = externalId;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetVersions(DIDRIVERVERSIONS* versions) override {
        CallbackTrace trace("GetVersions");
        if (versions == nullptr || versions->dwSize < sizeof(DIDRIVERVERSIONS)) return E_INVALIDARG;
        versions->dwFirmwareRevision = kFirmwareRevision;
        versions->dwHardwareRevision = kFirmwareRevision;
        versions->dwFFDriverVersion = kDriverVersion;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Escape(DWORD, DWORD, DIEFFESCAPE*) override {
        CallbackTrace trace("Escape");
        return DIERR_UNSUPPORTED;
    }

    HRESULT STDMETHODCALLTYPE SetGain(DWORD externalId, DWORD gain) override {
        CallbackTrace trace("SetGain");
        if (!Matches(externalId) || gain > 10000) return E_INVALIDARG;
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;
        std::array<std::byte, 4> payload{};
        Write32(payload, 0, gain);
        return MapResult(client_.Send(MessageType::SetGain, payload).result);
    }

    HRESULT STDMETHODCALLTYPE SendForceFeedbackCommand(DWORD externalId, DWORD command) override {
        CallbackTrace trace("SendForceFeedbackCommand");
        if (!Matches(externalId)) return E_INVALIDARG;
        std::uint8_t semantic{};
        switch (command) {
        case DISFFC_PAUSE: semantic = 0; break;
        case DISFFC_CONTINUE: semantic = 1; break;
        case DISFFC_SETACTUATORSON: semantic = 2; break;
        case DISFFC_SETACTUATORSOFF: semantic = 3; break;
        case DISFFC_STOPALL: semantic = 4; break;
        case DISFFC_RESET: semantic = 5; break;
        default: return E_INVALIDARG;
        }
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;
        std::array<std::byte, 4> payload{};
        payload[0] = static_cast<std::byte>(semantic);
        return MapResult(client_.Send(MessageType::DeviceCommand, payload).result);
    }

    HRESULT STDMETHODCALLTYPE GetForceFeedbackState(
        DWORD externalId,
        DIDEVICESTATE* state) override {
        CallbackTrace trace("GetForceFeedbackState");
        if (!Matches(externalId) || state == nullptr || state->dwSize < sizeof(DIDEVICESTATE)) {
            return E_INVALIDARG;
        }
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;
        const auto response = client_.Send(MessageType::QueryDeviceState);
        const auto mapped = MapResult(response.result);
        if (FAILED(mapped)) return mapped;
        if (response.payload.size() != 24) return E_FAIL;
        const auto flags = Read32(response.payload, 16);
        const auto downloads = Read32(response.payload, 20);
        state->dwState = DIGFFS_POWERON | DIGFFS_SAFETYSWITCHOFF | DIGFFS_USERFFSWITCHON;
        state->dwState |= (flags & 1U) != 0 ? DIGFFS_PAUSED : 0;
        state->dwState |= (flags & 2U) != 0 ? DIGFFS_ACTUATORSON : DIGFFS_ACTUATORSOFF;
        state->dwLoad = std::min<DWORD>(100, downloads * 100 / 16);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DownloadEffect(
        DWORD externalId,
        DWORD effectId,
        DWORD* handle,
        const DIEFFECT* effect,
        DWORD flags) override {
        CallbackTrace trace("DownloadEffect");
        if (!Matches(externalId) || handle == nullptr || effect == nullptr) return E_INVALIDARG;
        constexpr DWORD controlFlags = DIEP_START | DIEP_NORESTART | DIEP_NODOWNLOAD;
        if ((flags & ~(DIEP_ALLPARAMS | controlFlags)) != 0) return DIERR_INVALIDPARAM;

        logicontrol::ipc::EffectDefinition definition{};
        EffectUpdateMask updateMask{};
        const auto marshal = logicontrol::provider::MarshalEffect(effectId, *effect, flags, definition, updateMask);
        if (FAILED(marshal)) return marshal;
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;

        const auto effectLength = logicontrol::ipc::EncodedEffectLength(definition);
        std::vector<std::byte> payload(8U + effectLength);
        Write32(payload, 0, *handle);
        logicontrol::ipc::detail::Write16(payload, 4, static_cast<std::uint16_t>(updateMask));
        if (!logicontrol::ipc::EncodeEffect(std::span<std::byte>(payload).subspan(8), definition)) {
            return DIERR_INVALIDPARAM;
        }
        const auto message = (flags & DIEP_NODOWNLOAD) != 0
            ? MessageType::ValidateEffect
            : MessageType::UpsertEffect;
        const auto response = client_.Send(message, payload);
        const auto mapped = MapResult(response.result);
        if (FAILED(mapped)) return mapped;
        if ((flags & DIEP_NODOWNLOAD) != 0) return S_OK;
        if (response.payload.size() != 4) return E_FAIL;
        *handle = Read32(response.payload);
        if (*handle == kInvalidEffectHandle) return E_FAIL;

        if ((flags & DIEP_START) != 0) {
            return StartSemantic(
                *handle,
                1,
                false,
                (flags & DIEP_NORESTART) == 0);
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DestroyEffect(DWORD externalId, DWORD handle) override {
        CallbackTrace trace("DestroyEffect");
        if (!Matches(externalId) || handle == kInvalidEffectHandle) return E_INVALIDARG;
        return SendHandle(MessageType::DestroyEffect, handle);
    }

    HRESULT STDMETHODCALLTYPE StartEffect(
        DWORD externalId,
        DWORD handle,
        DWORD mode,
        DWORD iterations) override {
        CallbackTrace trace("StartEffect");
        if (!Matches(externalId) || handle == kInvalidEffectHandle || iterations == 0 ||
            (mode & ~(DIES_SOLO | DIES_NODOWNLOAD)) != 0) return E_INVALIDARG;
        return StartSemantic(handle, iterations, (mode & DIES_SOLO) != 0, true);
    }

    HRESULT STDMETHODCALLTYPE StopEffect(DWORD externalId, DWORD handle) override {
        CallbackTrace trace("StopEffect");
        if (!Matches(externalId) || handle == kInvalidEffectHandle) return E_INVALIDARG;
        return SendHandle(MessageType::StopEffect, handle);
    }

    HRESULT STDMETHODCALLTYPE GetEffectStatus(
        DWORD externalId,
        DWORD handle,
        DWORD* status) override {
        CallbackTrace trace("GetEffectStatus");
        if (!Matches(externalId) || handle == kInvalidEffectHandle || status == nullptr) return E_INVALIDARG;
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;
        std::array<std::byte, 4> payload{};
        Write32(payload, 0, handle);
        const auto response = client_.Send(MessageType::QueryEffect, payload);
        const auto mapped = MapResult(response.result);
        if (FAILED(mapped)) return mapped;
        if (response.payload.size() < 4) return E_FAIL;
        const auto playback = std::to_integer<std::uint8_t>(response.payload[0]);
        *status = playback == 1 || playback == 2 || playback == 3 ? DIEGES_PLAYING : 0;
        return S_OK;
    }

private:
    bool Matches(DWORD externalId) const noexcept { return externalId_.load() == externalId && externalId != 0; }

    HRESULT EnsureConnected() noexcept {
        if (client_.IsConnected()) return S_OK;
        std::wstring path;
        {
            std::scoped_lock lock(identityMutex_);
            path = devicePath_;
        }
        if (path.empty()) return DIERR_NOTINITIALIZED;
        const auto result = client_.ConnectAndBind(path.c_str());
        LogDiagnostic("Semantic connect result=%ld", static_cast<long>(result));
        return MapResult(result);
    }

    HRESULT SendHandle(MessageType message, DWORD handle) noexcept {
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;
        std::array<std::byte, 4> payload{};
        Write32(payload, 0, handle);
        return MapResult(client_.Send(message, payload).result);
    }

    HRESULT StartSemantic(DWORD handle, DWORD iterations, bool solo, bool restart) noexcept {
        const auto connected = EnsureConnected();
        if (FAILED(connected)) return connected;
        std::array<std::byte, 12> payload{};
        Write32(payload, 0, handle);
        Write32(payload, 4, iterations);
        Write32(payload, 8, (solo ? 1U : 0U) | (restart ? 2U : 0U));
        return MapResult(client_.Send(MessageType::StartEffect, payload).result);
    }

    std::atomic<long> references_{1};
    logicontrol::ipc::SemanticClient client_;
    mutable std::mutex identityMutex_;
    std::wstring devicePath_;
    std::atomic<DWORD> externalId_{};
    bool profileEnabled_{};
};

class ClassFactory final : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (object == nullptr) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory) {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(++references_); }

    ULONG STDMETHODCALLTYPE Release() override {
        const auto remaining = --references_;
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) override {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        auto* driver = new (std::nothrow) EffectDriver();
        if (driver == nullptr) return E_OUTOFMEMORY;
        const auto result = driver->QueryInterface(iid, object);
        driver->Release();
        return result;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) ++g_locks;
        else --g_locks;
        return S_OK;
    }

private:
    std::atomic<long> references_{1};
};

} // namespace

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object) {
    if (!IsEqualCLSID(clsid, CLSID_LogiControlFfb)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;
    const auto result = factory->QueryInterface(iid, object);
    factory->Release();
    return result;
}

STDAPI DllCanUnloadNow() {
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}
