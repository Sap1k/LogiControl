// SPDX-License-Identifier: GPL-3.0-or-later

namespace LogiControl.Broker;

using LogiControl.Protocol;

public interface IRuntimeMixer
{
    bool HasActiveEffects { get; }

    RuntimeSettings RuntimeSettings { get; }

    MixerSnapshot Render();

    bool TryDequeueConditionChange(out ConditionSlotChange change);

    bool TryConsumeStopAllBarrier();

    void StopAll();
}
