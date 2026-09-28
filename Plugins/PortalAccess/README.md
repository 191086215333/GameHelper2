# PortalAccess (experimental)

Open F12 → Maps & earnings → Portal interaction recovery. The default is **observation only**;
the plugin uses a separate read-only handle and displays detected portals and validation results.
Enabling **Restore portal interaction** opens a plugin-owned writable handle and attempts to set
the existing Targetable component's selectable/highlightable bytes from 0 to 1. The framework's
normal memory handle remains read-only.

This does not recreate removed entities, change server-side map limits or prove that a server
will accept a re-entry request. Current-version in-game behavior has not been verified.

Targets must have an actual Portal component, not merely a matching substring in their path.
Before each write the plugin checks the current process/area session, entity ID and details,
component ownership, live component vector membership and boolean byte values. Addresses come
from the framework's existing named offsets. These checks cannot guarantee version compatibility
or eliminate races with the remote process; field offsets can change after game updates.

The default interval is 1,000 ms, with 1,500 ms settling time after an area change. Loading,
returning to the same area and process replacement invalidate old records. Turning the switch off,
disabling the plugin or normal application shutdown attempts to undo fields that still belong
to the same entity and still hold this plugin's written value. Crashes and forced termination
cannot guarantee cleanup.

Settings and optional diagnostic exports stay in the plugin's ignored `config/` directory.
Diagnostics describe the current portal sample, not successful map entries. No proprietary
binary, authentication mechanism or third-party executable is bundled.

[Offline tests](../../Tests/README.md) cover simulated failures and read/write/undo against only
the test process's own allocated memory. No test attaches to the game.
