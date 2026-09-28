using Cvolo.Analysis.Layout;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Tests;

/// <summary>
/// The single authoritative layout service: one set of rules behind <c>sizeof</c>,
/// <c>alignof</c>, <c>offsetof</c>, LLVM emission and the inspection tooling consumes. These
/// tests pin the numbers themselves, and pin the critical invariant that the inspection and the
/// compact layout never disagree.
/// </summary>
public sealed class TypeLayoutServiceTests
{
	private static TypeLayoutService Service(int pointerBytes = 8, string target = "x86_64-pc-windows-msvc")
		=> new(pointerBytes, target);

	private static StructTypeSymbol Struct(string name, params (string Name, TypeSymbol Type)[] fields)
		=> new(name, [.. fields.Select(field => new StructFieldSymbol(field.Name, field.Type))]);

	[Fact]
	public void Primitive_Layout_Matches_Declaration_Order()
	{
		var service = Service();

		Assert.Equal(new TypeLayout(1, 1), service.GetLayout(TypeSymbol.Byte));
		Assert.Equal(new TypeLayout(2, 2), service.GetLayout(TypeSymbol.Short));
		Assert.Equal(new TypeLayout(4, 4), service.GetLayout(TypeSymbol.Int));
		Assert.Equal(new TypeLayout(8, 8), service.GetLayout(TypeSymbol.Long));
		Assert.Equal(new TypeLayout(4, 4), service.GetLayout(TypeSymbol.Float));
		Assert.Equal(new TypeLayout(8, 8), service.GetLayout(TypeSymbol.Double));
	}

	[Fact]
	public void Pointer_Sized_Types_Follow_The_Active_Pointer_Width()
	{
		Assert.Equal(new TypeLayout(8, 8), Service().GetLayout(TypeSymbol.NInt));
		Assert.Equal(new TypeLayout(8, 8), Service().GetLayout(TypeSymbol.NUInt));
		Assert.Equal(new TypeLayout(8, 8), Service().GetLayout(TypeSymbol.String));
		Assert.Equal(new TypeLayout(4, 4), Service(pointerBytes: 4).GetLayout(TypeSymbol.NInt));
		Assert.Equal(new TypeLayout(4, 4), Service(pointerBytes: 4).GetLayout(TypeSymbol.String));
	}

	[Fact]
	public void Struct_Without_Padding_Reports_No_Padding()
	{
		var layout = Service().Inspect(Struct("Packed", ("A", TypeSymbol.Int), ("B", TypeSymbol.Int), ("C", TypeSymbol.Int)));

		Assert.Equal(12, layout.Size);
		Assert.Equal(4, layout.Alignment);
		Assert.Equal(12, layout.PayloadSize);
		Assert.Equal(0, layout.PaddingSize);
		Assert.Empty(layout.Padding);
	}

	[Fact]
	public void Struct_With_Internal_And_Tail_Padding_Reports_Both()
	{
		// The increment's worked example: int, long, byte on a 64-bit target.
		var layout = Service().Inspect(
			Struct("Header", ("Kind", TypeSymbol.Int), ("Payload", TypeSymbol.Long), ("Version", TypeSymbol.Byte)));

		Assert.Equal(24, layout.Size);
		Assert.Equal(8, layout.Alignment);
		Assert.Equal(13, layout.PayloadSize);
		Assert.Equal(11, layout.PaddingSize);

		Assert.Equal([0L, 8L, 16L], layout.Members.Select(member => member.Offset));
		Assert.Equal([4L, 8L, 1L], layout.Members.Select(member => member.Size));
		Assert.Equal([4L, 8L, 1L], layout.Members.Select(member => member.Alignment));
		Assert.Equal(["int", "long", "byte"], layout.Members.Select(member => member.TypeDisplay));

		Assert.Equal(2, layout.Padding.Count);
		Assert.Equal(new TypeLayoutPaddingInfo(4, 4, TypeLayoutPaddingKind.Internal), layout.Padding[0]);
		Assert.Equal(new TypeLayoutPaddingInfo(17, 7, TypeLayoutPaddingKind.Tail), layout.Padding[1]);
	}

	[Fact]
	public void Zero_Padding_Is_Reported_As_Zero_Rather_Than_Omitted()
	{
		// 8 + 4 fills the object exactly, so the 8-byte alignment introduces no extra tail.
		var exact = Service().Inspect(Struct("Exact", ("A", TypeSymbol.Long), ("B", TypeSymbol.Long)));

		Assert.Equal(16, exact.Size);
		Assert.Equal(16, exact.PayloadSize);
		Assert.Equal(0, exact.PaddingSize);
		Assert.Empty(exact.Padding);

		// A trailing half-word does need the rounded-up tail, and it is reported as such.
		var rounded = Service().Inspect(Struct("Rounded", ("A", TypeSymbol.Long), ("B", TypeSymbol.Int)));

		Assert.Equal(16, rounded.Size);
		Assert.Equal(12, rounded.PayloadSize);
		Assert.Equal(4, rounded.PaddingSize);
		Assert.Equal(new TypeLayoutPaddingInfo(12, 4, TypeLayoutPaddingKind.Tail), Assert.Single(rounded.Padding));
	}

	[Fact]
	public void Nested_Struct_Field_Reports_The_Nested_Size()
	{
		var inner = Struct("Inner", ("A", TypeSymbol.Int), ("B", TypeSymbol.Int));
		var layout = Service().Inspect(Struct("Outer", ("Nested", inner), ("Tail", TypeSymbol.Byte)));

		var nested = Assert.Single(layout.Members, member => member.Name == "Nested");
		Assert.Equal(0, nested.Offset);
		Assert.Equal(8, nested.Size);
		Assert.Equal(4, nested.Alignment);
		Assert.Equal(12, layout.Size);
		Assert.Equal(3, layout.PaddingSize);
	}

	[Fact]
	public void Pointer_Width_Changes_Struct_Layout()
	{
		var type = Struct("Holds", ("Pointer", TypeSymbol.NInt), ("Byte", TypeSymbol.Byte));

		Assert.Equal(16, Service(pointerBytes: 8).Inspect(type).Size);
		Assert.Equal(8, Service(pointerBytes: 4).Inspect(type).Size);
	}

	[Fact]
	public void Array_Exposes_Element_Stride_And_Count()
	{
		var layout = Service().Inspect(new ArrayTypeSymbol(Struct("Entry", ("A", TypeSymbol.Int), ("B", TypeSymbol.Int), ("C", TypeSymbol.Int)), 32));

		Assert.Equal(384, layout.Size);
		Assert.Equal(4, layout.Alignment);
		Assert.Equal(12, layout.Stride);
		Assert.Equal(32, layout.ElementCount);
	}

	[Fact]
	public void Enum_Layout_Is_Its_Storage_Type()
	{
		var enumType = new EnumTypeSymbol("Status", [new("Ready", 0), new("Running", 1)], TypeSymbol.Byte);
		var layout = Service().Inspect(enumType);

		Assert.Equal(1, layout.Size);
		Assert.Equal(1, layout.Alignment);
	}

	[Fact]
	public void Raw_Union_Takes_The_Largest_Variant()
	{
		var union = new UnionTypeSymbol(
			"Value",
			[
				new UnionFieldSymbol("IntValue", TypeSymbol.Int, false),
				new UnionFieldSymbol("FloatValue", TypeSymbol.Double, false),
			])
		{ IsUnsafe = true };

		var layout = Service().Inspect(union);

		Assert.Equal(8, layout.Size);
		Assert.Equal(8, layout.Alignment);
		Assert.All(layout.Members, member => Assert.Equal(0, member.Offset));
		Assert.Equal(8, layout.PayloadSize);
	}

	[Fact]
	public void Raw_Union_Alignment_Comes_From_The_Widest_Variant()
	{
		var union = new UnionTypeSymbol(
			"Mixed",
			[
				new UnionFieldSymbol("A", TypeSymbol.Byte, false),
				new UnionFieldSymbol("B", TypeSymbol.Long, false),
			])
		{ IsUnsafe = true };

		Assert.Equal(8, Service().Inspect(union).Alignment);
	}

	[Fact]
	public void Tagged_Union_Carries_An_Authoritative_Tag_Byte()
	{
		var union = new UnionTypeSymbol(
			"Maybe",
			[
				new UnionFieldSymbol("None", TypeSymbol.Void, true),
				new UnionFieldSymbol("Some", TypeSymbol.Long, false),
			]);

		var layout = Service().Inspect(union);

		Assert.Equal(9, layout.Size);
		Assert.Equal(8, layout.Alignment);
		Assert.Equal("tag", layout.Members[0].Name);
		Assert.Equal(1, layout.Members[1].Offset);
	}

	[Fact]
	public void Null_Pointer_Optimized_Union_Is_A_Single_Pointer()
	{
		var union = new UnionTypeSymbol(
			"MaybeRef",
			[
				new UnionFieldSymbol("None", TypeSymbol.Void, true),
				new UnionFieldSymbol("Some", new PointerTypeSymbol(TypeSymbol.Int, isMutable: false), false),
			]);

		Assert.True(union.IsNpoEligible);
		Assert.Equal(new TypeLayout(8, 8), Service().GetLayout(union));
	}

	[Fact]
	public void Empty_Struct_Follows_The_Compiler_Representation()
	{
		var layout = Service().Inspect(Struct("Empty"));

		Assert.Equal(0, layout.Size);
		Assert.Equal(1, layout.Alignment);
		Assert.Equal(0, layout.PayloadSize);
		Assert.Empty(layout.Members);
	}

	[Fact]
	public void Inspection_Numbers_Always_Agree_With_The_Compact_Layout()
	{
		var service = Service();
		var types = new TypeSymbol[]
		{
			TypeSymbol.Byte,
			TypeSymbol.Int,
			TypeSymbol.Long,
			TypeSymbol.Double,
			TypeSymbol.NUInt,
			TypeSymbol.String,
			Struct("Header", ("Kind", TypeSymbol.Int), ("Payload", TypeSymbol.Long), ("Version", TypeSymbol.Byte)),
			new ArrayTypeSymbol(TypeSymbol.Int, 12),
			new EnumTypeSymbol("Status", [new("Ready", 0)], TypeSymbol.Byte),
		};

		foreach (var type in types)
		{
			var compact = service.GetLayout(type);
			var inspection = service.Inspect(type);

			Assert.Equal(compact.Size, inspection.Size);
			Assert.Equal(compact.Alignment, inspection.Alignment);
			Assert.Equal(inspection.Size, inspection.PayloadSize + inspection.PaddingSize);
			Assert.Equal(inspection.Padding.Sum(region => region.Size), inspection.PaddingSize);
		}
	}

	[Fact]
	public void Layout_Carries_Target_Identity()
	{
		Assert.Equal("x86_64-pc-windows-msvc", Service().Inspect(TypeSymbol.Int).TargetDisplay);
		Assert.Equal("aarch64-unknown-linux-gnu", Service(target: "aarch64-unknown-linux-gnu").Inspect(TypeSymbol.Int).TargetDisplay);
	}

	[Fact]
	public void Field_Offset_Matches_The_Inspection_Member_Offset()
	{
		var service = Service();
		var type = Struct("Header", ("Kind", TypeSymbol.Int), ("Payload", TypeSymbol.Long), ("Version", TypeSymbol.Byte));
		var inspection = service.Inspect(type);

		Assert.Equal(inspection.Members.Single(member => member.Name == "Payload").Offset, service.TryGetFieldOffset(type, ["Payload"]));
		Assert.Equal(inspection.Members.Single(member => member.Name == "Version").Offset, service.TryGetFieldOffset(type, ["Version"]));
	}

	[Fact]
	public void Nested_Field_Offset_Accumulates()
	{
		var inner = Struct("Inner", ("A", TypeSymbol.Int), ("B", TypeSymbol.Int));
		var outer = Struct("Outer", ("Nested", inner));

		Assert.Equal(0, Service().TryGetFieldOffset(outer, ["Nested", "A"]));
		Assert.Equal(4, Service().TryGetFieldOffset(outer, ["Nested", "B"]));
	}
}
