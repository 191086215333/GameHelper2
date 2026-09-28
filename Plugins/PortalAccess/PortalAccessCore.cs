// <copyright file="PortalAccessCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
namespace PortalAccess
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameOffsets.Objects.States.InGameState;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class PortalAccessCore : PCore<PortalAccessSettings>
    {
        private readonly PortalMemory memory = new();
        private readonly PatchLedger ledger = new();
        private readonly List<Observation> observations = new();
        private HashSet<PortalIdentity> live = new();
        private string areaKey = string.Empty;
        private string processSession = string.Empty;
        private long stableAt;
        private long nextSample;
        private int modified;
        private int restored;
        private int failed;
        private string status = "waiting";
        private string SettingsPath => Path.Combine(this.DllDirectory, "config", "settings.json");
        private sealed record Observation(PortalIdentity Identity, string Path, bool Valid, int Targetable, int Highlightable);

        public override void OnEnable(bool isGameOpened)
        {
            try
            {
                if (File.Exists(this.SettingsPath))
                    this.Settings = JsonConvert.DeserializeObject<PortalAccessSettings>(File.ReadAllText(this.SettingsPath)) ?? new();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PortalAccess] Settings: {ex.Message}");
                this.Settings = new();
            }
            this.Settings.RefreshIntervalMs = Math.Clamp(this.Settings.RefreshIntervalMs, 500, 10000);
            this.Settings.AreaDelayMs = Math.Clamp(this.Settings.AreaDelayMs, 500, 10000);
            this.ResetArea();
        }

        public override void OnDisable()
        {
            try { this.Restore(); this.SaveSettings(); }
            finally { this.memory.Dispose(); this.ResetArea(); }
        }

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            ImGui.TextWrapped(this.PluginText.T("settings.explanation", "Restores selection and highlighting for portal entities still present in the client. Server entry restrictions still apply."));
            ImGui.TextWrapped(this.PluginText.T("settings.experimental", "Experimental: uses client memory writes. Current game-version behavior requires in-game verification."));
            if (ImGui.Checkbox(this.PluginText.Label("settings.restore", "Restore portal interaction", "PortalRestore"), ref this.Settings.RestoreInteraction))
            {
                if (!this.Settings.RestoreInteraction) this.Restore();
                this.nextSample = 0;
                this.SaveSettings();
            }
            ImGui.TextWrapped(this.PluginText.T("settings.observe", "With this switch off, only inspect portals; no new modifications are made. Switching it off attempts to undo this plugin's changes."));
            var refreshChanged = ImGui.SliderInt(this.PluginText.Label("settings.interval", "Refresh interval (ms)", "PortalInterval"), ref this.Settings.RefreshIntervalMs, 500, 10000);
            var delayChanged = ImGui.SliderInt(this.PluginText.Label("settings.delay", "Wait after area changes (ms)", "PortalDelay"), ref this.Settings.AreaDelayMs, 500, 10000);
            if (refreshChanged || delayChanged) this.SaveSettings();
            ImGui.Separator();
            ImGui.TextWrapped(this.PluginText.F("status.line", "Status: {0}", this.StatusText()));
            ImGui.TextWrapped(this.PluginText.F("status.counts", "Portals: {0} | Modified fields: {1} | Failed attempts: {2} | Last undo: {3}", this.observations.Count, this.modified, this.failed, this.restored));
            if (this.status == "access")
                ImGui.TextWrapped(this.PluginText.F("status.error", "Process access failed (code {0}). Check that both applications run with matching privileges.", this.memory.LastError));
            if (ImGui.Button(this.PluginText.Label("settings.export", "Export portal diagnostics", "PortalExport")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
                File.WriteAllText(Path.Combine(this.DllDirectory, "config", "portal-diagnostics.json"),
                    JsonConvert.SerializeObject(new { CapturedUtc = DateTime.UtcNow, Status = this.status,
                        Area = this.areaKey, this.Settings.RestoreInteraction,
                        PortalMemory.TargetOffset, PortalMemory.HighlightOffset,
                        ModifiedFields = this.modified, FailedAttempts = this.failed, LastUndo = this.restored,
                        Portals = this.observations }, Formatting.Indented));
            }
            ImGui.TextWrapped(this.PluginText.T("settings.export_path", "Saved to Plugins/PortalAccess/config/portal-diagnostics.json. Counts report field writes, not successful map entries."));
            if (ImGui.BeginTable("PortalStates", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new System.Numerics.Vector2(0, 180)))
            {
                ImGui.TableSetupColumn(this.PluginText.T("table.portal", "Portal ID"));
                ImGui.TableSetupColumn(this.PluginText.T("table.check", "Validation"));
                ImGui.TableSetupColumn(this.PluginText.T("table.target", "Selectable"));
                ImGui.TableSetupColumn(this.PluginText.T("table.highlight", "Highlightable"));
                ImGui.TableHeadersRow();
                foreach (var item in this.observations)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Identity.Id.ToString());
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Path);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(this.PluginText.T(item.Valid ? "table.valid" : "table.invalid", item.Valid ? "Passed" : "Skipped"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Targetable < 0 ? "—" : item.Targetable.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Highlightable < 0 ? "—" : item.Highlightable.ToString());
                }
                ImGui.EndTable();
            }
        }

        private string StatusText() => this.status switch
        {
            "settling" => this.PluginText.T("status.settling", "Waiting for the area to stabilize"),
            "access" => this.PluginText.T("status.access", "Cannot open the game process"),
            "observe" => this.PluginText.T("status.observe", "Inspecting only; interaction restoration is off"),
            "empty" => this.PluginText.T("status.empty", "No portal entities found nearby"),
            "active" => this.PluginText.T("status.active", "Restoring portal interaction"),
            "failed" => this.PluginText.T("status.failed", "Some portals could not be validated or modified; see diagnostics"),
            "error" => this.PluginText.T("status.exception", "Sampling stopped after an error; see Error.log"),
            _ => this.PluginText.T("status.waiting", "Waiting for the game or an area to finish loading"),
        };

        public override void DrawUI()
        {
            try { this.Sample(); }
            catch (Exception ex)
            {
                // Stop writing on an unexpected failure. The next enable is an explicit retry.
                this.Settings.RestoreInteraction = false;
                try { this.Restore(); } catch { this.ledger.Clear(); }
                this.memory.Dispose();
                this.ResetArea();
                this.status = "error";
                this.nextSample = Environment.TickCount64 + 5000;
                Console.WriteLine($"[PortalAccess] Sampling: {ex}");
            }
        }

        private string GetAreaKey()
        {
            if (Core.Process.Pid == 0 || Core.States.AreaLoading.Address == IntPtr.Zero || Core.States.AreaLoading.IsLoading ||
                Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState)) return string.Empty;
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area.Address == IntPtr.Zero || area.Player.Address == IntPtr.Zero || !area.Player.IsValid ||
                string.IsNullOrEmpty(area.AreaHash) || area.AreaHash == "0") return string.Empty;
            return $"{Core.Process.Pid}:{Core.States.AreaLoading.AreaRevision}:{area.Address.ToInt64():X}:{area.AreaHash}:{area.Player.Id}";
        }

        private void Sample()
        {
            var key = this.GetAreaKey();
            var now = Environment.TickCount64;
            if (key.Length == 0)
            {
                this.ResetArea();
                this.memory.Dispose();
                this.status = "waiting";
                return;
            }
            if (key != this.areaKey)
            {
                this.ResetArea();
                this.areaKey = key;
                this.stableAt = now + this.Settings.AreaDelayMs;
            }
            if (now < this.stableAt) { this.status = "settling"; return; }
            if (now < this.nextSample) return;
            this.nextSample = now + this.Settings.RefreshIntervalMs;
            if (!this.memory.Attach(Core.Process.Pid, this.Settings.RestoreInteraction))
            {
                this.ledger.Clear(); this.status = "access"; return;
            }
            if (this.processSession != this.memory.Session)
            {
                this.ledger.Clear();
                this.processSession = this.memory.Session;
            }
            this.Collect();
            this.ledger.Retain(this.live);
            var errors = this.observations.Count(x => !x.Valid);
            if (this.Settings.RestoreInteraction)
            {
                foreach (var item in this.observations.Where(x => x.Valid))
                {
                    foreach (var offset in new[] { PortalMemory.TargetOffset, PortalMemory.HighlightOffset })
                    {
                        if (!this.ledger.Apply(this.memory, item.Identity, offset, this.CanTouch, out var changed))
                        { errors++; this.failed++; }
                        if (changed) this.modified++;
                    }
                }
                // Show the read-back state rather than the pre-write sample.
                this.Collect();
            }
            this.status = errors > 0 ? "failed" : this.observations.Count == 0 ? "empty" :
                this.Settings.RestoreInteraction ? "active" : "observe";
        }

        private void Collect()
        {
            this.observations.Clear();
            this.live = new();
            if (this.areaKey != this.GetAreaKey()) return;
            foreach (var entity in Core.States.InGameStateObject.CurrentAreaInstance.AwakeEntities.Values.ToArray())
            {
                // Include still-resident non-valid entities, but never match merely by path text.
                if (!entity.TryGetComponent<Portal>(out var portal, false) || !entity.TryGetComponent<Targetable>(out var target, false) ||
                    !this.memory.Read<EntityOffsets>(entity.Address.ToInt64(), out var data)) continue;
                var identity = new PortalIdentity(entity.Address.ToInt64(), entity.Id,
                    data.ItemBase.EntityDetailsPtr.ToInt64(), target.Address.ToInt64(), portal.Address.ToInt64());
                var valid = this.memory.Validate(identity);
                if (valid) this.live.Add(identity);
                var t = this.memory.ReadByte(identity.Targetable + PortalMemory.TargetOffset, out var tv) ? tv : -1;
                var h = this.memory.ReadByte(identity.Targetable + PortalMemory.HighlightOffset, out var hv) ? hv : -1;
                this.observations.Add(new(identity, entity.Path, valid, t, h));
            }
        }

        private bool CanTouch(PortalIdentity portal) => this.areaKey.Length > 0 &&
            this.areaKey == this.GetAreaKey() && this.processSession == this.memory.Session &&
            this.live.Contains(portal) && this.memory.Validate(portal);

        private void Restore()
        {
            this.restored = 0;
            if (this.ledger.Pending == 0) return;
            if (this.areaKey.Length > 0 && this.areaKey == this.GetAreaKey())
            {
                this.Collect();
                this.restored = this.ledger.Restore(this.memory, this.CanTouch);
            }
            else this.ledger.Clear();
        }

        private void ResetArea()
        {
            this.areaKey = string.Empty;
            this.ledger.Clear();
            this.live.Clear();
            this.observations.Clear();
            this.modified = 0;
            this.failed = 0;
            this.nextSample = 0;
        }
    }
}
