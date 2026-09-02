# Protocol and architecture sources

All source references below were inspected at the pinned commits during the
initial architecture investigation.

| Project | Commit | License | Use |
| --- | --- | --- | --- |
| [DFGT Control](https://github.com/tears-mysthrala/dfgt-control) | `426d7007a1d40e4d2de5c5873959620f9066ec1c` | MIT | DirectInput provider, COM/OEM registration, IPC and broker baseline |
| [new-lg4ff](https://github.com/berarma/new-lg4ff) | `2092db19f7b40854e0427a1b2e39eda9f8d0c3cd` | Relevant `hid-lg4ff.c`: GPL-2.0-or-later | Identity tables, modes, commands, capabilities, range, autocenter, mixer behavior |
| [lg4ff_userspace](https://github.com/Kethen/lg4ff_userspace) | `d81ccb5d23716fa859b5f72d7e26d4ac8e6ba67d` | Treat as GPL-2.0-only unless clarified | Conceptual userspace separation only; no direct copying |
| [OpenG27](https://github.com/Jabelius/OpenG27) | `12c9421d2c9e3de05c3ff543ed7748f8d6162994` | MIT | C# transport/lifecycle/testing patterns |
| [WheelEmulator](https://github.com/Creator84/WheelEmulator) | `054558efb117d4f30f0fe2fac2e164779a854708` | MIT | Windows SetOutputReport and hotplug reference |
| [xinput-ffb-driver](https://github.com/mentalfoundry/xinput-ffb-driver) | `f79f0fa91ce5f56ea95ce2b64e6cdd9ace6f6429` | MIT | Additional DirectInput effect-driver and registry reference |

Microsoft references:

- [`IDirectInputEffectDriver`](https://learn.microsoft.com/en-us/windows/win32/api/dinputd/nn-dinputd-idirectinputeffectdriver)
- [`DownloadEffect`](https://learn.microsoft.com/en-us/windows/win32/api/dinputd/nf-dinputd-idirectinputeffectdriver-downloadeffect)
- [`DIFFDEVICEATTRIBUTES`](https://learn.microsoft.com/en-us/windows/win32/api/dinputd/ns-dinputd-diffdeviceattributes)
- [`DIEFFECTATTRIBUTES`](https://learn.microsoft.com/en-us/windows/win32/api/dinputd/ns-dinputd-dieffectattributes)
- [`HidD_SetOutputReport`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_setoutputreport)
- [HID report sending](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/sending-hid-reports)
- [`CM_Register_Notification`](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification)

Protocol facts should be expressed independently and covered by golden vectors.
When implementation is adapted closely from upstream code, add source-file
headers and `THIRD_PARTY_NOTICES.md` entries rather than relying on this index
alone.

Phase 2 provenance details:

- The managed DFGT constant/spring/damper/friction report scaling and packing
  preserve the imported DFGT Control behavior and shared golden vectors. That
  translation remains covered by the DFGT Control MIT notice.
- Effect timing, four-slot division, class gains, native friction, inertia-to-
  damper behavior, autocenter strategy, and output backpressure were expressed
  for this architecture from the pinned `new-lg4ff` behavior. Source headers
  identify this behavioral provenance; no Linux kernel structures or code are
  copied into the ABI or IPC.
- DirectInput structure/mask/waveform semantics are normative from Microsoft;
  upstream behavior is corrected where it conflicts with DirectInput.

Phase 3 provenance details:

- The DFGT/G27/G25/DFP revision matchers, preferred presentations, EXT_CMD1,
  EXT_CMD9, EXT_CMD16, DFP discrete 200°/900° range behavior, shared `F8 81`
  range behavior, classic autocenter, native-friction capability, and
  inertia-to-damper mapping were independently expressed from the pinned
  `new-lg4ff` source and locked by golden vectors.
- The active family-wide constant-force strategy uses the pinned source's
  direct `0x00` encoding and selected-slot byte placement. There is no `0x08`
  encoder or fallback branch; the former DFGT Control variable-force packet is
  retained only as documented Phase 1 provenance and hardware evidence.
- DFP runtime range validation and encoding deliberately expose only 200° and
  900°. No intermediate packet generator is retained because the Linux
  implementation couples such ranges to input-axis rescaling that this
  non-interposing userspace architecture cannot perform.
- Condition class gain follows the pinned behavior at the report boundary: it
  reduces saturation without changing coefficient slope. Active condition
  reports are refreshed when a gain setting changes.
- The resulting implementation is a family protocol with data-driven
  definitions. It does not copy Linux driver structures and does not introduce
  a model-specific Windows driver or runtime.
