using System.Runtime.InteropServices;
using GameOffsets.Natives;
using GameOffsets.Objects.Components;
using GameOffsets.Objects.States.InGameState;
using PortalAccess;

var passed = 0;
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
var portal = new PortalIdentity(0x10000, 42, 0x20000, 0x30000, 0x40000);
const int offset = 0x51;
var address = portal.Targetable + offset;

Test("Zero changes to one and restores to zero", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    Check(ledger.Apply(memory, portal, offset, _ => true, out var changed) && changed);
    Check(memory.Bytes[address] == 1 && ledger.Pending == 1);
    Check(ledger.Restore(memory, _ => true) == 1 && memory.Bytes[address] == 0 && ledger.Pending == 0);
});
Test("Already open portal is never recorded or written", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 1;
    Check(ledger.Apply(memory, portal, offset, _ => true, out var changed) && !changed);
    Check(memory.Writes == 0 && ledger.Pending == 0);
});
Test("Unknown boolean value is rejected", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 3;
    Check(!ledger.Apply(memory, portal, offset, _ => true, out _) && memory.Writes == 0);
});
Test("Identity changing between read and write cancels mutation", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0; var calls = 0;
    Check(!ledger.Apply(memory, portal, offset, _ => ++calls == 1, out _) && memory.Writes == 0);
});
Test("Read failure never becomes a zero value to write", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger();
    Check(!ledger.Apply(memory, portal, offset, _ => true, out _) && memory.Writes == 0);
});
Test("Write failure is not reported as a successful change", () =>
{
    var memory = new FakeMemory { FailWrites = true }; var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    Check(!ledger.Apply(memory, portal, offset, _ => true, out var changed) && !changed);
    Check(ledger.Pending == 1 && memory.Bytes[address] == 0);
});
Test("Read-back mismatch is a failure and retains the undo record", () =>
{
    var memory = new FakeMemory { IgnoreWrites = true }; var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    Check(!ledger.Apply(memory, portal, offset, _ => true, out var changed) && !changed && ledger.Pending == 1);
});
Test("Undo does not overwrite a newer game value", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    ledger.Apply(memory, portal, offset, _ => true, out _); memory.Bytes[address] = 2;
    Check(ledger.Restore(memory, _ => true) == 0 && memory.Bytes[address] == 2 && memory.Writes == 1);
});
Test("Undo rejects recycled entities", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    ledger.Apply(memory, portal, offset, _ => true, out _);
    Check(ledger.Restore(memory, _ => false) == 0 && memory.Writes == 1);
});
Test("Area change forgets all old addresses without writing", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    ledger.Apply(memory, portal, offset, _ => true, out _); ledger.Clear();
    Check(ledger.Restore(memory, _ => true) == 0 && memory.Writes == 1);
});
Test("Missing entity pruning includes ID in its identity", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    ledger.Apply(memory, portal, offset, _ => true, out _);
    ledger.Retain(new HashSet<PortalIdentity> { portal with { Id = 43 } });
    Check(ledger.Pending == 0);
});
Test("Repeated recovery retains the original and does not grow records", () =>
{
    var memory = new FakeMemory(); var ledger = new PatchLedger(); memory.Bytes[address] = 0;
    ledger.Apply(memory, portal, offset, _ => true, out _); memory.Bytes[address] = 0;
    ledger.Apply(memory, portal, offset, _ => true, out _);
    Check(ledger.Pending == 1 && ledger.Restore(memory, _ => true) == 1 && memory.Bytes[address] == 0);
});

// Use only this test process's allocated memory. Never attach to the game for these tests.
var allocations = new List<IntPtr>();
IntPtr Allocate(int count)
{
    var p = Marshal.AllocHGlobal(count); allocations.Add(p); Marshal.Copy(new byte[count], 0, p, count); return p;
}
try
{
    var entity = Allocate(256); var details = Allocate(128); var target = Allocate(128);
    var gate = Allocate(128); var vector = Allocate(16); var dummyVtable = Allocate(8);
    var identity = new PortalIdentity(entity.ToInt64(), 123, details.ToInt64(), target.ToInt64(), gate.ToInt64());
    var entityData = new EntityOffsets { Id = 123, ItemBase = new ItemStruct {
        EntityDetailsPtr = details, ComponentListPtr = new StdVector { First = vector, Last = vector + 16, End = vector + 16 } } };
    void Reset()
    {
        Marshal.StructureToPtr(entityData, entity, false);
        Marshal.StructureToPtr(new ComponentHeader { EntityPtr = entity, StaticPtr = dummyVtable }, target, false);
        Marshal.StructureToPtr(new ComponentHeader { EntityPtr = entity, StaticPtr = dummyVtable }, gate, false);
        Marshal.WriteIntPtr(vector, target); Marshal.WriteIntPtr(vector, 8, gate);
        Marshal.WriteByte(target, PortalMemory.TargetOffset, 0); Marshal.WriteByte(target, PortalMemory.HighlightOffset, 0);
    }
    using var native = new PortalMemory();
    Reset();
    Test("Native read-only handle rejects writes", () =>
    {
        Check(native.Attach((uint)Environment.ProcessId, false));
        Check(native.Validate(identity));
        Check(!native.WriteByte(target.ToInt64() + PortalMemory.TargetOffset, 1));
    });
    Test("Native one-byte update and rollback preserve neighboring bytes", () =>
    {
        Check(native.Attach((uint)Environment.ProcessId, true));
        Marshal.WriteByte(target, PortalMemory.TargetOffset - 1, 0x7B);
        var ledger = new PatchLedger();
        Check(ledger.Apply(native, identity, PortalMemory.TargetOffset, native.Validate, out var changed) && changed);
        Check(Marshal.ReadByte(target, PortalMemory.TargetOffset) == 1);
        Check(Marshal.ReadByte(target, PortalMemory.TargetOffset - 1) == 0x7B);
        Check(Marshal.ReadByte(target, PortalMemory.HighlightOffset) == 0);
        Check(ledger.Restore(native, native.Validate) == 1);
    });
    Test("Native identity rejects a changed entity ID", () =>
    {
        Reset(); Check(!native.Validate(identity with { Id = 456 }));
    });
    Test("Native identity rejects a component owned by another entity", () =>
    {
        Reset(); Marshal.WriteIntPtr(target, 8, details); Check(!native.Validate(identity));
    });
    Test("Native identity rejects a removed component", () =>
    {
        Reset(); Marshal.WriteIntPtr(vector, 8, IntPtr.Zero); Check(!native.Validate(identity));
    });
    Test("Native identity rejects a corrupt component vector", () =>
    {
        Reset(); var invalid = entityData; invalid.ItemBase.ComponentListPtr.Last = vector + 9;
        Marshal.StructureToPtr(invalid, entity, false); Check(!native.Validate(identity));
    });
    Test("Native identity rejects out-of-range flag bytes", () =>
    {
        Reset(); Marshal.WriteByte(target, PortalMemory.HighlightOffset, 7); Check(!native.Validate(identity));
    });
    Test("Disposed handle cannot read or write", () =>
    {
        native.Dispose(); Check(!native.ReadByte(target.ToInt64(), out _)); Check(!native.WriteByte(target.ToInt64(), 1));
    });
}
finally { foreach (var p in allocations) Marshal.FreeHGlobal(p); }
Console.WriteLine($"Passed {passed} tests.");

sealed class FakeMemory : IByteMemory
{
    public readonly Dictionary<long, byte> Bytes = new();
    public int Writes;
    public bool FailWrites;
    public bool IgnoreWrites;
    public bool ReadByte(long address, out byte value) => this.Bytes.TryGetValue(address, out value);
    public bool WriteByte(long address, byte value)
    {
        this.Writes++;
        if (this.FailWrites) return false;
        if (!this.IgnoreWrites) this.Bytes[address] = value;
        return true;
    }
}
