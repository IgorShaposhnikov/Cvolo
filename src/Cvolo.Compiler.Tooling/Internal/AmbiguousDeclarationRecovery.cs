using Cvolo.Analysis;
using Cvolo.Analysis.Completion;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Refines the grammar's ambiguous 'type identifier(expression);' statement only after
/// the first project-wide bind has resolved actual types, namespaces and imports.
/// A physical newline is not a statement delimiter in Cvolo. A valid user type
/// always wins; an unresolved type on its own line cannot synthesize a local
/// variable in place of the following call. The second parse retains every
/// original source coordinate by masking only the incorrect head.
/// </summary>
internal static class AmbiguousDeclarationRecovery
{
	private static readonly HashSet<string> BuiltinTypeNames = new(StringComparer.Ordinal)
	{
		"int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
		"nint", "nuint", "float", "double", "bool", "string", "char", "void",
	};

	internal static (
		IReadOnlyList<CompilationUnitSyntax> Units,
		IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> UnitsByDocument,
		IReadOnlyList<Diagnostic> Diagnostics)?
		TryRecover(
			ProjectSnapshot snapshot,
			BindingContext binding,
			IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> originalUnits,
			IReadOnlyList<Diagnostic> originalDiagnostics)
	{
		var repairs = new Dictionary<DocumentId, Dictionary<int, (string TypeName, bool WasResolvedType)>>();
		var previousUnit = binding.CurrentUnit;
		var previousNamespace = binding.CurrentNamespace;

		try
		{
			foreach (var (documentId, unit) in originalUnits)
			{
				if (unit is null || !snapshot.TryGetDocument(documentId, out var document))
					continue;

				var source = document.Text.ToString();
				binding.CurrentUnit = unit;
				binding.CurrentNamespace = unit.NamespaceDeclaration?.Name;

				// The binder ordinarily enters generic scopes while validating a
				// function body. Do not misinterpret an in-scope generic type
				// parameter from a different query context as an unknown type.
				var genericNames = Descendants(unit)
					.OfType<FunctionDeclarationSyntax>()
					.SelectMany(fn => fn.GenericParameters)
					.ToHashSet(StringComparer.Ordinal);
				var heads = new Dictionary<int, (string TypeName, bool WasResolvedType)>();
				foreach (var variable in Descendants(unit).OfType<VariableDeclarationSyntax>())
				{
					if (!TryGetSplitHead(source, variable, out var headStart, out var nameStart, out var assignment)
						|| variable.Type is not { } typeName
						|| genericNames.Contains(typeName))
						continue;

					var typeResolved = binding.ResolveType(typeName) is not null;

					// 'Begi\ns = value;' must not introduce a second local when 's'
					// already exists in the lexical scope (including extension fields).
					// Conversely, 'MissingType\nnewName = value;' may be an intended
					// multiline declaration: without proof of an existing target
					// keep its ordinary unknown-type diagnostic.
					if (assignment)
					{
						if (typeResolved
							|| !HasVisibleAssignableName(binding, unit, nameStart, variable.Name))
							continue;
					}
					else if (typeResolved && (!BuiltinTypeNames.Contains(typeName)
						|| !HasVisibleFunction(binding, unit, nameStart, variable.Name)))
					{
						continue;
					}

					heads[headStart] = (typeName, typeResolved);
				}

				if (heads.Count != 0)
					repairs.Add(documentId, heads);
			}
		}
		finally
		{
			binding.CurrentUnit = previousUnit;
			binding.CurrentNamespace = previousNamespace;
		}

		if (repairs.Count == 0)
			return null;

		// Never mutate the generation-owned parse cache or the first binder's AST.
		// All replacement units are created from the same immutable snapshot text.
		var newUnits = new Dictionary<DocumentId, CompilationUnitSyntax?>(originalUnits);
		var newDiagnostics = originalDiagnostics
			.Where(d => !repairs.ContainsKey(d.Location.DocumentId))
			.ToList();
		var docByFile = snapshot.Documents.ToDictionary(
			pair => pair.Value.FilePath, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

		foreach (var (documentId, heads) in repairs)
		{
			var document = snapshot.Documents[documentId];
			var parser = new AntlrSyntaxParser(heads);
			var unit = parser.Parse(new CompilationContext(document.Text.ToString(), document.FilePath));
			if (unit is null)
				return null; // Never replace a usable AST with a failed speculative parse.

			newUnits[documentId] = unit;
			newDiagnostics.AddRange(DiagnosticAdapter.ConvertDiagnostics(parser.Diagnostics, docByFile));
		}

		return (
			snapshot.DocumentIds
				.Where(id => newUnits[id] is not null)
				.Select(id => newUnits[id]!)
				.ToArray(),
			newUnits,
			newDiagnostics);
	}

	private static bool HasVisibleAssignableName(
		BindingContext binding, CompilationUnitSyntax unit, int nameStart, string name)
	{
		// Completion's lexical-scope walker excludes the declaration currently
		// being inspected. It includes earlier locals, parameters, globals, and
		// accessible fields of the enclosing extension's receiver.
		return CompletionQuery.Compute(binding, unit, nameStart).Candidates.Any(
			candidate => candidate.Kind is CompletionKind.Local
				or CompletionKind.Parameter or CompletionKind.Global
				&& string.Equals(candidate.Label, name, StringComparison.Ordinal));
	}

	private static bool HasVisibleFunction(
		BindingContext binding, CompilationUnitSyntax unit, int nameStart, string name)
	{
		// CompletionQuery applies the compiler's namespace, import and visibility
		// rules. Merely finding a function of that name in an unrelated package is
		// not sufficient evidence to split a well-formed primitive declaration.
		return CompletionQuery.Compute(binding, unit, nameStart).Candidates.Any(
			candidate => candidate.Kind == CompletionKind.Function
				&& string.Equals(candidate.Label, name, StringComparison.Ordinal));
	}

	private static bool TryGetSplitHead(
		string source, VariableDeclarationSyntax variable,
		out int headStart, out int nameStart, out bool assignment)
	{
		headStart = -1;
		nameStart = -1;
		assignment = false;
		var typeName = variable.Type;
		if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(variable.Name)
			|| variable.Initializer is null)
			return false;

		var start = variable.Span.Start;
		if (start < 0 || start + typeName.Length > source.Length
			|| !source.AsSpan(start, typeName.Length).SequenceEqual(typeName.AsSpan()))
			return false;

		// Only a bare identifier head on its *own* physical line qualifies.
		// Qualified names, generic arguments, pointers, comments, and all
		// same-line constructor-style initializers remain untouched.
		if (!(char.IsLetter(typeName[0]) || typeName[0] == '_')
			|| typeName.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
			return false;

		var cursor = start + typeName.Length;
		var newLine = false;
		while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
		{
			if (source[cursor] is '\r' or '\n')
				newLine = true;
			cursor++;
		}

		if (!newLine || cursor + variable.Name.Length > source.Length
			|| !source.AsSpan(cursor, variable.Name.Length).SequenceEqual(variable.Name.AsSpan()))
			return false;

		nameStart = cursor;
		cursor += variable.Name.Length;
		if (cursor < source.Length && (char.IsLetterOrDigit(source[cursor]) || source[cursor] == '_'))
			return false;

		// Only a second-line call or assignment is ambiguous. A normal
		// multiline declaration ('Window\nvariable;') remains one declaration.
		while (cursor < source.Length && source[cursor] is ' ' or '\t')
			cursor++;
		if (cursor >= source.Length || source[cursor] is not ('(' or '='))
			return false;

		assignment = source[cursor] == '=';
		if (assignment && cursor + 1 < source.Length && source[cursor + 1] == '=')
			return false;

		headStart = start;
		return true;
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode? node)
	{
		// Damaged ANTLR trees can omit required children. Recovery must never
		// crash unrelated editor queries when traversing a null placeholder.
		if (node is null)
			yield break;
		yield return node;
		foreach (var child in node.GetChildren())
			foreach (var descendant in Descendants(child))
				yield return descendant;
	}
}
