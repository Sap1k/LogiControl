// SPDX-License-Identifier: GPL-3.0-or-later
#include "SemanticClient.h"
#include "MonotonicClock.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cwchar>
#include <limits>
#include <TraceLoggingProvider.h>

TRACELOGGING_DEFINE_PROVIDER(
    g_semanticIpcProvider,
    "LogiControl-NativeIpc",
    (0x2fcd9c92, 0xa978, 0x4b86, 0x99, 0x42, 0xe8, 0x30, 0xd5, 0x87, 0x94, 0xba));

namespace logicontrol::ipc {
namespace {

std::atomic<long> g_profileClients{};

bool ProfileRequested() noexcept {
    wchar_t value[2]{};
    return GetEnvironmentVariableW(L"LOGICONTROL_FFB_PROFILE", value, 2) == 1 && value[0] == L'1';
}

class IpcTrace final {
public:
    explicit IpcTrace(MessageType type) noexcept
        : type_(type), enabled_(g_profileClients.load(std::memory_order_relaxed) > 0),
          started_(enabled_ ? detail::QpcMicroseconds() : 0) {}

    ~IpcTrace() {
        if (!enabled_) return;
        TraceLoggingWrite(
            g_semanticIpcProvider,
            "IpcRoundTrip",
            TraceLoggingUInt16(static_cast<std::uint16_t>(type_), "MessageType"),
            TraceLoggingUInt64(detail::QpcMicroseconds() - started_, "DurationMicroseconds"));
    }

private:
    MessageType type_;
    bool enabled_;
    std::uint64_t started_;
};

} // namespace

SemanticClient::SemanticClient()
    : heartbeat_([this](std::stop_token stop) { HeartbeatLoop(stop); }) {
    profileEnabled_ = ProfileRequested();
    if (profileEnabled_ && g_profileClients.fetch_add(1) == 0) {
        TraceLoggingRegister(g_semanticIpcProvider);
    }
}

SemanticClient::~SemanticClient() {
    heartbeat_.request_stop();
    heartbeatWake_.notify_all();
    if (heartbeat_.joinable()) heartbeat_.join();
    Close();
    if (profileEnabled_ && g_profileClients.fetch_sub(1) == 1) {
        TraceLoggingUnregister(g_semanticIpcProvider);
    }
}

Result SemanticClient::ConnectAndBind(const wchar_t* devicePath) noexcept {
    if (devicePath == nullptr || *devicePath == L'\0') return Result::InvalidArgument;
    std::scoped_lock lock(mutex_);
    if (pipe_ != INVALID_HANDLE_VALUE) return Result::Ok;
    if (!WaitNamedPipeW(PipeName, ConnectTimeoutMilliseconds)) return Result::InputLost;
    pipe_ = CreateFileW(
        PipeName,
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED,
        nullptr);
    if (pipe_ == INVALID_HANDLE_VALUE) return Result::InputLost;

    const auto hello = SendLocked(MessageType::Hello, {});
    if (hello.result != Result::Ok || sessionId_ == 0) {
        DisconnectLocked();
        return hello.result;
    }

    const auto required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, devicePath, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1 || required - 1 > 512 || required - 1 > std::numeric_limits<std::uint16_t>::max()) {
        DisconnectLocked();
        return Result::InvalidArgument;
    }
    std::vector<std::byte> binding(static_cast<std::size_t>(required - 1) + 2U);
    detail::Write16(binding, 0, static_cast<std::uint16_t>(required - 1));
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            devicePath,
            static_cast<int>(std::wcslen(devicePath)),
            reinterpret_cast<char*>(binding.data() + 2),
            required - 1,
            nullptr,
            nullptr) == 0) {
        DisconnectLocked();
        return Result::InvalidArgument;
    }

    const auto bound = SendLocked(MessageType::BindDevice, binding);
    if (bound.result != Result::Ok) DisconnectLocked();
    heartbeatWake_.notify_all();
    return bound.result;
}

Response SemanticClient::Send(MessageType type, std::span<const std::byte> payload) noexcept {
    std::scoped_lock lock(mutex_);
    return SendLocked(type, payload);
}

void SemanticClient::Close() noexcept {
    std::scoped_lock lock(mutex_);
    if (pipe_ != INVALID_HANDLE_VALUE && sessionId_ != 0) {
        static_cast<void>(SendLocked(MessageType::CloseSession, {}));
    }
    DisconnectLocked();
}

bool SemanticClient::IsConnected() const noexcept {
    std::scoped_lock lock(mutex_);
    return pipe_ != INVALID_HANDLE_VALUE && sessionId_ != 0;
}

Response SemanticClient::SendLocked(MessageType type, std::span<const std::byte> payload) noexcept {
    IpcTrace trace(type);
    Response failure{Result::InputLost, {}};
    if (pipe_ == INVALID_HANDLE_VALUE || payload.size() > MaximumPayloadLength) return failure;
    const auto requestId = ++requestId_ == 0 ? ++requestId_ : requestId_;
    std::array<std::byte, HeaderLength> headerBytes{};
    const FrameHeader request{
        MajorVersion,
        MinorVersion,
        type,
        FrameFlags::None,
        static_cast<std::uint32_t>(payload.size()),
        requestId,
        type == MessageType::Hello ? 0 : sessionId_};
    if (!EncodeHeader(headerBytes, request) ||
        !TransferLocked(true, headerBytes) ||
        (!payload.empty() && !TransferLocked(
            true,
            std::span<std::byte>(const_cast<std::byte*>(payload.data()), payload.size())))) {
        DisconnectLocked();
        return failure;
    }

    std::array<std::byte, HeaderLength> responseHeaderBytes{};
    if (!TransferLocked(false, responseHeaderBytes)) {
        DisconnectLocked();
        return failure;
    }
    FrameHeader responseHeader{};
    if (!DecodeHeader(responseHeaderBytes, responseHeader) ||
        responseHeader.majorVersion != MajorVersion ||
        responseHeader.minorVersion > MinorVersion ||
        responseHeader.messageType != type ||
        responseHeader.requestId != requestId ||
        responseHeader.payloadLength < sizeof(std::int32_t) ||
        (static_cast<std::uint16_t>(responseHeader.flags) & static_cast<std::uint16_t>(FrameFlags::Response)) == 0) {
        DisconnectLocked();
        return {Result::ProtocolError, {}};
    }
    std::vector<std::byte> responseBytes(responseHeader.payloadLength);
    if (!TransferLocked(false, responseBytes)) {
        DisconnectLocked();
        return failure;
    }
    const auto result = static_cast<Result>(static_cast<std::int32_t>(detail::Read32(responseBytes, 0)));
    if (type == MessageType::Hello && result == Result::Ok) {
        if (responseHeader.sessionId == 0) {
            DisconnectLocked();
            return {Result::ProtocolError, {}};
        }
        sessionId_ = responseHeader.sessionId;
    } else if (responseHeader.sessionId != sessionId_) {
        DisconnectLocked();
        return {Result::ProtocolError, {}};
    }
    responseBytes.erase(responseBytes.begin(), responseBytes.begin() + sizeof(std::int32_t));
    return {result, std::move(responseBytes)};
}

bool SemanticClient::TransferLocked(bool write, std::span<std::byte> bytes) noexcept {
    while (!bytes.empty()) {
        const auto event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event == nullptr) return false;
        OVERLAPPED overlapped{};
        overlapped.hEvent = event;
        DWORD transferred{};
        const auto requested = static_cast<DWORD>(std::min<std::size_t>(bytes.size(), MAXDWORD));
        const auto immediate = write
            ? WriteFile(pipe_, bytes.data(), requested, nullptr, &overlapped)
            : ReadFile(pipe_, bytes.data(), requested, nullptr, &overlapped);
        if (!immediate && GetLastError() != ERROR_IO_PENDING) {
            CloseHandle(event);
            return false;
        }
        if (!immediate) {
            const auto wait = WaitForSingleObject(event, RequestTimeoutMilliseconds);
            if (wait != WAIT_OBJECT_0 || !GetOverlappedResult(pipe_, &overlapped, &transferred, FALSE)) {
                CancelIoEx(pipe_, &overlapped);
                WaitForSingleObject(event, INFINITE);
                CloseHandle(event);
                return false;
            }
        } else if (!GetOverlappedResult(pipe_, &overlapped, &transferred, FALSE)) {
            CloseHandle(event);
            return false;
        }
        CloseHandle(event);
        if (transferred == 0 || transferred > bytes.size()) return false;
        bytes = bytes.subspan(transferred);
    }
    return true;
}

void SemanticClient::DisconnectLocked() noexcept {
    sessionId_ = 0;
    if (pipe_ != INVALID_HANDLE_VALUE) {
        CancelIoEx(pipe_, nullptr);
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
}

void SemanticClient::HeartbeatLoop(std::stop_token stop) noexcept {
    std::mutex waitMutex;
    std::unique_lock waitLock(waitMutex);
    while (!stop.stop_requested()) {
        heartbeatWake_.wait_for(waitLock, std::chrono::milliseconds(100));
        if (stop.stop_requested()) break;
        std::scoped_lock lock(mutex_);
        if (pipe_ == INVALID_HANDLE_VALUE || sessionId_ == 0) continue;
        if (SendLocked(MessageType::Heartbeat, {}).result != Result::Ok) DisconnectLocked();
    }
}

} // namespace logicontrol::ipc
