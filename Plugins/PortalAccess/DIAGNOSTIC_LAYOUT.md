# Read-only Targetable layout investigation, 2026-10-05

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

This does not establish highlighting offsets, model restoration, portal destination validity,
click handling or server re-entry. The legacy framework TargetableOffsets is not used for
these live diagnostics. Patch-ledger fixtures retain legacy locations only in test-owned
allocations, and the fixture validator refuses foreign processes. Recovery remains paused.
