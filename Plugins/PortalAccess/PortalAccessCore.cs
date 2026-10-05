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
    using GameOffsets.Objects.States.InGameState;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class PortalAccessCore : PCore<PortalAccessSettings>
    {
        private readonly PortalMemory memory = new();
        private readonly List<Observation> observations = new();
        private string areaKey = string.Empty;
        private string processSession = string.Empty;
        private long stableAt;
        private long nextSample;
        private string status = "waiting";
        private readonly Dictionary<PortalIdentity, PortalCandidate> known = new();
        private int awakeVisited;
        private int sleepingVisited;
        private bool scanComplete = true;
        private long lastDiagnosticAt;
        private string SettingsPath => Path.Combine(this.DllDirectory, "config", "settings.json");
        private sealed record Observation(PortalIdentity Identity, string Path, bool Valid, PortalFlagReader.Flags? VerifiedFlags,
            string Source, byte EntityState, string[] Components, string TargetableBytes);

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
            // Ignore old configs that enabled writes. Observation is the only supported
            // mode until the current client layout has been independently verified.
            if (this.Settings.RestoreInteraction)
            {
                this.Settings.RestoreInteraction = false;
                this.SaveSettings();
            }
            this.ResetArea();
        }

        public override void OnDisable()
        {
            try { this.SaveSettings(); }
            finally { this.memory.Dispose(); this.ResetArea(); }
        }

        public override void SaveSettings()
        {
            this.Settings.RestoreInteraction = false;
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            ImGui.TextWrapped(this.PluginText.T("settings.explanation", "Inspect portal entities still present in the client. Interaction recovery is currently unavailable."));
            ImGui.TextWrapped(this.PluginText.T("settings.experimental", "Recovery is paused after an area-entry crash. Read-only flag checks do not verify portal restoration."));
            this.Settings.RestoreInteraction = false;
            ImGui.BeginDisabled();
            ImGui.Checkbox(this.PluginText.Label("settings.restore", "Restore portal interaction", "PortalRestore"), ref this.Settings.RestoreInteraction);
            ImGui.EndDisabled();
            ImGui.TextWrapped(this.PluginText.T("settings.observe", "This build only reads portal data. It does not modify or restore game memory, even with an older enabled configuration."));
            var refreshChanged = ImGui.SliderInt(this.PluginText.Label("settings.interval", "Refresh interval (ms)", "PortalInterval"), ref this.Settings.RefreshIntervalMs, 500, 10000);
            var delayChanged = ImGui.SliderInt(this.PluginText.Label("settings.delay", "Wait after area changes (ms)", "PortalDelay"), ref this.Settings.AreaDelayMs, 500, 10000);
            if (refreshChanged || delayChanged) this.SaveSettings();
            ImGui.Separator();
            ImGui.TextWrapped(this.PluginText.F("status.line", "Status: {0}", this.StatusText()));
            ImGui.TextWrapped(this.PluginText.F("status.counts", "Portals: {0} | Modified fields: {1} | Failed attempts: {2} | Last undo: {3}", this.observations.Count, 0, 0, 0));
            if (this.status == "access")
                ImGui.TextWrapped(this.PluginText.F("status.error", "Process access failed (code {0}). Check that both applications run with matching privileges.", this.memory.LastError));
            if (ImGui.Button(this.PluginText.Label("settings.export", "Export portal diagnostics", "PortalExport")))
                this.SaveDiagnostics();
            ImGui.TextWrapped(this.PluginText.T("settings.export_path", "Saved to Plugins/PortalAccess/config/portal-diagnostics.json every 10 seconds. Flag offsets are identified from the client's debug method; unrecognized layouts show no values. These flags cannot prove that a portal is usable."));
            ImGui.TextWrapped(this.PluginText.F("status.scan", "Scanned entities: awake {0}, sleeping {1}; retained portals {2}", this.awakeVisited, this.sleepingVisited, this.known.Count));
            if (!this.scanComplete) ImGui.TextWrapped(this.PluginText.T("status.scan_incomplete", "The sample ended at its time/read limit or the area changed. Some portals may be missing."));
            if (ImGui.BeginTable("PortalStates", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new System.Numerics.Vector2(0, 180)))
            {
                ImGui.TableSetupColumn(this.PluginText.T("table.portal", "Portal ID"));
                ImGui.TableSetupColumn(this.PluginText.T("table.check", "Identity check"));
                ImGui.TableSetupColumn(this.PluginText.T("table.target", "Targetable flag"));
                ImGui.TableSetupColumn(this.PluginText.T("table.hidden", "Hidden from player"));
                ImGui.TableSetupColumn(this.PluginText.T("table.quest", "Quest requirements"));
                ImGui.TableSetupColumn(this.PluginText.T("table.item", "Item requirements"));
                ImGui.TableHeadersRow();
                foreach (var item in this.observations)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Identity.Id.ToString());
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Path);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(this.PluginText.T(item.Valid ? "table.valid" : "table.invalid", item.Valid ? "Passed" : "Skipped"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.VerifiedFlags?.Targetable.ToString() ?? "—");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.VerifiedFlags?.HiddenFromPlayer.ToString() ?? "—");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.VerifiedFlags?.MeetsQuestState.ToString() ?? "—");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(item.VerifiedFlags?.MeetsItemRequirements.ToString() ?? "—");
                }
                ImGui.EndTable();
            }
        }

        private string StatusText() => this.status switch
        {
            "settling" => this.PluginText.T("status.settling", "Waiting for the area to stabilize"),
            "access" => this.PluginText.T("status.access", "Cannot open the game process"),
            "observe" => this.PluginText.T("status.observe", "Read-only diagnostics; interaction recovery is paused"),
            "empty" => this.PluginText.T("status.empty", "No portal entities found nearby"),
            "failed" => this.PluginText.T("status.failed", "The read-only sample is incomplete or some entities failed identity checks"),
            "error" => this.PluginText.T("status.exception", "Sampling stopped after an error; see Error.log"),
            _ => this.PluginText.T("status.waiting", "Waiting for the game or an area to finish loading"),
        };

        public override void DrawUI()
        {
            try { this.Sample(); }
            catch (Exception ex)
            {
                this.Settings.RestoreInteraction = false;
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
                this.memory.Dispose();
                this.areaKey = key;
                this.stableAt = now + this.Settings.AreaDelayMs;
            }
            if (now < this.stableAt) { this.status = "settling"; return; }
            if (now < this.nextSample) return;
            this.nextSample = now + this.Settings.RefreshIntervalMs;
            if (!this.memory.Attach(Core.Process.Pid, false))
            {
                this.status = "access"; return;
            }
            if (this.processSession != this.memory.Session)
            {
                this.known.Clear();
                this.processSession = this.memory.Session;
            }
            this.Collect();
            var errors = this.observations.Count(x => !x.Valid) + (this.scanComplete ? 0 : 1);
            this.status = errors > 0 ? "failed" : this.observations.Count == 0 ? "empty" : "observe";
            if (now - this.lastDiagnosticAt >= 10000)
            {
                this.SaveDiagnostics();
                this.lastDiagnosticAt = now;
            }
        }

        private void Collect()
        {
            this.observations.Clear();
            if (this.areaKey != this.GetAreaKey()) return;
            var budget = new ScanBudget(this.memory, () => this.areaKey == this.GetAreaKey() && this.memory.IsAlive());
            var instance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!budget.Read<AreaInstanceOffsets>(instance.Address.ToInt64(), out var data) ||
                data.CurrentAreaHash.ToString("X") != instance.AreaHash || data.PlayerInfo.LocalPlayerPtr != instance.Player.Address)
            { this.scanComplete = false; return; }
            var scanner = new PortalScanner(budget, () => budget.CanContinue);
            var awake = scanner.Scan(data.Entities.AwakeEntities, "awake-native");
            var sleeping = scanner.Scan(data.Entities.SleepingEntities, "sleeping-native");
            this.awakeVisited = awake.Visited;
            this.sleepingVisited = sleeping.Visited;
            this.scanComplete = awake.Complete && sleeping.Complete;
            var candidates = awake.Portals.Concat(sleeping.Portals).GroupBy(x => x.Identity).Select(x => x.First()).ToList();
            var found = candidates.Select(x => x.Identity).ToHashSet();
            foreach (var identity in this.known.Keys.ToArray())
            {
                if (!budget.CanContinue) break;
                if (found.Contains(identity)) continue;
                if (scanner.TryReadCandidate(identity.Entity, identity.Id, "retained", out var candidate) && candidate!.Identity == identity)
                    candidates.Add(candidate);
                else this.known.Remove(identity);
            }
            foreach (var candidate in candidates)
            {
                if (!budget.CanContinue) break;
                var identity = candidate.Identity;
                var valid = budget.Read<GameOffsets.Objects.Components.ComponentHeader>(identity.Targetable, out var header) && header.EntityPtr.ToInt64() == identity.Entity;
                if (valid && this.known.Count < 128) this.known[identity] = candidate;
                PortalFlagReader.Flags? flags = null;
                if (valid) PortalFlagReader.TryRead(budget, identity, this.memory.ImageStart, this.memory.ImageSize, out flags);
                var raw = budget.ReadBytes(identity.Targetable, 0x80, out var bytes) ? Convert.ToHexString(bytes) : string.Empty;
                this.observations.Add(new(identity, candidate.Path, valid, flags, candidate.Source, candidate.EntityState, candidate.Components, raw));
            }
            this.scanComplete &= budget.CanContinue;
            if (this.areaKey != this.GetAreaKey() || !this.memory.IsAlive())
            {
                this.observations.Clear();
                this.known.Clear();
                this.scanComplete = false;
            }
        }

        private void SaveDiagnostics()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
                File.WriteAllText(Path.Combine(this.DllDirectory, "config", "portal-diagnostics.json"),
                    JsonConvert.SerializeObject(new { CapturedUtc = DateTime.UtcNow, Status = this.status,
                        Area = this.areaKey, this.Settings.RestoreInteraction,
                        Mode = "read-only-crash-quarantine", WritesBlocked = true,
                        FlagOffsetsVerified = this.observations.Count > 0 && this.observations.All(x => x.VerifiedFlags != null),
                        FlagVerification = "client-debug-labels-read-only", RecoveryVerified = false,
                        ModifiedFields = 0, FailedAttempts = 0, LastUndo = 0,
                        Collector = "native-bounded-read-only-v3", AwakeVisited = this.awakeVisited,
                        SleepingVisited = this.sleepingVisited, ScanComplete = this.scanComplete,
                        Portals = this.observations }, Formatting.Indented));
            }
            catch (IOException ex) { Console.WriteLine($"[PortalAccess] Diagnostics: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Console.WriteLine($"[PortalAccess] Diagnostics: {ex.Message}"); }
        }

        private void ResetArea()
        {
            this.areaKey = string.Empty;
            this.known.Clear();
            this.observations.Clear();
            this.awakeVisited = 0;
            this.sleepingVisited = 0;
            this.scanComplete = true;
            this.lastDiagnosticAt = 0;
            this.nextSample = 0;
        }
    }
}
