// SPDX-License-Identifier: GPL-3.0-or-later
#include <windows.h>
#include <dinput.h>

#include <array>
#include <cstdlib>
#include <iostream>

#include "../LogiControl.Ffb/src/EffectMarshaller.h"

namespace {

void Require(bool condition, const char* message) {
    if (condition) return;
    std::cerr << message << '\n';
    std::exit(1);
}

} // namespace

int main() {
    using namespace logicontrol::ipc;
    using namespace logicontrol::provider;

    DIEFFECT gainOnly{};
    gainOnly.dwSize = sizeof(gainOnly);
    gainOnly.dwGain = 7500;
    // Poison unselected pointers: a partial update must not dereference them.
    gainOnly.rgdwAxes = reinterpret_cast<DWORD*>(1);
    gainOnly.rglDirection = reinterpret_cast<LONG*>(1);
    gainOnly.lpEnvelope = reinterpret_cast<DIENVELOPE*>(1);
    gainOnly.lpvTypeSpecificParams = reinterpret_cast<void*>(1);
    EffectDefinition gainDefinition{};
    EffectUpdateMask gainMask{};
    Require(SUCCEEDED(MarshalEffect(ConstantEffectId, gainOnly, DIEP_GAIN, gainDefinition, gainMask)),
        "Gain-only update failed or dereferenced an unselected field.");
    Require(gainDefinition.gain == 7500 && gainMask == EffectUpdateMask::Gain,
        "Gain-only update produced the wrong semantic mask.");

    DWORD axis = 42;
    LONG direction = -10000;
    DIPERIODIC periodic{8000, -500, 9000, 100000};
    DIEFFECT all{};
    all.dwSize = sizeof(all);
    all.dwFlags = DIEFF_CARTESIAN;
    all.dwDuration = 2'000'000;
    all.dwSamplePeriod = 2'000;
    all.dwGain = 7'500;
    all.dwTriggerButton = DIEB_NOTRIGGER;
    all.cAxes = 1;
    all.rgdwAxes = &axis;
    all.rglDirection = &direction;
    all.cbTypeSpecificParams = sizeof(periodic);
    all.lpvTypeSpecificParams = &periodic;
    all.dwStartDelay = 10'000;
    EffectDefinition periodicDefinition{};
    EffectUpdateMask allMask{};
    Require(SUCCEEDED(MarshalEffect(TriangleEffectId, all, DIEP_ALLPARAMS, periodicDefinition, allMask)),
        "Complete periodic effect did not marshal.");
    Require(periodicDefinition.kind == EffectKind::Triangle && periodicDefinition.axisIdentifier == 42 &&
        periodicDefinition.direction == -10000 && periodicDefinition.periodMicroseconds == 100000 &&
        periodicDefinition.magnitude == 8000 && allMask == EffectUpdateMask::All,
        "Complete periodic semantic fields differ.");

    constexpr std::array<DWORD, 3> directionEffectIds{ConstantEffectId, SineEffectId, SpringEffectId};
    constexpr std::array<LONG, 5> directionInputs{1, 10000, 0, -1, -10000};
    constexpr std::array<std::int32_t, 5> expectedDirections{10000, 10000, 10000, -10000, -10000};
    for (const auto effectId : directionEffectIds) {
        for (std::size_t index = 0; index < directionInputs.size(); ++index) {
            LONG input = directionInputs[index];
            DIEFFECT directionOnly{};
            directionOnly.dwSize = sizeof(directionOnly);
            directionOnly.dwFlags = DIEFF_CARTESIAN;
            directionOnly.cAxes = 1;
            directionOnly.rglDirection = &input;
            EffectDefinition directionDefinition{};
            EffectUpdateMask directionMask{};
            Require(SUCCEEDED(MarshalEffect(
                effectId, directionOnly, DIEP_DIRECTION, directionDefinition, directionMask)),
                "Valid Cartesian direction failed to marshal.");
            Require(directionDefinition.direction == expectedDirections[index] &&
                directionMask == EffectUpdateMask::Direction,
                "Cartesian direction was not normalized to its one-axis sign.");
        }
    }

    LONG outOfRangeDirection = 10001;
    DIEFFECT invalidDirection{};
    invalidDirection.dwSize = sizeof(invalidDirection);
    invalidDirection.dwFlags = DIEFF_CARTESIAN;
    invalidDirection.cAxes = 1;
    invalidDirection.rglDirection = &outOfRangeDirection;
    EffectDefinition invalidDirectionDefinition{};
    EffectUpdateMask invalidDirectionMask{};
    Require(MarshalEffect(ConstantEffectId, invalidDirection, DIEP_DIRECTION,
        invalidDirectionDefinition, invalidDirectionMask) == DIERR_INVALIDPARAM,
        "Out-of-range Cartesian direction was accepted.");

    DICUSTOMFORCE malicious{};
    malicious.cChannels = 1;
    malicious.dwSamplePeriod = 1000;
    malicious.cSamples = 4097;
    malicious.rglForceData = reinterpret_cast<LONG*>(1);
    DIEFFECT custom{};
    custom.dwSize = sizeof(custom);
    custom.cbTypeSpecificParams = sizeof(malicious);
    custom.lpvTypeSpecificParams = &malicious;
    EffectDefinition customDefinition{};
    EffectUpdateMask customMask{};
    Require(MarshalEffect(CustomForceEffectId, custom, DIEP_TYPESPECIFICPARAMS, customDefinition, customMask) ==
        DIERR_INVALIDPARAM, "Oversized custom force data was accepted.");

    LONG customSamples[]{-10000, 0, 10000};
    DICUSTOMFORCE customParameters{};
    customParameters.cChannels = 1;
    customParameters.dwSamplePeriod = 2000;
    customParameters.cSamples = 3;
    customParameters.rglForceData = customSamples;
    custom.dwSamplePeriod = 1000;
    custom.cbTypeSpecificParams = sizeof(customParameters);
    custom.lpvTypeSpecificParams = &customParameters;
    Require(MarshalEffect(CustomForceEffectId, custom,
        DIEP_SAMPLEPERIOD | DIEP_TYPESPECIFICPARAMS, customDefinition, customMask) == DIERR_INVALIDPARAM,
        "Conflicting custom sample periods were accepted.");

    custom.dwSamplePeriod = 2000;
    Require(SUCCEEDED(MarshalEffect(CustomForceEffectId, custom,
        DIEP_SAMPLEPERIOD | DIEP_TYPESPECIFICPARAMS, customDefinition, customMask)),
        "A valid custom effect failed to marshal.");
    Require(customDefinition.samplePeriodMicroseconds == 2000 && customDefinition.customSamples.size() == 3,
        "Custom sample data or period was not copied semantically.");

    std::cout << "{\"effectMarshallerCases\":6,\"passed\":true}\n";
    return 0;
}
