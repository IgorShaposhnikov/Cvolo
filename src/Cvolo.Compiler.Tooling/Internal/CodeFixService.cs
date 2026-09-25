using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using CoreDiagnosticIds = Cvolo.Core.Diagnostics.DiagnosticIds;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Public entry points for compiler-provided code fixes. Every call resolves against exactly the
/// snapshot it is given; no newer snapshot, disk state, or mutable workspace state is consulted.
/// </summary>
internal static class CodeFixService
{
	internal static IReadOnlyList<CodeFixInfo> GetCodeFixes(ProjectSnapshot snapshot, DocumentId document, TextSpan range)
		=> snapshot.GetCodeFixIndex().GetCodeFixes(document, range);

	internal static CodeFixResolution ResolveCodeFix(ProjectSnapshot snapshot, CodeFixId fix)
		=> snapshot.GetCodeFixIndex().Resolve(fix);
}

internal sealed record CodeFixSuggestion(string Title, IReadOnlyList<CodeFixEdit> Edits);

/// <summary>
/// Snapshot-scoped index of compiler-provided code fixes. Entries are built once per snapshot from
/// the same immutable diagnostics the rest of the semantic API surfaces.
/// </summary>
internal sealed class CodeFixIndex
{
	private readonly Guid _token = Guid.NewGuid();
	private readonly Dictionary<int, CodeFixEntry> _byId = new();
	private readonly Dictionary<DocumentId, List<CodeFixEntry>> _byDocument = new();
	private int _nextId;

	private CodeFixIndex()
	{
	}

	internal static CodeFixIndex Build(ProjectSnapshot snapshot)
	{
		var index = new CodeFixIndex();

		foreach (var documentId in snapshot.DocumentIds)
		{
			var document = snapshot.GetDocument(documentId);

			foreach (var diagnostic in document.GetDiagnostics())
			{
				if (!CodeFixRegistry.TryCreate(document, diagnostic, out var suggestions))
					continue;

				foreach (var suggestion in suggestions)
					index.Add(documentId, diagnostic, suggestion.Title, suggestion.Edits);
			}
		}

		return index;
	}

	private void Add(DocumentId documentId, Diagnostic diagnostic, string title, IReadOnlyList<CodeFixEdit> edits)
	{
		var id = new CodeFixId(_token, _nextId++);
		var entry = new CodeFixEntry(id, title, diagnostic, edits);

		_byId[id.Value] = entry;

		if (!_byDocument.TryGetValue(documentId, out var entries))
		{
			entries = [];
			_byDocument[documentId] = entries;
		}

		entries.Add(entry);
	}

	internal IReadOnlyList<CodeFixInfo> GetCodeFixes(DocumentId documentId, TextSpan range)
	{
		if (!_byDocument.TryGetValue(documentId, out var entries))
			return [];

		var result = new List<CodeFixInfo>();

		foreach (var entry in entries)
		{
			if (Intersects(entry.Diagnostic.Location.Span, range))
				result.Add(new CodeFixInfo(entry.Id, entry.Title, [entry.Diagnostic]));
		}

		return result;
	}

	internal CodeFixResolution Resolve(CodeFixId fix)
	{
		if (fix.SnapshotToken != _token || !_byId.TryGetValue(fix.Value, out var entry))
			return new CodeFixFailure("The code fix is not available for this project state.");

		if (entry.Edits.Count == 0)
			return new CodeFixFailure("The code fix produced no changes.");

		return new CodeFixSuccess(entry.Edits);
	}

	internal bool Owns(CodeFixId fix)
		=> fix.SnapshotToken == _token && _byId.ContainsKey(fix.Value);

	private static bool Intersects(TextSpan left, TextSpan right)
		=> left.Start <= right.End && right.Start <= left.End;

	private sealed record CodeFixEntry(CodeFixId Id, string Title, Diagnostic Diagnostic, IReadOnlyList<CodeFixEdit> Edits);
}

/// <summary>
/// Compiler-owned registry of diagnostics that have a semantics-safe quick fix. Titles and edit
/// sets are produced here so the language server never hardcodes a diagnostic id or reconstructs a
/// fix from diagnostic message text.
/// </summary>
internal static class CodeFixRegistry
{
	internal static bool TryCreate(DocumentSnapshot document, Diagnostic diagnostic, out IReadOnlyList<CodeFixSuggestion> suggestions)
	{
		switch (diagnostic.Id)
		{
			case CoreDiagnosticIds.DoubleLiteralToFloatAssignment:
				return TryCreateFloatSuffixFix(document, diagnostic, out suggestions);
			case CoreDiagnosticIds.UnresolvedFunctionCall:
				return TryCreateAddUsingFix(document, diagnostic, out suggestions);
			default:
				suggestions = [];
				return false;
		}
	}

	private static bool TryCreateFloatSuffixFix(DocumentSnapshot document, Diagnostic diagnostic, out IReadOnlyList<CodeFixSuggestion> suggestions)
	{
		suggestions = [];

		var span = diagnostic.Location.Span;

		if (span.Length <= 0 || span.End > document.Text.Length)
			return false;

		var literal = document.Text.GetText(span);

		if (literal.Length == 0 || literal.EndsWith('f') || literal.EndsWith('F'))
			return false;

		var edit = new CodeFixEdit(document.Id, new TextSpan(span.End, 0), "f");
		suggestions = [new CodeFixSuggestion("Add 'f' suffix to make it a float literal", [edit])];
		return true;
	}

	private static bool TryCreateAddUsingFix(DocumentSnapshot document, Diagnostic diagnostic, out IReadOnlyList<CodeFixSuggestion> suggestions)
	{
		suggestions = [];

		var snapshot = document.OwningSnapshot;

		if (snapshot is null)
			return false;

		var analysis = snapshot.GetAnalysis();

		if (!analysis.UnitsByDocument.TryGetValue(document.Id, out var unit) || unit is null)
			return false;

		var functionName = FindCalledFunctionName(unit, diagnostic.Location.Span, document.Text);

		if (functionName is null || functionName.Length == 0 || functionName.Contains('.', StringComparison.Ordinal))
			return false;

		var candidates = FindDeclaringNamespaces(snapshot, analysis, unit, functionName);

		if (candidates.Count == 0)
			return false;

		var text = document.Text;
		var newline = text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var insertAt = FindUsingInsertionPoint(unit, text);
		var result = new List<CodeFixSuggestion>(candidates.Count);

		foreach (var candidate in candidates)
		{
			var edit = new CodeFixEdit(document.Id, new TextSpan(insertAt, 0), $"using {candidate};{newline}");
			result.Add(new CodeFixSuggestion($"Add 'using {candidate};'", [edit]));
		}

		suggestions = result;
		return true;
	}

	private static string? FindCalledFunctionName(CompilationUnitSyntax unit, TextSpan argumentListSpan, SourceText text)
	{
		foreach (var node in Descendants(unit))
		{
			if (node is CallExpressionSyntax call
				&& call.ArgumentListSpan.Start == argumentListSpan.Start
				&& call.ArgumentListSpan.Length == argumentListSpan.Length)
			{
				return call.FunctionName;
			}
		}

		return null;
	}

	private static List<string> FindDeclaringNamespaces(ProjectSnapshot snapshot, AnalyzedProject analysis, CompilationUnitSyntax requestingUnit, string functionName)
	{
		var active = new HashSet<string>(StringComparer.Ordinal);

		foreach (var directive in requestingUnit.Usings)
		{
			if (!directive.IsExposed)
				active.Add(directive.NamespaceName);
		}

		if (requestingUnit.NamespaceDeclaration is { } ownNamespace)
		{
			foreach (var directive in ownNamespace.Usings)
			{
				if (!directive.IsExposed)
					active.Add(directive.NamespaceName);
			}

			active.Add(ownNamespace.Name);
		}

		var found = new HashSet<string>(StringComparer.Ordinal);

		foreach (var unit in EnumerateUnits(snapshot, analysis))
		{
			var namespaceName = unit.NamespaceDeclaration?.Name;
			var members = unit.NamespaceDeclaration?.Members ?? unit.Members;

			if (string.IsNullOrEmpty(namespaceName) || active.Contains(namespaceName))
				continue;

			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax function && string.Equals(function.Name, functionName, StringComparison.Ordinal))
				{
					found.Add(namespaceName);
					break;
				}
			}
		}

		var result = new List<string>(found);
		result.Sort(StringComparer.Ordinal);
		return result;
	}

	private static IEnumerable<CompilationUnitSyntax> EnumerateUnits(ProjectSnapshot snapshot, AnalyzedProject analysis)
	{
		foreach (var unit in analysis.Units)
			yield return unit;

		foreach (var external in snapshot.ExternalUnits)
			yield return external.Unit;
	}

	private static int FindUsingInsertionPoint(CompilationUnitSyntax unit, SourceText text)
	{
		var lastEnd = -1;

		foreach (var directive in unit.Usings)
			lastEnd = Math.Max(lastEnd, directive.Span.End);

		if (unit.NamespaceDeclaration is { } ns)
		{
			foreach (var directive in ns.Usings)
				lastEnd = Math.Max(lastEnd, directive.Span.End);
		}

		if (lastEnd < 0)
			return 0;

		if (lastEnd + 1 < text.Length && text[lastEnd] == '\r' && text[lastEnd + 1] == '\n')
			return lastEnd + 2;

		if (lastEnd < text.Length && text[lastEnd] == '\n')
			return lastEnd + 1;

		return lastEnd;
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
	{
		foreach (var child in root.GetChildren())
		{
			yield return child;

			foreach (var descendant in Descendants(child))
				yield return descendant;
		}
	}
}
