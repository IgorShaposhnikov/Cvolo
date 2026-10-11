using Antlr4.Runtime;
using Cvolo.Analysis;
using Cvolo.Analysis.Completion;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>One semantically resolved occurrence of a symbol inside one document.</summary>
internal readonly record struct Occurrence(DocumentId DocumentId, TextSpan Span, bool IsDeclaration);

/// <summary>
/// Snapshot-scoped, lazily built map from every symbol in the project to all of its semantic
/// occurrences, built in a single project-wide pass. Editor features that need many counts at once
/// (CodeLens over a whole document) read this instead of running one project scan per symbol: the
/// index belongs to one <see cref="ProjectSnapshot"/> instance and is never shared with a derived
/// snapshot, so a document edit produces a new snapshot and therefore a new index.
/// <para>
/// Candidate occurrences are discovered lexically, but an occurrence is only accepted once a
/// compiler-backed resolver binds it, so comments, strings and unrelated same-spelling identifiers
/// are never counted. Every identifier is resolved exactly once through a lean resolution (no
/// documentation/type enrichment, which hover-grade paths pay but a whole-project pass must not);
/// symbols the navigation index covers yield <see cref="SymbolId"/> occurrences, while locals and
/// parameters — which have no <see cref="SymbolId"/> — are keyed by their declaration node.
/// </para>
/// </summary>
internal sealed class OccurrenceIndex
{
	private readonly Dictionary<SymbolId, List<Occurrence>> _bySymbol;
	private readonly Dictionary<SyntaxNode, SymbolId> _symbolOfDeclaration;
	private readonly Dictionary<SyntaxNode, List<Occurrence>> _byDeclaration;

	private OccurrenceIndex(
		Dictionary<SymbolId, List<Occurrence>> bySymbol,
		Dictionary<SyntaxNode, SymbolId> symbolOfDeclaration,
		Dictionary<SyntaxNode, List<Occurrence>> byDeclaration)
	{
		_bySymbol = bySymbol;
		_symbolOfDeclaration = symbolOfDeclaration;
		_byDeclaration = byDeclaration;
	}

	internal static OccurrenceIndex Build(ProjectSnapshot snapshot)
	{
		var navigation = snapshot.GetNavigationIndex();
		var analysis = snapshot.GetAnalysis();
		var context = analysis.BinderContext;
		var declarationStarts = new Dictionary<SymbolId, HashSet<(DocumentId DocumentId, int Start)>>();

		foreach (var (symbol, documentId, selectionSpan) in navigation.AllDefinitions())
		{
			if (!declarationStarts.TryGetValue(symbol, out var starts))
			{
				starts = [];
				declarationStarts[symbol] = starts;
			}

			starts.Add((documentId, selectionSpan.Start));
		}

		var bySymbol = new Dictionary<SymbolId, List<Occurrence>>();
		var symbolOfDeclaration = new Dictionary<SyntaxNode, SymbolId>(ReferenceEqualityComparer.Instance);
		var byDeclaration = new Dictionary<SyntaxNode, List<Occurrence>>(ReferenceEqualityComparer.Instance);
var seen = new HashSet<(DocumentId DocumentId, int Start, int Length)>();

			foreach (var documentId in snapshot.DocumentIds)
			{
				var document = snapshot.GetDocument(documentId);
				var text = document.Text;
				var source = text.ToString();

			foreach (var token in IdentifierTokens(source))
			{
				var navigable = navigation.LookupOccurrence(documentId, token.StartIndex, out var resolved);
				if (navigable is not null)
				{
					var span = navigable.SubjectSpan;
					if (span.Length <= 0 || span.Start < 0 || span.End > text.Length)
						continue;

					if (!seen.Add((documentId, span.Start, span.Length)))
						continue;

					if (!bySymbol.TryGetValue(navigable.SymbolId, out var occurrences))
					{
						occurrences = [];
						bySymbol[navigable.SymbolId] = occurrences;
					}

					var isDeclaration = declarationStarts.TryGetValue(navigable.SymbolId, out var starts)
						&& starts.Contains((documentId, span.Start));

					occurrences.Add(new Occurrence(documentId, span, isDeclaration));
					symbolOfDeclaration.TryAdd(navigation.Declaration(navigable.SymbolId), navigable.SymbolId);
					continue;
				}

				// The navigation index has no entry for a local or a parameter, but the binder still
				// resolves them, and the editor highlights them. The same lean resolve above already
				// reached them, so no second resolution is needed.
				if (context is null
					|| !analysis.UnitsByDocument.TryGetValue(documentId, out var unit)
					|| unit is null
					|| resolved is null
					|| resolved.Declaration is not VariableDeclarationSyntax and not ParameterSyntax)
				{
					continue;
				}

				var localSpan = resolved.SubjectSpan;
				if (localSpan.Length <= 0 || localSpan.Start < 0 || localSpan.End > text.Length)
					continue;

				if (!seen.Add((documentId, localSpan.Start, localSpan.Length)))
					continue;

				if (!byDeclaration.TryGetValue(resolved.Declaration, out var localOccurrences))
				{
					localOccurrences = [];
					byDeclaration[resolved.Declaration] = localOccurrences;
				}

				localOccurrences.Add(new Occurrence(
					documentId,
					new TextSpan(localSpan.Start, localSpan.Length),
					IsNameOf(resolved.Declaration, source, localSpan)));
			}
		}

		return new OccurrenceIndex(bySymbol, symbolOfDeclaration, byDeclaration);
	}

	/// <summary>
	/// One symbol whose reference count a CodeLens shows. The name is the plain identifier spelling
	/// the declaration uses, so candidate use sites can be found textually before any symbol
	/// resolution is attempted.
	/// </summary>
	internal readonly record struct ReferenceCountTarget(SymbolId Symbol, string Name);

	/// <summary>
	/// Reference counts for a small set of requested symbols (the declarations of one edited
	/// document). Unlike <see cref="Build"/>, which resolves every identifier in every document to
	/// serve whole-project queries such as highlights, this resolves only tokens whose spelling
	/// could be a use of one of the requested names, so CodeLens stays interactive even when the
	/// project drags in tens of thousands of generated identifiers.
	/// <para>
	/// A generic declaration's type parameters are references to the generic symbol itself, so a
	/// parameter name such as <c>T</c> is a candidate too; the accepted counts stay identical to
	/// <see cref="ReferenceRenameService.GetReferences"/> because every candidate is confirmed with
	/// the same full <see cref="NavigationIndex.Lookup"/> and the same declaration-span exclusion
	/// that Find All References uses.
	/// </para>
	/// </summary>
	internal static IReadOnlyDictionary<SymbolId, int> ReferenceCounts(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		IReadOnlyList<ReferenceCountTarget> targets)
	{
		var bySymbol = new Dictionary<SymbolId, ReferenceCountTarget>();
		var candidateNames = new HashSet<string>(StringComparer.Ordinal);
		var definitionSpans = new Dictionary<SymbolId, HashSet<(DocumentId DocumentId, TextSpan Span)>>();

		foreach (var target in targets)
		{
			if (string.IsNullOrEmpty(target.Name))
				continue;

			bySymbol[target.Symbol] = target;
			candidateNames.Add(target.Name);
			foreach (var parameter in GenericParameterNames(navigation.Declaration(target.Symbol)))
				candidateNames.Add(parameter);

			if (!definitionSpans.ContainsKey(target.Symbol))
			{
				definitionSpans[target.Symbol] = navigation.Definitions(target.Symbol)
					.Select(definition => (definition.DocumentId, definition.SelectionSpan))
					.ToHashSet();
			}
		}

		var counts = new Dictionary<SymbolId, int>();
		var seen = new HashSet<(DocumentId DocumentId, int Start, int Length)>();

		foreach (var documentId in snapshot.DocumentIds)
		{
			var document = snapshot.GetDocument(documentId);
			var text = document.Text;
			var source = text.ToString();

			foreach (var token in IdentifierTokens(source))
			{
				if (!candidateNames.Contains(token.Text))
					continue;

				var resolved = navigation.Lookup(documentId, token.StartIndex);
				if (resolved is null || !bySymbol.ContainsKey(resolved.SymbolId))
					continue;

				var span = resolved.SubjectSpan;
				if (span.Length <= 0 || span.Start < 0 || span.End > text.Length)
					continue;

				if (definitionSpans[resolved.SymbolId].Contains((documentId, span)))
					continue;

				if (!seen.Add((documentId, span.Start, span.Length)))
					continue;

				counts[resolved.SymbolId] = counts.TryGetValue(resolved.SymbolId, out var count) ? count + 1 : 1;
			}
		}

		return counts;
	}

	/// <summary>
	/// The declared generic parameter names of a declaration, which are additional spellings a use
	/// of the declaration may take. Non-generic declarations contribute none.
	/// </summary>
	private static IReadOnlyList<string> GenericParameterNames(SyntaxNode? declaration) => declaration switch
	{
		FunctionDeclarationSyntax function => function.GenericParameters,
		StructDeclarationSyntax structure => structure.GenericParameters,
		UnionDeclarationSyntax union => union.GenericParameters,
		InterfaceDeclarationSyntax @interface => @interface.GenericParameters,
		ProtocolDeclarationSyntax protocol => protocol.GenericParameters,
		DelegateDeclarationSyntax @delegate => @delegate.GenericParameters,
		TypeAliasDeclarationSyntax alias => alias.GenericParameters,
		ExtensionDeclarationSyntax extension => extension.GenericParameters,
		_ => [],
	};

	/// <summary>
	/// The number of references to <paramref name="symbol"/>, excluding its own declaration. Zero is
	/// a computed result, never a failure.
	/// </summary>
	internal int ReferenceCount(SymbolId symbol)
	{
		if (!_bySymbol.TryGetValue(symbol, out var occurrences))
			return 0;

		var count = 0;
		foreach (var occurrence in occurrences)
		{
			if (!occurrence.IsDeclaration)
				count++;
		}

		return count;
	}

	/// <summary>
	/// Every occurrence of <paramref name="symbol"/>, optionally restricted to one document and in
	/// ascending source order.
	/// </summary>
	internal IReadOnlyList<Occurrence> OccurrencesOf(SymbolId symbol, DocumentId? documentId = null)
	{
		if (!_bySymbol.TryGetValue(symbol, out var occurrences))
			return [];

		return Filter(occurrences, documentId);
	}

	/// <summary>
	/// Every occurrence bound to <paramref name="declaration"/>, whether that declaration is a
	/// navigable symbol, a local, or a parameter. This is the entry point for per-position features
	/// such as document highlights, which must work for locals the navigation index never sees.
	/// </summary>
	internal IReadOnlyList<Occurrence> OccurrencesOfDeclaration(SyntaxNode declaration, DocumentId? documentId = null)
	{
		List<Occurrence>? collected = null;

		if (_symbolOfDeclaration.TryGetValue(declaration, out var symbol))
			collected = Filter(_bySymbol[symbol], documentId).ToList();

		if (_byDeclaration.TryGetValue(declaration, out var local))
		{
			collected ??= [];
			collected.AddRange(Filter(local, documentId));
		}

		if (collected is null)
			return [];

		return [.. collected.OrderBy(occurrence => occurrence.Span.Start).ThenBy(occurrence => occurrence.Span.End)];
	}

	private static IReadOnlyList<Occurrence> Filter(List<Occurrence> occurrences, DocumentId? documentId)
	{
		if (documentId is not { } wanted)
			return occurrences;

		var result = new List<Occurrence>();
		foreach (var occurrence in occurrences)
		{
			if (occurrence.DocumentId == wanted)
				result.Add(occurrence);
		}

		return [.. result.OrderBy(occurrence => occurrence.Span.Start).ThenBy(occurrence => occurrence.Span.End)];
	}

	/// <summary>
	/// True when the occurrence is the declaring name of a local or a parameter, as opposed to a use
	/// of it. The declaration span starts at the keyword, so the declared name is the first
	/// identifier inside it.
	/// </summary>
	private static bool IsNameOf(SyntaxNode declaration, string source, Cvolo.Core.Diagnostics.TextSpan span)
	{
		var name = declaration switch
		{
			VariableDeclarationSyntax variable => variable.Name,
			ParameterSyntax parameter => parameter.Name,
			_ => null,
		};

		if (name is null || name.Length == 0 || declaration.Span.Start < 0 || declaration.Span.End > source.Length)
			return false;

		var index = source.IndexOf(name, declaration.Span.Start, StringComparison.Ordinal);
		return index >= 0 && index + name.Length <= declaration.Span.End && index == span.Start && span.Length == name.Length;
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
}

