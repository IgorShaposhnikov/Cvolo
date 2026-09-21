using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Owns declaration-time destructor rules and the transitive cleanup-depth validation performed
/// after all type and extension symbols have been registered.
/// </summary>
internal sealed class DestructorValidator(BindingContext context)
{
	// Memory & Safety spec §2: the destructor nesting depth is capped. Dropping a value of a
	// deeply nested (by-value) move type would recurse once per nested owner; past this bound we
	// refuse to compile rather than risk unbounded cleanup recursion.
	private const int MaxDestructorNestingDepth = 1024;

	private const string CyclicDestructorDepthError = "Cyclic destructor nesting depth exceeded. Please use an arena allocator or manual cleanup.";

	/// <summary>
	/// Validates a destructor declaration before its ordinary extension-method symbol is built.
	/// The destructor name must match the extended type and each type may own only one destructor.
	/// </summary>
	public bool ValidateDeclaration(string extendedTypeName, FunctionDeclarationSyntax method)
	{
		if (method.Name[1..] != extendedTypeName)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				method.NameSpan,
				$"Destructor name '{method.Name}' does not match extended type '{extendedTypeName}'.");
			return false;
		}

		if (context.Destructors.ContainsKey(extendedTypeName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				method.NameSpan,
				$"Duplicate destructor definition for type '{extendedTypeName}'.");
			return false;
		}

		return true;
	}

	/// <summary>
	/// Records the successfully registered destructor symbol for later cleanup and depth analysis.
	/// </summary>
	public void Register(string extendedTypeName, FunctionSymbol symbol)
	{
		context.Destructors[extendedTypeName] = symbol;
	}

	/// <summary>
	/// Pass 2. Enforces the destructor nesting-depth cap. The walk runs after every struct symbol
	/// (including embeds) is fully materialized so the transitive ownership graph is complete.
	/// Generic templates are skipped because their concrete ownership graph is checked after
	/// instantiation. Pointer fields are excluded because they do not own their pointees.
	/// </summary>
	public void ValidateDepth(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is not StructDeclarationSyntax structDecl)
					continue;

				// Generic templates' fields are type-parameter placeholders; their concrete
				// nesting is checked when an instantiation is registered.
				if (structDecl.GenericParameters.Count > 0)
					continue;

				var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);
				if (!context.StructTypes.TryGetValue(mangledName, out var structType))
					continue;

				if (DestructorDepth(structType, new HashSet<string>()) > MaxDestructorNestingDepth)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, structDecl.Span, CyclicDestructorDepthError);
				}
			}
		}
	}

	/// <summary>
	/// Returns whether dropping <paramref name="type"/> performs user-visible cleanup either
	/// directly or through an owned aggregate field. Pointer and primitive/type-parameter values
	/// do not contribute owned cleanup.
	/// </summary>
	private bool DestructorNeedsCleanup(TypeSymbol type)
	{
		switch (type)
		{
			case ArrayTypeSymbol arr:
				return DestructorNeedsCleanup(arr.ElementType);
			case SliceTypeSymbol slice:
				return DestructorNeedsCleanup(slice.ElementType);
			case StructTypeSymbol structType:
				if (HasOwnDestructor(structType))
					return true;
				return structType.Fields.Any(f => DestructorNeedsCleanup(f.Type));
			case UnionTypeSymbol unionType:
				return unionType.Fields.Any(f => !f.IsVoidVariant && DestructorNeedsCleanup(f.Type));
			default:
				return false;
		}
	}

	/// <summary>
	/// Returns whether the struct has an explicitly registered destructor that owns cleanup for its
	/// complete payload.
	/// </summary>
	private bool HasOwnDestructor(StructTypeSymbol structType) => context.Destructors.ContainsKey(structType.Name);

	/// <summary>
	/// Computes the maximum cleanup-recursion depth reachable from <paramref name="type"/> through
	/// owned fields. A struct with its own destructor is a base case because that destructor takes
	/// responsibility for the complete payload. The path set is a defensive cycle guard.
	/// </summary>
	private int DestructorDepth(TypeSymbol type, HashSet<string> path)
	{
		if (type is ArrayTypeSymbol arr)
		{
			if (!DestructorNeedsCleanup(arr.ElementType))
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var depth = 1 + DestructorDepth(arr.ElementType, path);
			path.Remove(type.Name);
			return depth;
		}

		if (type is SliceTypeSymbol slice)
		{
			if (!DestructorNeedsCleanup(slice.ElementType))
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var depth = 1 + DestructorDepth(slice.ElementType, path);
			path.Remove(type.Name);
			return depth;
		}

		if (type is UnionTypeSymbol unionType)
		{
			var variants = unionType.Fields.Where(f => !f.IsVoidVariant && DestructorNeedsCleanup(f.Type)).ToList();
			if (variants.Count == 0)
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var depth = 1 + variants.Max(f => DestructorDepth(f.Type, path));
			path.Remove(type.Name);
			return depth;
		}

		if (type is StructTypeSymbol structType)
		{
			// A struct with its own destructor takes responsibility for its whole payload.
			if (HasOwnDestructor(structType))
				return 0;

			var fields = structType.Fields.Where(f => DestructorNeedsCleanup(f.Type)).ToList();
			if (fields.Count == 0)
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var result = 1 + fields.Max(f => DestructorDepth(f.Type, path));
			path.Remove(type.Name);
			return result;
		}

		return 0;
	}
}
