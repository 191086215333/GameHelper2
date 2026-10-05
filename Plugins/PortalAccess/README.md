# PortalAccess

F12 → Maps & earnings → Portal interaction recovery offers an opt-in switch to restore targeting and highlighting on closed portal entities still resident in the current area. New configurations default to read-only; existing explicit preferences are preserved.

Recovery requires the verified 2026-10-05 client instruction profile. Four instruction-block hashes, the component vtable and the client's named diagnostic flags must all match. Only Targetable +0x69 and Highlightable +0x6A are writable. The inherited +0x51/+0x52 locations overlap a vector pointer and are never used for game writes. Unknown layouts remain read-only, even with the switch enabled. See [layout evidence](DIAGNOSTIC_LAYOUT.md).

Each write checks native loading, area pointer/hash, player identity and portal component ownership again. Only valid supported portal objects with a Portal component and satisfied quest/item conditions qualify. Each entity is repaired once per area, matching the reference behavior. Switching off attempts to undo this plugin's changes to the same current entities; area/session changes discard stale addresses without writing. Generic writable attachments remain restricted to own-process offline fixtures.

Discovery shares a 15 ms / 4,000-read budget, with at most 2,000 nodes per map and 128 retained identities. Recovery has a separate 40 ms deadline checked between validations/writes; an in-progress validation can finish after the deadline but cannot write afterwards. Incomplete samples are reported. Diagnostics in config/ update every 10 seconds and include the matched profile and modified-field count.

The profile and current closed-portal identity were checked read-only in a running client. Appearance and entry await user testing. Flag readback is not visual or server validation. Deleted entities or expired/rejected destinations cannot be recreated. No proprietary executable or authentication code is bundled. [Offline tests](../../Tests/README.md) never write to the game.
