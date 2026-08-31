// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <windows.h>
#include <dinput.h>

#include "../../LogiControl.SemanticIpc/SemanticProtocol.h"

namespace logicontrol::provider {

inline constexpr DWORD ConstantEffectId = 0;
inline constexpr DWORD RampEffectId = 1;
inline constexpr DWORD SquareEffectId = 2;
inline constexpr DWORD SineEffectId = 3;
inline constexpr DWORD TriangleEffectId = 4;
inline constexpr DWORD SawtoothUpEffectId = 5;
inline constexpr DWORD SawtoothDownEffectId = 6;
inline constexpr DWORD SpringEffectId = 7;
inline constexpr DWORD DamperEffectId = 8;
inline constexpr DWORD InertiaEffectId = 9;
inline constexpr DWORD FrictionEffectId = 10;
inline constexpr DWORD CustomForceEffectId = 0x100;

HRESULT MarshalEffect(
    DWORD effectId,
    const DIEFFECT& source,
    DWORD flags,
    logicontrol::ipc::EffectDefinition& target,
    logicontrol::ipc::EffectUpdateMask& updateMask) noexcept;

} // namespace logicontrol::provider
