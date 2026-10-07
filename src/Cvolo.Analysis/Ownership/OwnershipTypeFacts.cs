using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Ownership;

/// <summary>
/// Analysis-side ownership type predicates. These mirror the emitter's destruction obligations
/// so that the semantic ownership decision is made once, during binding, and never rediscovered
/// during code generation.
/// </summary>
internal static class OwnershipTypeFacts
{
	/// <summary>True when <paramref name="type"/> carries a user-defined destructor.</summary>
	public static bool HasUserDestructor(TypeSymbol type, BindingContext context)
		=> type is StructTypeSymbol structType
			&& context.OverloadedFunctions.ContainsKey($"{structType.Name}.~{structType.Name}");

	/// <summary>True when the union owns a resource in at least one non-void variant.</summary>
	public static bool IsResourceOwningUnion(UnionTypeSymbol unionType, BindingContext context)
		=> unionType.Fields.Any(field => !field.IsVoidVariant && NeedsDestruction(field.Type, context));

	/// <summary>
	/// True when a value of <paramref name="type"/> carries a recursive destruction obligation at
	/// scope exit or on replacement.
	/// </summary>
	public static bool NeedsDestruction(TypeSymbol type, BindingContext context)
		=> NeedsDestruction(type, context, []);

	private static bool NeedsDestruction(TypeSymbol type, BindingContext context, HashSet<string> active)
	{
		switch (type)
		{
			case StructTypeSymbol structType:
				if (context.OverloadedFunctions.ContainsKey($"{structType.Name}.~{structType.Name}"))
					return true;
				if (!active.Add(structType.Name))
					return false;
				var structOwned = structType.Fields.Any(field => NeedsDestruction(field.Type, context, active));
				active.Remove(structType.Name);
				return structOwned;

			case UnionTypeSymbol unionType:
				if (!active.Add(unionType.Name))
					return false;
				var unionOwned = unionType.Fields.Any(field => !field.IsVoidVariant && NeedsDestruction(field.Type, context, active));
				active.Remove(unionType.Name);
				return unionOwned;

			case ArrayTypeSymbol arrayType:
				return NeedsDestruction(arrayType.ElementType, context, active);

			default:
				return false;
		}
	}
}
