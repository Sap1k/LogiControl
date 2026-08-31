# Third-party notices

## DFGT Control

Selected source files were adapted from DFGT Control commit
`426d7007a1d40e4d2de5c5873959620f9066ec1c`:

- DirectInput effect provider and COM implementation;
- userspace HID broker;
- legacy IPC and Logitech report encoders;
- protocol, COM, broker-range, and control smoke tests.

DFGT Control is Copyright (c) 2025 mysthrala and contributors and is licensed
under the MIT License. Its license is reproduced in
`third_party/dfgt-control/LICENSE` and the imported files retain MIT SPDX and
source annotations.

The DFGT Control native agent, UI, profiles, device discovery, and driver-stack
migration scripts were not imported.

The Phase 2 managed DFGT constant and condition report encoders are a translated
expression of the imported DFGT Control scaling and bit-packing behavior. They
retain the same MIT provenance through this notice and are checked against the
shared captured vectors.

The implementation also reuses or derives behavior from the projects
listed in `docs/protocol-sources.md`. When source is imported, this file must be
updated in the same change with the exact upstream project, commit, files,
copyright notice, license, and nature of the reuse.

In particular, planned Phase 1 reuse of DFGT Control is MIT-licensed and must
retain its MIT notice. Protocol behavior learned from `new-lg4ff` must retain
appropriate authorship and GPL-2.0-or-later provenance where the implementation
is derivative.

The Phase 2 engine and runtime independently express behavior studied from
`new-lg4ff/hid-lg4ff.c` at commit
`2092db19f7b40854e0427a1b2e39eda9f8d0c3cd`, including the software/condition
slot division, class gains, native friction, inertia mapping, timing, and
backpressure. The repository is GPL-3.0-or-later; relevant source headers and
`docs/protocol-sources.md` retain the pinned GPL-2.0-or-later provenance.
