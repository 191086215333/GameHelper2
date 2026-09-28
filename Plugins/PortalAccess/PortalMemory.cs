// <copyright file="PortalMemory.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
namespace PortalAccess
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.States.InGameState;
    using Microsoft.Win32.SafeHandles;

    /// <summary>A plugin-owned handle; the framework and other plugins remain read-only.</summary>
    internal sealed class PortalMemory : IByteMemory, IDisposable
    {
        private SafeProcessHandle? handle;
        private bool writable;
        public string Session { get; private set; } = string.Empty;
        public int LastError { get; private set; }
        public static readonly int TargetOffset = Marshal.OffsetOf<TargetableOffsets>(nameof(TargetableOffsets.IsTargetable)).ToInt32();
        public static readonly int HighlightOffset = Marshal.OffsetOf<TargetableOffsets>(nameof(TargetableOffsets.IsHighlightable)).ToInt32();

        public bool Attach(uint pid, bool write)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)pid));
                var session = $"{pid}:{process.StartTime.ToUniversalTime().Ticks}";
                if (this.handle is { IsInvalid: false, IsClosed: false } && this.Session == session && this.writable == write && this.IsAlive())
                    return true;
                this.Dispose();
                // QUERY_LIMITED_INFORMATION | VM_READ; add only the two necessary write rights.
                this.handle = OpenProcess(0x1000u | 0x10u | (write ? 0x28u : 0), false, pid);
                if (this.handle.IsInvalid)
                {
                    this.LastError = Marshal.GetLastWin32Error();
                    return false;
                }
                this.Session = session;
                this.writable = write;
                this.LastError = 0;
                return this.IsAlive();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
            {
                this.Dispose();
                this.LastError = ex is System.ComponentModel.Win32Exception w ? w.NativeErrorCode : -1;
                return false;
            }
        }

        public bool IsAlive() => this.handle is { IsInvalid: false, IsClosed: false } &&
            GetExitCodeProcess(this.handle, out var exit) && exit == 259;

        public static bool AddressValid(long address) => address >= 0x10000 && address < 0x7FFFFFFF0000;

        public bool Read<T>(long address, out T value) where T : unmanaged
        {
            value = default;
            var buffer = new byte[Marshal.SizeOf<T>()];
            if (!AddressValid(address) || this.handle is null || !this.IsAlive() ||
                !ReadProcessMemory(this.handle, (IntPtr)address, buffer, (nuint)buffer.Length, out var count) || count != (nuint)buffer.Length)
                return false;
            value = MemoryMarshal.Read<T>(buffer);
            return true;
        }

        public bool ReadByte(long address, out byte value) => this.Read(address, out value);

        public bool WriteByte(long address, byte value)
        {
            if (!this.writable || !AddressValid(address) || this.handle is null || !this.IsAlive()) return false;
            var success = WriteProcessMemory(this.handle, (IntPtr)address, new[] { value }, 1, out var count) && count == 1;
            if (!success) this.LastError = Marshal.GetLastWin32Error();
            return success;
        }

        public bool Validate(PortalIdentity portal)
        {
            if (!AddressValid(portal.Entity) || !AddressValid(portal.Portal) || !AddressValid(portal.Targetable) ||
                !this.Read<EntityOffsets>(portal.Entity, out var entity) || entity.Id != portal.Id ||
                entity.ItemBase.EntityDetailsPtr.ToInt64() != portal.Details || !AddressValid(portal.Details) ||
                !this.Read<ComponentHeader>(portal.Targetable, out var target) || target.EntityPtr.ToInt64() != portal.Entity ||
                !this.Read<ComponentHeader>(portal.Portal, out var gate) || gate.EntityPtr.ToInt64() != portal.Entity ||
                !AddressValid(target.StaticPtr.ToInt64()) || !AddressValid(gate.StaticPtr.ToInt64())) return false;

            // Verify that the live entity's component vector still contains both components.
            var first = entity.ItemBase.ComponentListPtr.First.ToInt64();
            var last = entity.ItemBase.ComponentListPtr.Last.ToInt64();
            var length = last - first;
            if (!AddressValid(first) || length < 16 || length > 50 * 8 || length % 8 != 0) return false;
            var hasTarget = false;
            var hasPortal = false;
            for (var p = first; p < last; p += 8)
            {
                if (!this.Read<long>(p, out var component)) return false;
                hasTarget |= component == portal.Targetable;
                hasPortal |= component == portal.Portal;
            }
            return hasTarget && hasPortal && TargetOffset is >= 0 and < 1024 && HighlightOffset is >= 0 and < 1024 &&
                this.ReadByte(portal.Targetable + TargetOffset, out var t) && t <= 1 &&
                this.ReadByte(portal.Targetable + HighlightOffset, out var h) && h <= 1;
        }

        public void Dispose()
        {
            this.handle?.Dispose();
            this.handle = null;
            this.Session = string.Empty;
            this.writable = false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, [Out] byte[] buffer, nuint length, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nuint length, out nuint written);
    }
}
