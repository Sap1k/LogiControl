// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <vector>

namespace logicontrol::ipc {

inline constexpr std::uint32_t Magic = 0x4646434C;
inline constexpr std::uint16_t MajorVersion = 1;
inline constexpr std::uint16_t MinorVersion = 0;
inline constexpr std::size_t HeaderLength = 32;
inline constexpr std::size_t MaximumFrameLength = 64 * 1024;
inline constexpr std::size_t MaximumPayloadLength = MaximumFrameLength - HeaderLength;
inline constexpr std::size_t MaximumCustomSamples = 4096;
inline constexpr wchar_t PipeName[] = LR"(\\.\pipe\LogiControl.Broker.v1)";

enum class MessageType : std::uint16_t {
    Hello = 1,
    BindDevice = 2,
    Heartbeat = 3,
    CloseSession = 4,
    ValidateEffect = 10,
    UpsertEffect = 11,
    StartEffect = 12,
    StopEffect = 13,
    DestroyEffect = 14,
    QueryEffect = 15,
    SetGain = 16,
    DeviceCommand = 17,
    QueryDeviceState = 18,
    SetRuntimeSettings = 30,
    QueryRuntimeSettings = 31,
    QueryStatus = 32,
    QueryTelemetry = 33,
    EmergencyStop = 34,
};

enum class FrameFlags : std::uint16_t {
    None = 0,
    Response = 1,
    Error = 2,
};

enum class Result : std::int32_t {
    Ok = 0,
    InvalidArgument = 1,
    Unsupported = 2,
    DeviceFull = 3,
    OtherApplicationHasPriority = 4,
    InputLost = 5,
    NotFound = 6,
    DeviceNotReady = 7,
    ProtocolError = 8,
    InternalError = 9,
};

struct FrameHeader final {
    std::uint16_t majorVersion{MajorVersion};
    std::uint16_t minorVersion{MinorVersion};
    MessageType messageType{};
    FrameFlags flags{};
    std::uint32_t payloadLength{};
    std::uint64_t requestId{};
    std::uint64_t sessionId{};
};

enum class EffectKind : std::uint8_t {
    Constant = 1,
    Ramp = 2,
    Square = 3,
    Sine = 4,
    Triangle = 5,
    SawtoothUp = 6,
    SawtoothDown = 7,
    Spring = 8,
    Damper = 9,
    Friction = 10,
    Inertia = 11,
    Custom = 12,
};

enum class EffectUpdateMask : std::uint16_t {
    None = 0,
    Duration = 1U << 0,
    StartDelay = 1U << 1,
    SamplePeriod = 1U << 2,
    Gain = 1U << 3,
    Direction = 1U << 4,
    Envelope = 1U << 5,
    TypeSpecific = 1U << 6,
    Axis = 1U << 7,
    All = 0x00FF,
};

struct Envelope final {
    std::int32_t attackLevel{};
    std::uint32_t attackTimeMicroseconds{};
    std::int32_t fadeLevel{};
    std::uint32_t fadeTimeMicroseconds{};
};

struct EffectDefinition final {
    EffectKind kind{EffectKind::Constant};
    std::uint32_t durationMicroseconds{UINT32_MAX};
    std::uint32_t startDelayMicroseconds{};
    std::uint32_t samplePeriodMicroseconds{};
    std::int32_t gain{10000};
    std::int32_t direction{10000};
    std::uint32_t axisIdentifier{};
    std::optional<Envelope> envelope;
    std::int32_t magnitude{};
    std::int32_t offset{};
    std::int32_t secondMagnitude{};
    std::uint32_t phaseHundredthsOfDegree{};
    std::uint32_t periodMicroseconds{};
    std::int32_t positiveCoefficient{};
    std::int32_t negativeCoefficient{};
    std::int32_t positiveSaturation{};
    std::int32_t negativeSaturation{};
    std::int32_t deadBand{};
    std::vector<std::int32_t> customSamples;
};

namespace detail {

inline void Write16(std::span<std::byte> destination, std::size_t offset, std::uint16_t value) noexcept {
    destination[offset] = static_cast<std::byte>(value);
    destination[offset + 1] = static_cast<std::byte>(value >> 8);
}

inline void Write32(std::span<std::byte> destination, std::size_t offset, std::uint32_t value) noexcept {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[offset + index] = static_cast<std::byte>(value >> (index * 8));
    }
}

inline void Write64(std::span<std::byte> destination, std::size_t offset, std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < 8; ++index) {
        destination[offset + index] = static_cast<std::byte>(value >> (index * 8));
    }
}

inline std::uint16_t Read16(std::span<const std::byte> source, std::size_t offset) noexcept {
    return static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(source[offset])) |
        static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(source[offset + 1]) << 8);
}

inline std::uint32_t Read32(std::span<const std::byte> source, std::size_t offset) noexcept {
    std::uint32_t value{};
    for (std::size_t index = 0; index < 4; ++index) {
        value |= static_cast<std::uint32_t>(std::to_integer<std::uint8_t>(source[offset + index])) << (index * 8);
    }
    return value;
}

inline std::uint64_t Read64(std::span<const std::byte> source, std::size_t offset) noexcept {
    std::uint64_t value{};
    for (std::size_t index = 0; index < 8; ++index) {
        value |= static_cast<std::uint64_t>(std::to_integer<std::uint8_t>(source[offset + index])) << (index * 8);
    }
    return value;
}

} // namespace detail

inline bool EncodeHeader(std::span<std::byte> destination, const FrameHeader& header) noexcept {
    if (destination.size() < HeaderLength || header.payloadLength > MaximumPayloadLength) return false;
    std::fill_n(destination.begin(), HeaderLength, std::byte{});
    detail::Write32(destination, 0, Magic);
    detail::Write16(destination, 4, header.majorVersion);
    detail::Write16(destination, 6, header.minorVersion);
    detail::Write16(destination, 8, static_cast<std::uint16_t>(header.messageType));
    detail::Write16(destination, 10, static_cast<std::uint16_t>(header.flags));
    detail::Write32(destination, 12, header.payloadLength);
    detail::Write64(destination, 16, header.requestId);
    detail::Write64(destination, 24, header.sessionId);
    return true;
}

inline bool DecodeHeader(std::span<const std::byte> source, FrameHeader& header) noexcept {
    if (source.size() < HeaderLength || detail::Read32(source, 0) != Magic) return false;
    const auto payloadLength = detail::Read32(source, 12);
    if (payloadLength > MaximumPayloadLength) return false;
    header.majorVersion = detail::Read16(source, 4);
    header.minorVersion = detail::Read16(source, 6);
    header.messageType = static_cast<MessageType>(detail::Read16(source, 8));
    header.flags = static_cast<FrameFlags>(detail::Read16(source, 10));
    header.payloadLength = payloadLength;
    header.requestId = detail::Read64(source, 16);
    header.sessionId = detail::Read64(source, 24);
    return true;
}

inline std::size_t EncodedEffectLength(const EffectDefinition& effect) noexcept {
    const auto common = 28U + (effect.envelope.has_value() ? 16U : 0U);
    switch (effect.kind) {
    case EffectKind::Constant: return common + 4U;
    case EffectKind::Ramp: return common + 8U;
    case EffectKind::Square:
    case EffectKind::Sine:
    case EffectKind::Triangle:
    case EffectKind::SawtoothUp:
    case EffectKind::SawtoothDown: return common + 16U;
    case EffectKind::Spring:
    case EffectKind::Damper:
    case EffectKind::Friction:
    case EffectKind::Inertia: return common + 28U;
    case EffectKind::Custom: return common + 4U + effect.customSamples.size() * 4U;
    }
    return 0;
}

inline bool EncodeEffect(std::span<std::byte> destination, const EffectDefinition& effect) noexcept {
    const auto length = EncodedEffectLength(effect);
    if (length == 0 || length > MaximumPayloadLength || destination.size() < length ||
        effect.customSamples.size() > MaximumCustomSamples) return false;
    std::fill_n(destination.begin(), length, std::byte{});
    destination[0] = static_cast<std::byte>(effect.kind);
    destination[1] = effect.envelope.has_value() ? std::byte{1} : std::byte{};
    detail::Write32(destination, 4, effect.durationMicroseconds);
    detail::Write32(destination, 8, effect.startDelayMicroseconds);
    detail::Write32(destination, 12, effect.samplePeriodMicroseconds);
    detail::Write32(destination, 16, static_cast<std::uint32_t>(effect.gain));
    detail::Write32(destination, 20, static_cast<std::uint32_t>(effect.direction));
    detail::Write32(destination, 24, effect.axisIdentifier);
    std::size_t offset = 28;
    if (effect.envelope.has_value()) {
        detail::Write32(destination, offset, static_cast<std::uint32_t>(effect.envelope->attackLevel));
        detail::Write32(destination, offset + 4, effect.envelope->attackTimeMicroseconds);
        detail::Write32(destination, offset + 8, static_cast<std::uint32_t>(effect.envelope->fadeLevel));
        detail::Write32(destination, offset + 12, effect.envelope->fadeTimeMicroseconds);
        offset += 16;
    }
    const auto writeSigned = [&](std::size_t at, std::int32_t value) {
        detail::Write32(destination, at, static_cast<std::uint32_t>(value));
    };
    switch (effect.kind) {
    case EffectKind::Constant:
        writeSigned(offset, effect.magnitude);
        break;
    case EffectKind::Ramp:
        writeSigned(offset, effect.magnitude);
        writeSigned(offset + 4, effect.secondMagnitude);
        break;
    case EffectKind::Square:
    case EffectKind::Sine:
    case EffectKind::Triangle:
    case EffectKind::SawtoothUp:
    case EffectKind::SawtoothDown:
        writeSigned(offset, effect.magnitude);
        writeSigned(offset + 4, effect.offset);
        detail::Write32(destination, offset + 8, effect.phaseHundredthsOfDegree);
        detail::Write32(destination, offset + 12, effect.periodMicroseconds);
        break;
    case EffectKind::Spring:
    case EffectKind::Damper:
    case EffectKind::Friction:
    case EffectKind::Inertia:
        writeSigned(offset, effect.offset);
        writeSigned(offset + 4, effect.positiveCoefficient);
        writeSigned(offset + 8, effect.negativeCoefficient);
        writeSigned(offset + 12, effect.positiveSaturation);
        writeSigned(offset + 16, effect.negativeSaturation);
        writeSigned(offset + 20, effect.deadBand);
        break;
    case EffectKind::Custom:
        detail::Write32(destination, offset, static_cast<std::uint32_t>(effect.customSamples.size()));
        offset += 4;
        for (const auto sample : effect.customSamples) {
            writeSigned(offset, sample);
            offset += 4;
        }
        break;
    }
    return true;
}

} // namespace logicontrol::ipc
