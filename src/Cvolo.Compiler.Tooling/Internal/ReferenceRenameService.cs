using Antlr4.Runtime;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Snapshot-pure semantic reference and rename operations. Candidate source occurrences are
/// discovered lexically, but identity is accepted only after the compiler-backed navigation index
/// resolves the occurrence to the requested <see cref="SymbolId"/>.
/// </summary>
internal static class ReferenceRenameService
{
	internal static IReadOnlyList<SymbolReference> GetReferences(ProjectSnapshot snapshot, SymbolId symbol, bool includeDeclaration)
	{
		var index = snapshot.GetNavigationIndex();
		if (!index.Owns(symbol))
			return [];

		var definitions = index.Definitions(symbol)
			.Select(d => (d.DocumentId, d.SelectionSpan))
			.ToHashSet();
		var result = new List<SymbolReference>();
		var seen = new HashSet<(DocumentId DocumentId, int Start, int Length)>();

		foreach (var documentId in snapshot.DocumentIds)
		{
			var document = snapshot.GetDocument(documentId);
			foreach (var token in IdentifierTokens(document.Text.ToString()))
			{
				var position = token.StartIndex;
				var resolved = index.Lookup(documentId, position);
				if (resolved is null || resolved.SymbolId != symbol)
					continue;

				var span = resolved.SubjectSpan;
				if (span.Length <= 0 || span.Start < 0 || span.End > document.Text.Length)
					continue;

				var isDeclaration = definitions.Contains((documentId, span));
				if (!includeDeclaration && isDeclaration)
					continue;

				if (seen.Add((documentId, span.Start, span.Length)))
					result.Add(new SymbolReference(documentId, span, isDeclaration));
			}
		}

		return [.. result
			.OrderBy(r => snapshot.GetDocument(r.DocumentId).FilePath, StringComparer.Ordinal)
			.ThenBy(r => r.Span.Start)
			.ThenBy(r => r.Span.End)];
	}

	internal static RenamePreparation? PrepareRename(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
	{
		var lookup = snapshot.GetNavigationIndex().Lookup(document.Id, position);
		if (lookup is null || !snapshot.GetNavigationIndex().IsSourceOwned(lookup.SymbolId))
			return null;

		if (lookup.SubjectSpan.Length <= 0 || lookup.SubjectSpan.End > document.Text.Length)
			return null;

		var placeholder = document.Text.GetText(lookup.SubjectSpan);
		if (string.IsNullOrWhiteSpace(placeholder) || !IsIdentifier(placeholder))
			return null;

		return new RenamePreparation(lookup.SymbolId, lookup.SubjectSpan, placeholder);
	}

	internal static RenameResult Rename(ProjectSnapshot snapshot, SymbolId symbol, string newName)
	{
		var index = snapshot.GetNavigationIndex();
		if (!index.Owns(symbol) || !index.IsSourceOwned(symbol))
			return new RenameFailure("The selected symbol is not renameable in this project.");

		if (!IsIdentifier(newName))
			return new RenameFailure($"'{newName}' is not a valid Cvolo identifier.");

		var definitions = index.Definitions(symbol);
		if (definitions.Count == 0)
			return new RenameFailure("The selected symbol does not have a source declaration in this project.");

		var firstDefinition = definitions[0];
		var currentName = snapshot.GetDocument(firstDefinition.DocumentId).Text.GetText(firstDefinition.SelectionSpan);
		if (string.Equals(currentName, newName, StringComparison.Ordinal))
			return new RenameSuccess([]);

		// Reject declaration collisions before producing edits. The speculative compiler pass below
		// remains the final authority for broader rebinding errors, but overload identity is cheap and
		// deterministic to validate directly here. In particular, two functions in the same declaration
		// scope may share a name only when their callable signatures are distinct.
		if (HasFunctionSignatureCollision(snapshot, index, symbol, newName))
			return new RenameFailure($"Renaming to '{newName}' conflicts with an existing function declaration with the same signature.");

		var references = GetReferences(snapshot, symbol, includeDeclaration: true);
		if (references.Count == 0)
			return new RenameFailure("The compiler could not establish a complete rename set for the selected symbol.");

		var edits = references
			.Select(r => new RenameEdit(r.DocumentId, r.Span, newName))
			.ToList();

		// Speculatively apply the complete edit set to a derived immutable snapshot, then require every
		// edited occurrence to bind to one canonical renamed symbol. This catches declaration collisions
		// and rebinding without introducing an LSP-side or textual semantic rule.
		var candidate = snapshot;
		var adjustedStarts = new Dictionary<(DocumentId Document, int OriginalStart), int>();
		foreach (var group in edits.GroupBy(e => e.DocumentId))
		{
			var document = candidate.GetDocument(group.Key);
			var original = document.Text.ToString();
			var ordered = group.OrderBy(e => e.Span.Start).ToArray();
			var delta = 0;
			foreach (var edit in ordered)
			{
				if (edit.Span.Start < 0 || edit.Span.Length <= 0 || edit.Span.End > original.Length)
					return new RenameFailure("The compiler produced an invalid source span for rename.");
				adjustedStarts[(group.Key, edit.Span.Start)] = edit.Span.Start + delta;
				delta += newName.Length - edit.Span.Length;
			}

			var rewritten = original;
			foreach (var edit in ordered.OrderByDescending(e => e.Span.Start))
				rewritten = rewritten.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, newName);
			candidate = candidate.WithDocument(group.Key, SourceText.From(rewritten));
		}

		// Reject any compiler error introduced by the speculative rename before accepting the
		// rebinding result. Navigation alone is not sufficient here: when two declarations acquire
		// the same name/signature, the navigation index can still resolve every edited token to one
		// declaration even though the compiler has correctly diagnosed a duplicate definition.
		if (TryGetIntroducedError(snapshot, candidate, out var introducedError))
			return new RenameFailure($"Renaming to '{newName}' is not valid: {introducedError}");

		SymbolId? renamedId = null;
		foreach (var edit in edits)
		{
			var document = candidate.GetDocument(edit.DocumentId);
			var newStart = adjustedStarts[(edit.DocumentId, edit.Span.Start)];
			var resolved = document.GetSymbolAtPosition(newStart);
			if (resolved is null || !string.Equals(resolved.Name, newName, StringComparison.Ordinal))
				return new RenameFailure($"Renaming to '{newName}' would make at least one occurrence bind incorrectly.");

			renamedId ??= resolved.SymbolId;
			if (resolved.SymbolId != renamedId.Value)
				return new RenameFailure($"Renaming to '{newName}' conflicts with another declaration or changes symbol binding.");
		}

		return new RenameSuccess(edits);
	}

	private static bool HasFunctionSignatureCollision(ProjectSnapshot snapshot, NavigationIndex index, SymbolId symbol, string newName)
	{
		if (index.Declaration(symbol) is not FunctionDeclarationSyntax target)
			return false;

		var analysis = snapshot.GetAnalysis();
		if (!TryFindFunctionScope(analysis, target, out var targetNamespace, out var targetOwner))
			return false;

		foreach (var (_, unit) in analysis.UnitsByDocument)
		{
			if (unit is null || !string.Equals(unit.NamespaceDeclaration?.Name, targetNamespace, StringComparison.Ordinal))
				continue;

			var members = unit.NamespaceDeclaration is { } ns ? ns.Members : unit.Members;
			foreach (var member in members)
			{
				if (targetOwner is null)
				{
					if (member is FunctionDeclarationSyntax candidate && !ReferenceEquals(candidate, target)
						&& string.Equals(candidate.Name, newName, StringComparison.Ordinal)
						&& SameCallableSignature(target, candidate))
						return true;
					continue;
				}

				if (member is not ExtensionDeclarationSyntax extension
					|| !string.Equals(extension.ExtendedTypeName, targetOwner, StringComparison.Ordinal))
					continue;

				foreach (var candidate in extension.Methods)
				{
					if (!ReferenceEquals(candidate, target)
						&& string.Equals(candidate.Name, newName, StringComparison.Ordinal)
						&& SameCallableSignature(target, candidate))
						return true;
				}
			}
		}

		return false;
	}

	private static bool TryFindFunctionScope(AnalyzedProject analysis, FunctionDeclarationSyntax target, out string? namespaceName, out string? extensionOwner)
	{
		foreach (var (_, unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
				continue;

			var members = unit.NamespaceDeclaration is { } ns ? ns.Members : unit.Members;
			foreach (var member in members)
			{
				if (ReferenceEquals(member, target))
				{
					namespaceName = unit.NamespaceDeclaration?.Name;
					extensionOwner = null;
					return true;
				}

				if (member is ExtensionDeclarationSyntax extension && extension.Methods.Any(method => ReferenceEquals(method, target)))
				{
					namespaceName = unit.NamespaceDeclaration?.Name;
					extensionOwner = extension.ExtendedTypeName;
					return true;
				}
			}
		}

		namespaceName = null;
		extensionOwner = null;
		return false;
	}

	private static bool SameCallableSignature(FunctionDeclarationSyntax left, FunctionDeclarationSyntax right)
	{
		if (left.GenericParameters.Count != right.GenericParameters.Count
			|| left.Parameters.Count != right.Parameters.Count
			|| left.Receiver != right.Receiver)
			return false;

		for (var i = 0; i < left.Parameters.Count; i++)
		{
			if (!string.Equals(left.Parameters[i].Type, right.Parameters[i].Type, StringComparison.Ordinal))
				return false;
		}

		return true;
	}

	private static bool TryGetIntroducedError(ProjectSnapshot original, ProjectSnapshot candidate, out string message)
	{
		var baseline = ErrorMultiset(original.GetAnalysis().ResultDiagnostics);
		var remaining = new Dictionary<(string Id, string Message), int>(baseline);

		foreach (var diagnostic in candidate.GetAnalysis().ResultDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
		{
			var key = (diagnostic.Id, diagnostic.Message);
			if (remaining.TryGetValue(key, out var count) && count > 0)
			{
				remaining[key] = count - 1;
				continue;
			}

			message = diagnostic.Message;
			return true;
		}

		message = string.Empty;
		return false;
	}

	private static Dictionary<(string Id, string Message), int> ErrorMultiset(IReadOnlyList<Diagnostic> diagnostics)
	{
		var result = new Dictionary<(string Id, string Message), int>();
		foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
		{
			var key = (diagnostic.Id, diagnostic.Message);
			result.TryGetValue(key, out var count);
			result[key] = count + 1;
		}
		return result;
	}

	private static IEnumerable<IToken> IdentifierTokens(string source)
	{
		var lexer = new CvoloLexer(new AntlrInputStream(source));
		foreach (var token in lexer.GetAllTokens())
		{
			if (token.Channel == Lexer.DefaultTokenChannel && token.Type == CvoloLexer.Identifier)
				yield return token;
		}
	}

	private static bool IsIdentifier(string text)
	{
		if (string.IsNullOrEmpty(text))
			return false;

		var lexer = new CvoloLexer(new AntlrInputStream(text));
		var tokens = lexer.GetAllTokens()
			.Where(t => t.Channel == Lexer.DefaultTokenChannel && t.Type != TokenConstants.EOF)
			.ToArray();
		return tokens.Length == 1 && tokens[0].Type == CvoloLexer.Identifier &&
			tokens[0].StartIndex == 0 && tokens[0].StopIndex == text.Length - 1;
	}
}
