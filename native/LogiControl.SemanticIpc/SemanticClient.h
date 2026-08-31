// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <windows.h>

#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <span>
#include <stop_token>
#include <thread>
#include <vector>

#include "SemanticProtocol.h"

namespace logicontrol::ipc {

struct Response final {
    Result result{Result::InternalError};
    std::vector<std::byte> payload;
};

class SemanticClient final {
public:
    SemanticClient();
    ~SemanticClient();

    SemanticClient(const SemanticClient&) = delete;
    SemanticClient& operator=(const SemanticClient&) = delete;

    Result ConnectAndBind(const wchar_t* devicePath) noexcept;
    Response Send(MessageType type, std::span<const std::byte> payload = {}) noexcept;
    void Close() noexcept;
    [[nodiscard]] bool IsConnected() const noexcept;

private:
    static constexpr DWORD ConnectTimeoutMilliseconds = 50;
    static constexpr DWORD RequestTimeoutMilliseconds = 250;

    Response SendLocked(MessageType type, std::span<const std::byte> payload) noexcept;
    bool TransferLocked(bool write, std::span<std::byte> bytes) noexcept;
    void DisconnectLocked() noexcept;
    void HeartbeatLoop(std::stop_token stop) noexcept;

    mutable std::mutex mutex_;
    HANDLE pipe_{INVALID_HANDLE_VALUE};
    std::uint64_t requestId_{};
    std::uint64_t sessionId_{};
    std::condition_variable heartbeatWake_;
    std::jthread heartbeat_;
    bool profileEnabled_{};
};

} // namespace logicontrol::ipc
