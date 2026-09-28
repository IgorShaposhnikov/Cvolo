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
/// are never counted. Two resolvers are consulted in one pass: the navigation index, which covers
/// every symbol the editor can navigate to, and then the binder, which is what reaches locals and
/// parameters. Those two have no <see cref="SymbolId"/>, so they are keyed by their declaration node.
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
				var resolved = navigation.Lookup(documentId, token.StartIndex);
				if (resolved is not null)
				{
					var span = resolved.SubjectSpan;
					if (span.Length <= 0 || span.Start < 0 || span.End > text.Length)
						continue;

					if (!seen.Add((documentId, span.Start, span.Length)))
						continue;

					if (!bySymbol.TryGetValue(resolved.SymbolId, out var occurrences))
					{
						occurrences = [];
						bySymbol[resolved.SymbolId] = occurrences;
					}

					var isDeclaration = declarationStarts.TryGetValue(resolved.SymbolId, out var starts)
						&& starts.Contains((documentId, span.Start));

					occurrences.Add(new Occurrence(documentId, span, isDeclaration));
					symbolOfDeclaration.TryAdd(navigation.Declaration(resolved.SymbolId), resolved.SymbolId);
					continue;
				}

				// The navigation index has no entry for a local or a parameter, but the binder still
				// resolves them, and the editor highlights them, so the second pass uses the binder.
				if (context is null
					|| !analysis.UnitsByDocument.TryGetValue(documentId, out var unit)
					|| unit is null)
				{
					continue;
				}

				lock (context)
				{
					if (CompletionQuery.ResolveSymbol(context, unit, token.StartIndex) is not { } local)
						continue;

					if (local.Declaration is not VariableDeclarationSyntax and not ParameterSyntax)
						continue;

					var localSpan = local.SubjectSpan;
					if (localSpan.Length <= 0 || localSpan.Start < 0 || localSpan.End > text.Length)
						continue;

					if (!seen.Add((documentId, localSpan.Start, localSpan.Length)))
						continue;

					if (!byDeclaration.TryGetValue(local.Declaration, out var localOccurrences))
					{
						localOccurrences = [];
						byDeclaration[local.Declaration] = localOccurrences;
					}

					localOccurrences.Add(new Occurrence(
						documentId,
						new TextSpan(localSpan.Start, localSpan.Length),
						IsNameOf(local.Declaration, source, localSpan)));
				}
			}
		}

		return new OccurrenceIndex(bySymbol, symbolOfDeclaration, byDeclaration);
	}

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

