# Targetable layout and guarded recovery, 2026-10-05

The inspected PoE2 image has SHA256
`9d9716f155fd87b206a458feeb04708230de1dd466f8681a156947d971841a93`.
An entity found through native awake/sleeping maps has a Targetable component owned by
that entity. Its vtable identifies these methods in the executable:

- Deleting destructor: RVA `0x1729880`, calls destructor `0x17298C0`.
- Destructor: at `0x1729C2E`, takes `this+0x50`; at `0x1729C32`, calls container cleanup
  `0x140D80`, leaving return address `0x1729C37`.
- Debug description method (vtable slot 13): RVA `0x172B470`.

The captured access-violation stack includes `0x1729894` and `0x1729C37`. The faulting
instruction at `0x140DA1` dereferences `RAX+8`, with `RAX=0x10100`. The previous writes
set bytes at 0x51 and 0x52 to 1; these addresses are inside the pointer at 0x50, not flags.
This strongly links the layout error to Targetable destruction. The dump did not capture
the corresponding heap object, so its contents cannot be directly checked retrospectively.

The client's own debug method prints these UTF-16 labels from `.rdata` and then loads
one byte from the indicated component offset:

| Label | Label RVA | Field offset |
| --- | --- | --- |
| Targetable | `0x33A86A8` | `0x69` |
| Hidden from Player | `0x33A8620` | `0x73` |
| Meets Quest State | `0x33A85F8` | `0x6E` |
| Meets Item Requirements | `0x33A8650` | `0x6F` |

PortalFlagReader recovers those four offsets from the live debug method rather than
assuming this image's RVAs. It reads the method and labels only within the main module,
rejects unknown prints, missing/duplicate labels, overlapping offsets, non-boolean values
and changed identities. It executes no game code and performs no writes. Two distinct
portal entities in the same game session were sampled with Targetable values 0 and 1,
respectively. Their differing identities prevent interpreting this as restoration.

The highlight/outline update method at RVA 0x1729590 tests byte this+0x6A at 0x172959A and returns if zero. Its following code selects render colors and changes the render outline bit. This identifies the highlight gate independently of the inherited framework layout. The reference sets targeting and highlighting to 1 once per entity/area; it does not recreate models or change server-side entry counts.

PortalRecoveryLayout matches SHA256 hashes of the 608-byte highlight method, 240-byte diagnostic method, 80-byte target predicate (0x172B6B0), and 9-byte vector-cleanup call site. It also requires Targetable vtable RVA 0x33A6028, matching named flag offsets, a valid supported portal with an owned Portal component, boolean values and satisfied conditions. PortalWriteGate allows only 0x69/0x6A. PortalAreaFence rechecks native loading, area and player identities before each write; the portal is validated again. Old-area addresses are discarded without undo. Unknown profiles cannot obtain a recovery writer.

Read-only live verification matched all four code hashes and the current closed portal's full identity. Offline tests cover field gates, area changes, recycled identities, unknown profiles and undo. Appearance, click handling and server re-entry await user testing; matching code or reading back bytes alone do not establish those outcomes. No game-memory writes were performed during this validation. Legacy offsets remain solely in test-owned allocations; arbitrary writable attachments reject foreign processes.
