// SPDX-License-Identifier: GPL-3.0-or-later
#include "EffectMarshaller.h"

#include <algorithm>
#include <ranges>

namespace logicontrol::provider {
namespace {

using logicontrol::ipc::EffectDefinition;
using logicontrol::ipc::EffectKind;
using logicontrol::ipc::EffectUpdateMask;

constexpr EffectUpdateMask operator|(EffectUpdateMask left, EffectUpdateMask right) noexcept {
    return static_cast<EffectUpdateMask>(
        static_cast<std::uint16_t>(left) | static_cast<std::uint16_t>(right));
}

void AddMask(EffectUpdateMask& value, EffectUpdateMask added) noexcept {
    value = value | added;
}

bool SignedMagnitude(LONG value) noexcept { return value >= -10000 && value <= 10000; }
bool UnsignedMagnitude(DWORD value) noexcept { return value <= 10000; }

HRESULT SetKind(DWORD effectId, EffectDefinition& target) noexcept {
    switch (effectId) {
    case ConstantEffectId: target.kind = EffectKind::Constant; return S_OK;
    case RampEffectId: target.kind = EffectKind::Ramp; return S_OK;
    case SquareEffectId: target.kind = EffectKind::Square; target.periodMicroseconds = 1; return S_OK;
    case SineEffectId: target.kind = EffectKind::Sine; target.periodMicroseconds = 1; return S_OK;
    case TriangleEffectId: target.kind = EffectKind::Triangle; target.periodMicroseconds = 1; return S_OK;
    case SawtoothUpEffectId: target.kind = EffectKind::SawtoothUp; target.periodMicroseconds = 1; return S_OK;
    case SawtoothDownEffectId: target.kind = EffectKind::SawtoothDown; target.periodMicroseconds = 1; return S_OK;
    case SpringEffectId: target.kind = EffectKind::Spring; return S_OK;
    case DamperEffectId: target.kind = EffectKind::Damper; return S_OK;
    case InertiaEffectId: target.kind = EffectKind::Inertia; return S_OK;
    case FrictionEffectId: target.kind = EffectKind::Friction; return S_OK;
    case CustomForceEffectId:
        target.kind = EffectKind::Custom;
        target.samplePeriodMicroseconds = 1;
        target.customSamples.push_back(0);
        return S_OK;
    default: return DIERR_UNSUPPORTED;
    }
}

HRESULT MarshalTypeSpecific(const DIEFFECT& source, EffectDefinition& target) {
    if (source.lpvTypeSpecificParams == nullptr) return DIERR_INVALIDPARAM;
    switch (target.kind) {
    case EffectKind::Constant: {
        if (source.cbTypeSpecificParams != sizeof(DICONSTANTFORCE)) return DIERR_INVALIDPARAM;
        const auto& value = *static_cast<const DICONSTANTFORCE*>(source.lpvTypeSpecificParams);
        if (!SignedMagnitude(value.lMagnitude)) return DIERR_INVALIDPARAM;
        target.magnitude = value.lMagnitude;
        return S_OK;
    }
    case EffectKind::Ramp: {
        if (source.cbTypeSpecificParams != sizeof(DIRAMPFORCE)) return DIERR_INVALIDPARAM;
        const auto& value = *static_cast<const DIRAMPFORCE*>(source.lpvTypeSpecificParams);
        if (!SignedMagnitude(value.lStart) || !SignedMagnitude(value.lEnd)) return DIERR_INVALIDPARAM;
        target.magnitude = value.lStart;
        target.secondMagnitude = value.lEnd;
        return S_OK;
    }
    case EffectKind::Square:
    case EffectKind::Sine:
    case EffectKind::Triangle:
    case EffectKind::SawtoothUp:
    case EffectKind::SawtoothDown: {
        if (source.cbTypeSpecificParams != sizeof(DIPERIODIC)) return DIERR_INVALIDPARAM;
        const auto& value = *static_cast<const DIPERIODIC*>(source.lpvTypeSpecificParams);
        if (!UnsignedMagnitude(value.dwMagnitude) || !SignedMagnitude(value.lOffset) ||
            value.dwPhase >= 36000 || value.dwPeriod == 0) return DIERR_INVALIDPARAM;
        target.magnitude = static_cast<std::int32_t>(value.dwMagnitude);
        target.offset = value.lOffset;
        target.phaseHundredthsOfDegree = value.dwPhase;
        target.periodMicroseconds = value.dwPeriod;
        return S_OK;
    }
    case EffectKind::Spring:
    case EffectKind::Damper:
    case EffectKind::Friction:
    case EffectKind::Inertia: {
        if (source.cbTypeSpecificParams != sizeof(DICONDITION)) return DIERR_INVALIDPARAM;
        const auto& value = *static_cast<const DICONDITION*>(source.lpvTypeSpecificParams);
        if (!SignedMagnitude(value.lOffset) || !SignedMagnitude(value.lPositiveCoefficient) ||
            !SignedMagnitude(value.lNegativeCoefficient) || !UnsignedMagnitude(value.dwPositiveSaturation) ||
            !UnsignedMagnitude(value.dwNegativeSaturation) || value.lDeadBand < 0 || value.lDeadBand > 10000) {
            return DIERR_INVALIDPARAM;
        }
        target.offset = value.lOffset;
        target.positiveCoefficient = value.lPositiveCoefficient;
        target.negativeCoefficient = value.lNegativeCoefficient;
        target.positiveSaturation = static_cast<std::int32_t>(value.dwPositiveSaturation);
        target.negativeSaturation = static_cast<std::int32_t>(value.dwNegativeSaturation);
        target.deadBand = value.lDeadBand;
        return S_OK;
    }
    case EffectKind::Custom: {
        if (source.cbTypeSpecificParams != sizeof(DICUSTOMFORCE)) return DIERR_INVALIDPARAM;
        const auto& value = *static_cast<const DICUSTOMFORCE*>(source.lpvTypeSpecificParams);
        if (value.cChannels != 1 || value.dwSamplePeriod == 0 || value.cSamples == 0 ||
            value.cSamples > logicontrol::ipc::MaximumCustomSamples || value.rglForceData == nullptr) {
            return DIERR_INVALIDPARAM;
        }
        target.customSamples.assign(value.rglForceData, value.rglForceData + value.cSamples);
        if (std::ranges::any_of(target.customSamples, [](LONG sample) { return !SignedMagnitude(sample); })) {
            return DIERR_INVALIDPARAM;
        }
        target.samplePeriodMicroseconds = value.dwSamplePeriod;
        return S_OK;
    }
    }
    return DIERR_UNSUPPORTED;
}

} // namespace

HRESULT MarshalEffect(
    DWORD effectId,
    const DIEFFECT& source,
    DWORD flags,
    EffectDefinition& target,
    EffectUpdateMask& updateMask) noexcept {
    if (source.dwSize < sizeof(DIEFFECT)) return DIERR_INVALIDPARAM;
    target = {};
    updateMask = EffectUpdateMask::None;
    const auto kindResult = SetKind(effectId, target);
    if (FAILED(kindResult)) return kindResult;

    if ((flags & DIEP_DURATION) != 0) {
        target.durationMicroseconds = source.dwDuration;
        AddMask(updateMask, EffectUpdateMask::Duration);
    }
    if ((flags & DIEP_STARTDELAY) != 0) {
        target.startDelayMicroseconds = source.dwStartDelay;
        AddMask(updateMask, EffectUpdateMask::StartDelay);
    }
    if ((flags & DIEP_SAMPLEPERIOD) != 0) {
        target.samplePeriodMicroseconds = source.dwSamplePeriod;
        AddMask(updateMask, EffectUpdateMask::SamplePeriod);
    }
    if ((flags & DIEP_GAIN) != 0) {
        if (!UnsignedMagnitude(source.dwGain)) return DIERR_INVALIDPARAM;
        target.gain = static_cast<std::int32_t>(source.dwGain);
        AddMask(updateMask, EffectUpdateMask::Gain);
    }
    if ((flags & DIEP_AXES) != 0) {
        if (source.cAxes != 1 || source.rgdwAxes == nullptr) return DIERR_INVALIDPARAM;
        target.axisIdentifier = source.rgdwAxes[0];
        AddMask(updateMask, EffectUpdateMask::Axis);
    }
    if ((flags & DIEP_DIRECTION) != 0) {
        if (source.cAxes != 1 || source.rglDirection == nullptr ||
            (source.dwFlags & DIEFF_CARTESIAN) == 0 || !SignedMagnitude(source.rglDirection[0])) {
            return DIERR_INVALIDPARAM;
        }
        target.direction = source.rglDirection[0];
        AddMask(updateMask, EffectUpdateMask::Direction);
    }
    if ((flags & DIEP_ENVELOPE) != 0) {
        if (source.lpEnvelope != nullptr) {
            if (source.lpEnvelope->dwSize < sizeof(DIENVELOPE) ||
                !UnsignedMagnitude(source.lpEnvelope->dwAttackLevel) ||
                !UnsignedMagnitude(source.lpEnvelope->dwFadeLevel)) return DIERR_INVALIDPARAM;
            target.envelope = logicontrol::ipc::Envelope{
                static_cast<std::int32_t>(source.lpEnvelope->dwAttackLevel),
                source.lpEnvelope->dwAttackTime,
                static_cast<std::int32_t>(source.lpEnvelope->dwFadeLevel),
                source.lpEnvelope->dwFadeTime};
        }
        AddMask(updateMask, EffectUpdateMask::Envelope);
    }
    if ((flags & DIEP_TYPESPECIFICPARAMS) != 0) {
        try {
            const auto result = MarshalTypeSpecific(source, target);
            if (FAILED(result)) return result;
        } catch (...) {
            return E_OUTOFMEMORY;
        }
        AddMask(updateMask, EffectUpdateMask::TypeSpecific);
        if (target.kind == EffectKind::Custom) {
            const auto customPeriod = target.samplePeriodMicroseconds;
            if ((flags & DIEP_SAMPLEPERIOD) != 0 &&
                source.dwSamplePeriod != 0 && source.dwSamplePeriod != customPeriod) {
                return DIERR_INVALIDPARAM;
            }
            target.samplePeriodMicroseconds = customPeriod;
            AddMask(updateMask, EffectUpdateMask::SamplePeriod);
        }
    }
    return S_OK;
}

} // namespace logicontrol::provider
