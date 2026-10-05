// <copyright file="PortalRecoveryLayout.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
namespace PortalAccess
{
    using System;
    using System.Security.Cryptography;
    using GameOffsets.Objects.Components;

    /// <summary>An explicitly verified client layout, never a fallback to framework offsets.</summary>
    internal static class PortalRecoveryLayout
    {
        internal const int TargetOffset = 0x69;
        internal const int HighlightOffset = 0x6A;
        internal const long VtableRva = 0x33A6028;
        internal const string Profile = "poe2-2026-10-05-targetable-69-6a";

        // Full instruction blocks from the inspected image: highlight/outline update,
        // named diagnostic prints, target predicate, and vector destruction at +0x50.
        // No code is executed or patched. ASLR does not change these relative instructions.
        internal static bool MatchesImage(IRemoteMemory memory, long image, int size)
        {
            if (image < 0x10000 || size < VtableRva + 14 * 8) return false;
            return Match(0x1729590, 608, "125F2D4B70B33E7BD47C061B8024F6B8ABA2807ED0A76933E5CB927066766835") &&
                Match(0x172B470, 240, "4D2CB2DE011119979BE72CF5C651C297ED24D1CE6FAB476EF490B07596CA6CAB") &&
                Match(0x172B6B0, 80, "65F64DA3AF977255CE016B950A36B889C211023BE529E512D3742D6B258FDCFB") &&
                Match(0x1729C2E, 9, "31C5077B4D28B469A92D0476B0E02DF7EA04FE0ECAC80643D9900DB440890CB3");

            bool Match(int rva, int length, string digest) => rva <= size - length &&
                memory.ReadBytes(image + rva, length, out var code) &&
                Convert.ToHexString(SHA256.HashData(code)) == digest;
        }

        internal static bool ValidatePortal(IRemoteMemory memory, PortalIdentity identity, long image, int size)
        {
            if (!new PortalScanner(memory).TryReadCandidate(identity.Entity, identity.Id, "recovery-validation", out var candidate) ||
                candidate!.Identity != identity || identity.Portal == 0 || candidate.EntityState != 12 ||
                !PortalScanner.IsSupportedPortalPath(candidate.Path) ||
                !memory.Read<ComponentHeader>(identity.Portal, out var gate) || gate.EntityPtr.ToInt64() != identity.Entity ||
                !memory.Read<ComponentHeader>(identity.Targetable, out var target) || target.EntityPtr.ToInt64() != identity.Entity ||
                target.StaticPtr.ToInt64() != image + VtableRva ||
                !PortalFlagReader.TryRead(memory, identity, image, size, out var flags) ||
                flags!.TargetOffset != TargetOffset || flags.HiddenOffset != 0x73 || flags.QuestOffset != 0x6E || flags.ItemOffset != 0x6F ||
                flags.HiddenFromPlayer != 0 || flags.MeetsQuestState != 1 || flags.MeetsItemRequirements != 1 ||
                !memory.ReadByte(identity.Targetable + HighlightOffset, out var highlight) || highlight > 1) return false;
            return true;
        }
    }

    /// <summary>Restricts both repair and undo to the two verified boolean fields.</summary>
    internal sealed class PortalWriteGate : IByteMemory
    {
        private readonly PortalIdentity portal;
        private readonly IByteMemory memory;
        private readonly Func<bool> validate;
        internal PortalWriteGate(PortalIdentity portal, IByteMemory memory, Func<bool> validate)
        { this.portal = portal; this.memory = memory; this.validate = validate; }
        private bool Allowed(long address) => address == this.portal.Targetable + PortalRecoveryLayout.TargetOffset ||
            address == this.portal.Targetable + PortalRecoveryLayout.HighlightOffset;
        public bool ReadByte(long address, out byte value)
        { value = default; return this.Allowed(address) && this.memory.ReadByte(address, out value); }
        public bool WriteByte(long address, byte value) => value <= 1 && this.Allowed(address) && this.validate() &&
            this.memory.ReadByte(address, out var current) && current <= 1 && this.validate() && this.memory.WriteByte(address, value);
    }
}
