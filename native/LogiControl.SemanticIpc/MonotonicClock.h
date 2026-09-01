// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <windows.h>

#include <cstdint>

namespace logicontrol::ipc::detail {

constexpr std::uint64_t ScaleTicksToMicroseconds(
    std::uint64_t ticks,
    std::uint64_t frequency) noexcept {
    if (frequency == 0) return 0;
    const auto seconds = ticks / frequency;
    const auto remainder = ticks % frequency;
    return seconds * 1'000'000ULL + remainder * 1'000'000ULL / frequency;
}

inline std::uint64_t QpcMicroseconds() noexcept {
    LARGE_INTEGER counter{};
    if (!QueryPerformanceCounter(&counter)) return 0;
    static const std::uint64_t frequency = []() noexcept {
        LARGE_INTEGER value{};
        return QueryPerformanceFrequency(&value)
            ? static_cast<std::uint64_t>(value.QuadPart)
            : 0;
    }();
    return ScaleTicksToMicroseconds(static_cast<std::uint64_t>(counter.QuadPart), frequency);
}

} // namespace logicontrol::ipc::detail
