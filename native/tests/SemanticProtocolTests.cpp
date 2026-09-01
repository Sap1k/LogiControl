// SPDX-License-Identifier: GPL-3.0-or-later
#include <array>
#include <cstdlib>
#include <iostream>

#include "../LogiControl.SemanticIpc/SemanticProtocol.h"
#include "../LogiControl.SemanticIpc/MonotonicClock.h"

namespace {

void Require(bool condition, const char* message) {
    if (condition) return;
    std::cerr << message << '\n';
    std::exit(1);
}

} // namespace

int main() {
    using namespace logicontrol::ipc;
    std::array<std::byte, HeaderLength> encoded{};
    const FrameHeader header{
        1,
        0,
        MessageType::StartEffect,
        FrameFlags::None,
        4,
        0x0102030405060708ULL,
        9};
    Require(EncodeHeader(encoded, header), "Header did not encode.");
    constexpr std::array<std::uint8_t, HeaderLength> expected{
        0x4C, 0x43, 0x46, 0x46, 1, 0, 0, 0, 12, 0, 0, 0, 4, 0, 0, 0,
        8, 7, 6, 5, 4, 3, 2, 1, 9, 0, 0, 0, 0, 0, 0, 0};
    for (std::size_t index = 0; index < encoded.size(); ++index) {
        Require(std::to_integer<std::uint8_t>(encoded[index]) == expected[index], "Header golden vector differs.");
    }

    FrameHeader decoded{};
    Require(DecodeHeader(encoded, decoded), "Header did not decode.");
    Require(decoded.requestId == header.requestId && decoded.sessionId == header.sessionId &&
        decoded.messageType == header.messageType && decoded.payloadLength == header.payloadLength,
        "Header round trip differs.");

    EffectDefinition effect{};
    effect.kind = EffectKind::Triangle;
    effect.durationMicroseconds = 2'000'000;
    effect.startDelayMicroseconds = 10'000;
    effect.samplePeriodMicroseconds = 2'000;
    effect.gain = 7'500;
    effect.direction = -10'000;
    effect.envelope = Envelope{1'000, 20'000, 2'000, 30'000};
    effect.magnitude = 8'000;
    effect.offset = -500;
    effect.phaseHundredthsOfDegree = 9'000;
    effect.periodMicroseconds = 100'000;
    std::vector<std::byte> effectBytes(EncodedEffectLength(effect));
    Require(effectBytes.size() == 60, "Periodic effect payload has the wrong length.");
    Require(EncodeEffect(effectBytes, effect), "Periodic effect did not encode.");
    Require(std::to_integer<std::uint8_t>(effectBytes[0]) == 5 &&
        std::to_integer<std::uint8_t>(effectBytes[1]) == 1 &&
        detail::Read32(effectBytes, 4) == 2'000'000 &&
        static_cast<std::int32_t>(detail::Read32(effectBytes, 20)) == -10'000 &&
        detail::Read32(effectBytes, 52) == 9'000,
        "Periodic effect payload differs from the managed codec contract.");

    EffectDefinition custom{};
    custom.kind = EffectKind::Custom;
    custom.customSamples = {-10'000, 0, 10'000};
    std::vector<std::byte> customBytes(EncodedEffectLength(custom));
    Require(EncodeEffect(customBytes, custom), "Custom effect did not encode.");
    Require(detail::Read32(customBytes, 28) == 3 &&
        static_cast<std::int32_t>(detail::Read32(customBytes, 32)) == -10'000,
        "Custom effect payload differs.");

    constexpr std::uint64_t overflowBoundary = 9'223'372'036'854ULL;
    Require(detail::ScaleTicksToMicroseconds(overflowBoundary, 10'000'000ULL) == 922'337'203'685ULL &&
        detail::ScaleTicksToMicroseconds(overflowBoundary + 10'000'000ULL, 10'000'000ULL) ==
            922'338'203'685ULL,
        "QPC scaling differs across the former multiplication-overflow boundary.");

    std::cout << "{\"semanticProtocolVectors\":4,\"passed\":true}\n";
    return 0;
}
