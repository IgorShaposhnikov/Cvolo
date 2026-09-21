using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Resolves and flattens declaration-time struct embedding after raw type symbols exist.
/// </summary>
/// <remarks>
/// Embedded fields are prepended recursively so layout, field lookup, literals, and byte-size
/// calculations observe the same flattened field sequence. This service deliberately owns only
/// Pass 0c layout linking; promoted extension methods remain in the later function-registration phase.
/// </remarks>
internal sealed class EmbedLinker(BindingContext context)
{
	/// <summary>
	/// Links all <c>struct T embed Base</c> clauses, reporting invalid generic, unknown, cyclic,
	/// and field-conflict cases while preserving the original struct when linking fails.
	/// </summary>
	/// <param name="units">Compilation units whose raw struct symbols were already registered.</param>
	public void Link(IEnumerable<CompilationUnitSyntax> units)
	{
		var structDecls = new Dictionary<string, StructDeclarationSyntax>();
		foreach (var unit in units)
		{
			var members = unit.NamespaceDeclaration is not null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					structDecls[context.GetMangledName(structDecl.Name, unit.NamespaceDeclaration?.Name)] = structDecl;
			}
		}

		var flattened = new Dictionary<string, List<StructFieldSymbol>>();
		foreach (var (mangledName, decl) in structDecls)
		{
			if (decl.EmbeddedType is null)
				continue;

			FlattenStructFields(mangledName, decl, structDecls, flattened, new HashSet<string>());
		}
	}

	/// <summary>
	/// Recursively materializes the flattened field list for one struct and replaces its cached
	/// symbol with a linked symbol whose embedded composition points at the resolved base struct.
	/// </summary>
	private List<StructFieldSymbol> FlattenStructFields(
		string mangledName,
		StructDeclarationSyntax decl,
		Dictionary<string, StructDeclarationSyntax> structDecls,
		Dictionary<string, List<StructFieldSymbol>> flattened,
		HashSet<string> stack)
	{
		if (flattened.TryGetValue(mangledName, out var cached))
			return cached;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		var ownFields = context.StructTypes[mangledName].Fields.ToList();
		if (decl.EmbeddedType is null)
		{
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		if (context.GenericStructTemplates.ContainsKey(mangledName))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Cannot use embed in generic struct template '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		var embeddedName = decl.EmbeddedType;
		var baseType = context.ResolveType(embeddedName) as StructTypeSymbol;
		if (baseType is null || !structDecls.ContainsKey(baseType.Name))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Unknown struct '{embeddedName}' in embed clause of struct '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		if (context.GenericStructTemplates.ContainsKey(baseType.Name))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Cannot embed generic struct template '{embeddedName}' in struct '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		if (!stack.Add(baseType.Name))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Circular embed clause involving struct '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		var baseDecl = structDecls[baseType.Name];
		var baseFields = FlattenStructFields(baseType.Name, baseDecl, structDecls, flattened, stack);
		stack.Remove(baseType.Name);

		var conflict = ownFields.FirstOrDefault(f => baseFields.Any(b => b.Name == f.Name));
		if (conflict is not null)
		{
			context.Diagnostics.Report(currentFileContext, decl.Span,
				$"Field '{conflict.Name}' of struct '{decl.Name}' conflicts with embedded field from '{embeddedName}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		var combined = new List<StructFieldSymbol>(baseFields.Count + ownFields.Count);
		combined.AddRange(baseFields);
		combined.AddRange(ownFields);

		var rebuiltEmbed = baseType; // The embedded composition has already been flattened recursively.
		var rebuilt = new StructTypeSymbol(mangledName, combined, rebuiltEmbed)
		{
			Visibility = decl.Visibility
		};
		context.StructTypes[mangledName] = rebuilt;
		context.ReplaceTypeInCache(mangledName, rebuilt);
		flattened[mangledName] = combined;
		return combined;
	}
}
