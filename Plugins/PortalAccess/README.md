# PortalAccess (recovery paused)

After an area-entry access-violation crash on 2026-10-05, this build suspends interaction
recovery. F12 → Maps & earnings → Portal interaction recovery now offers **read-only
diagnostics**. Old `RestoreInteraction: true` settings are reset and the switch is disabled.
The plugin never opens a writable game handle and performs no undo writes on shutdown.
The low-level writable path is restricted to the current process for offline tests only.

The crash dump contains an invalid `0x10100` pointer, consistent with the previous two
one-byte writes. Read-only inspection of the same game image identifies the Targetable
destructor in that crash stack and its vector at 0x50: the inherited 0x51/0x52 fields fall
inside that vector's pointer. This strongly identifies a layout error; the dump omits
the actual heap object. Do not use the framework's inherited field offsets for writes.

Diagnostics identify four boolean offsets through the current Targetable debug method's
UTF-16 labels: Targetable, Hidden from Player, Meets Quest State, Meets Item Requirements.
The method and labels must lie within the attached game's executable image, labels must
be distinct, values must be boolean, and the entity/component identity must match before
and after reading. Unknown methods produce no flag values. This validates read-only field
interpretation, not highlighting, model rendering or server re-entry. Recovery stays paused.

Discovery reads native awake/sleeping maps and checks retained portal identities. One sample
shares a 15 ms / 4,000-read budget, with at most 2,000 nodes per map and 128 retained identities.
Checks stop when loading/process state changes. A limit or failed read produces an incomplete
sample, never a fabricated zero. Large maps may have portals missing from diagnostics.

Settings and diagnostic exports live in `config/`; active samples refresh the export every
10 seconds. No proprietary executable or authentication code is bundled. Offline tests use
fake memory and their own allocations, never the game; see [tests](../../Tests/README.md).
