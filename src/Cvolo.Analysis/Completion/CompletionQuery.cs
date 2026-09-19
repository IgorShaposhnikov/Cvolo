using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.VisibilityChecks;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Completion;

public enum CompletionQueryContext
{
	Declaration,
	Statement,
	Expression,
	Member,
	None
}

public enum CompletionKind
{
	Local,
	Parameter,
	Global,
	Function,
	Method,
	Type,
	Namespace,
	StructField,
	UnionVariant,
	EnumVariant,
	EnumMetadata,
	ArrayLength
}

public sealed record CompletionCandidate(string Label, string InsertText, CompletionKind Kind);

public sealed record CompletionQueryResult(CompletionQueryContext Context, IReadOnlyList<CompletionCandidate> Candidates);

public static class CompletionQuery
{
	public static CompletionQueryResult Compute(BindingContext context, CompilationUnitSyntax unit, int position)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(unit);
		ArgumentOutOfRangeException.ThrowIfNegative(position);

		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

		// COMPLETION-TIME MUTATION BLOCKER (reported): BindingContext.CurrentUnit/CurrentNamespace are
		// shared mutable fields read by every resolver (ResolveGlobalReference, ResolveQualifiedGlobal,
		// ResolveType's using-import, GetActiveUsings, mangling); no unit-parameterized overloads exist,
		// the compiler's own passes set them per unit, and BindingContext itself save/restores around
		// scoped operations. Refactoring every resolver/caller to pass context explicitly is a
		// cross-cutting compiler-API change, so completion keeps the same symmetric save/restore pattern
		// (try/finally below) and CompletionService serializes concurrent queries via lock(BinderContext).

		try
		{
			var state = new QueryState(context, unit, position);
			var contextKind = Classify(unit, position, out var memberNode);

			if (contextKind == CompletionQueryContext.None)
				return new CompletionQueryResult(CompletionQueryContext.None, []);

			if (contextKind == CompletionQueryContext.Member && memberNode is not null)
			{
				var visible = BuildVisibleScopeMap(state);
				return new CompletionQueryResult(CompletionQueryContext.Member, EnumerateMemberCandidates(memberNode, visible, state));
			}

			var visibleMap = BuildVisibleScopeMap(state);
			return new CompletionQueryResult(contextKind, EnumerateIdentifierCandidates(state, visibleMap));
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
		}
	}

	/// <summary>
	/// Resolves the semantic symbol bound at <paramref name="position"/> using the same binding
	/// state as completion. Returns null when no trustworthy symbol is resolved.
	/// </summary>
	public static ResolvedSymbol? ResolveSymbol(BindingContext context, CompilationUnitSyntax unit, int position)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(unit);
		ArgumentOutOfRangeException.ThrowIfNegative(position);

		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

		try
		{
			var state = new QueryState(context, unit, position);
			var visible = BuildVisibleScopeMap(state);
			return SymbolResolver.Resolve(context, unit, position, visible);
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
		}
	}

	/// <summary>
	/// Returns a compiler-owned display string for a declaration node, used by the tooling
	/// document-symbol outline. The node must come from the same compiler analysis.
	/// </summary>
	public static string DescribeDeclaration(SyntaxNode declaration, string fallback = "")
		=> SymbolResolver.Describe(declaration, fallback);

	private static CompletionQueryContext Classify(CompilationUnitSyntax unit, int position, out MemberAccessExpressionSyntax? member)
	{
		member = null;
		var path = new List<SyntaxNode>();
		SyntaxNode? current = unit;

		while (current is not null)
		{
			path.Add(current);

			if (current is MemberAccessExpressionSyntax ma && position >= ma.Expression.Span.End && position <= ma.Span.End)
			{
				member = ma;
				return CompletionQueryContext.Member;
			}

			if (StrictContains(current.Span, position) && IsLiteralNode(current))
				return CompletionQueryContext.None;

			SyntaxNode? next = null;
			foreach (var child in current.GetChildren())
			{
				if (child is MemberAccessExpressionSyntax m && position >= m.Expression.Span.End && position <= m.Span.End)
				{
					member = m;
					return CompletionQueryContext.Member;
				}

				if (StrictContains(child.Span, position))
					next = child;
			}

			current = next;
		}

		for (var i = path.Count - 1; i >= 0; i--)
		{
			var node = path[i];
			if (node is ExpressionSyntax)
				return CompletionQueryContext.Expression;
			if (node is FunctionDeclarationSyntax fn && fn.Body is not null && Touching(fn.Body.Span, position))
				return CompletionQueryContext.Statement;
		}

		return CompletionQueryContext.Declaration;
	}

	private static bool IsLiteralNode(SyntaxNode node) => node is
		StringLiteralExpressionSyntax or
		InterpolatedStringExpressionSyntax or
		CharacterLiteralExpressionSyntax or
		IntegerLiteralExpressionSyntax or
		DoubleLiteralExpressionSyntax or
		BooleanLiteralExpressionSyntax or
		NullLiteralExpressionSyntax;

	private static Dictionary<string, ScopedVariable> BuildVisibleScopeMap(QueryState state)
	{
		var frames = new List<List<ScopedVariable>>();
		var root = new List<ScopedVariable>();
		frames.Add(root);

		var fn = FindEnclosingFunction(state.Unit, state.Position);
		if (fn is not null)
		{
			foreach (var param in fn.Parameters)
			{
				if (param.Type is not null)
					root.Add(new ScopedVariable(param.Name, OriginKind.Parameter, ResolveTypeOrNull(state, param.Type), param));
			}

			if (fn.Body is not null)
				ProcessStatements(fn.Body.Statements, root, state, frames);
		}

		var visible = new Dictionary<string, ScopedVariable>(StringComparer.Ordinal);
		foreach (var frame in frames)
		{
			foreach (var variable in frame)
				visible[variable.Name] = variable;
		}

		return visible;
	}

	private static FunctionDeclarationSyntax? FindEnclosingFunction(SyntaxNode node, int position)
	{
		foreach (var child in node.GetChildren())
		{
			if (FindEnclosingFunction(child, position) is { } nested)
				return nested;
		}

		if (node is FunctionDeclarationSyntax fn && fn.Body is not null && Touching(fn.Body.Span, position))
			return fn;

		return null;
	}

	private static void ProcessStatements(
		IReadOnlyList<SyntaxNode> statements,
		List<ScopedVariable> frame,
		QueryState state,
		List<List<ScopedVariable>> frames)
	{
		foreach (var statement in statements)
		{
			if (statement.Span.End < state.Position)
			{
				DeclareFromStatement(statement, frame, state);
			}
			else if (Touching(statement.Span, state.Position))
			{
				EnterStatement(statement, frame, state, frames);
				return;
			}
			else if (statement.Span.Start > state.Position)
			{
				return;
			}
		}
	}

	private static void DeclareFromStatement(SyntaxNode statement, List<ScopedVariable> frame, QueryState state)
	{
		if (statement is VariableDeclarationSyntax v && v.Span.End <= state.Position)
			frame.Add(new ScopedVariable(v.Name, OriginKind.Local, ResolveVariableType(state, v), v));
	}

	private static void EnterStatement(
		SyntaxNode statement,
		List<ScopedVariable> frame,
		QueryState state,
		List<List<ScopedVariable>> frames)
	{
		switch (statement)
		{
			case VariableDeclarationSyntax v:
				if (v.Span.End <= state.Position)
					frame.Add(new ScopedVariable(v.Name, OriginKind.Local, ResolveVariableType(state, v), v));
				break;

			case BlockStatementSyntax block:
				{
					var child = new List<ScopedVariable>();
					frames.Add(child);
					ProcessStatements(block.Statements, child, state, frames);
					break;
				}

			case LabeledBlockStatementSyntax labeled:
				{
					var child = new List<ScopedVariable>();
					frames.Add(child);
					ProcessStatements(labeled.Body.Statements, child, state, frames);
					break;
				}

			case UnsafeBlockStatementSyntax unsafeBlock:
				{
					var child = new List<ScopedVariable>();
					frames.Add(child);
					ProcessStatements(unsafeBlock.Body.Statements, child, state, frames);
					break;
				}

			case ForStatementSyntax forStatement:
				{
					var child = new List<ScopedVariable>();
					frames.Add(child);
					if (forStatement.Initializer is not null && forStatement.Initializer.Span.End <= state.Position)
						child.Add(new ScopedVariable(forStatement.Initializer.Name, OriginKind.Local, ResolveVariableType(state, forStatement.Initializer), forStatement.Initializer));
					WalkBody(forStatement.Condition, child, state, frames);
					WalkBody(forStatement.Increment, child, state, frames);
					WalkBody(forStatement.Body, child, state, frames);
					break;
				}

			case ForEachStatementSyntax forEach:
				{
					var child = new List<ScopedVariable>();
					frames.Add(child);
					if (forEach.Collection.Span.End <= state.Position && state.Position >= forEach.Body.Span.Start)
					{
						var itemType = ResolveTypeOrNull(state, forEach.ItemTypeName ?? forEach.ExplicitItemType);
						child.Add(new ScopedVariable(forEach.ItemName, OriginKind.Local, itemType, forEach));
					}

					WalkBody(forEach.Body, child, state, frames);
					break;
				}

			case IfStatementSyntax ifStatement:
				WalkBody(ifStatement.ThenStatement, frame, state, frames);
				if (ifStatement.ElseClause is not null)
					WalkBody(ifStatement.ElseClause.Body, frame, state, frames);
				break;

			case WhileStatementSyntax whileStatement:
				WalkBody(whileStatement.Body, frame, state, frames);
				break;

			case SwitchStatementSyntax switchStatement:
				foreach (var caseSyntax in switchStatement.Cases)
				{
					if (!Touching(caseSyntax.Span, state.Position))
						continue;

					var child = new List<ScopedVariable>();
					frames.Add(child);
					if (!caseSyntax.IsDefault && caseSyntax.VariableName is not null)
						child.Add(new ScopedVariable(caseSyntax.VariableName, OriginKind.Local, null, caseSyntax));
					ProcessStatements(caseSyntax.Body, child, state, frames);
					break;
				}

				break;

			case TryStatementSyntax tryStatement:
				WalkBody(tryStatement.Body, frame, state, frames);
				foreach (var clause in tryStatement.CatchClauses)
				{
					if (!Touching(clause.Span, state.Position))
						continue;

					var child = new List<ScopedVariable>();
					frames.Add(child);
					if (clause.BindingName is not null)
						child.Add(new ScopedVariable(clause.BindingName, OriginKind.Local, ResolveTypeOrNull(state, clause.ErrorTypeName), clause));
					ProcessStatements(clause.Body.Statements, child, state, frames);
					break;
				}

				break;

			case DeferStatementSyntax deferStatement:
				WalkBody(deferStatement.Body, frame, state, frames);
				break;
		}
	}

	private static void WalkBody(SyntaxNode? node, List<ScopedVariable> frame, QueryState state, List<List<ScopedVariable>> frames)
	{
		if (node is null)
			return;

		if (node.Span.End < state.Position)
		{
			DeclareFromStatement(node, frame, state);
		}
		else if (Touching(node.Span, state.Position))
		{
			EnterStatement(node, frame, state, frames);
		}
	}

	private static List<CompletionCandidate> EnumerateIdentifierCandidates(QueryState state, Dictionary<string, ScopedVariable> visible)
	{
		var candidates = new List<CompletionCandidate>();
		var seen = new HashSet<(string Label, CompletionKind Kind)>();

		foreach (var name in visible.Keys.OrderBy(n => n, StringComparer.Ordinal))
		{
			var variable = visible[name];
			var kind = variable.Origin == OriginKind.Parameter ? CompletionKind.Parameter : CompletionKind.Local;
			AddCandidate(candidates, seen, name, kind);
		}

		foreach (var shortName in state.Context.GlobalsByShortName.Keys.OrderBy(n => n, StringComparer.Ordinal))
		{
			if (visible.ContainsKey(shortName))
				continue;

			var global = state.Context.ResolveGlobalReference(shortName, out _);
			if (global is null || !IsVisible(state, global.Visibility, global.DeclaringUnit))
				continue;

			AddCandidate(candidates, seen, shortName, CompletionKind.Global);
		}

		foreach (var entry in state.Context.OverloadedFunctions.OrderBy(e => e.Key, StringComparer.Ordinal))
		{
			if (!IsReachableNamespace(state, entry.Key))
				continue;

			// Constructors and extension methods share OverloadedFunctions with ordinary free
			// functions. Compiler-generated receiver-backed callables carry the synthetic first
			// parameter named "this"; a free-function label survives when any reachable overload
			// is not receiver-backed and is visible. Never let declaration order choose visibility.
			if (!entry.Value.Any(fn => !IsReceiverBacked(fn) && IsVisible(state, fn.Visibility, fn.DeclaringUnit)))
				continue;

			AddCandidate(candidates, seen, Leaf(entry.Key), CompletionKind.Function);
		}

		// Generic function templates are callable declarations — DeclarationPass stores their
		// syntax in GenericFunctionTemplates and ValidationPass resolves calls through that table.
		// They are functions, not types, and type-only narrowing happens in CompletionService.
		foreach (var (key, func) in state.Context.GenericFunctionTemplates.OrderBy(e => e.Key, StringComparer.Ordinal))
		{
			if (!IsReachableNamespace(state, key))
				continue;

			var declaringUnit = state.Context.SymbolUnits.TryGetValue(key, out var unit) ? unit : null;
			if (!IsVisible(state, func.Visibility, declaringUnit))
				continue;

			AddCandidate(candidates, seen, func.Name, CompletionKind.Function);
		}

		AddTypeCandidates(state, candidates, seen);
		AddNamespaceCandidates(state, candidates, seen);

		return candidates;
	}

	private static void AddTypeCandidates(QueryState state, List<CompletionCandidate> candidates, HashSet<(string Label, CompletionKind Kind)> seen)
	{
		var types = new List<(TypeSymbol Symbol, string Key)>();

		foreach (var (key, symbol) in state.Context.StructTypes)
		{
			if (!key.Contains('<'))
				types.Add((symbol, key));
		}

		foreach (var (key, symbol) in state.Context.UnionTypes)
		{
			if (!key.Contains('<'))
				types.Add((symbol, key));
		}

		foreach (var (key, symbol) in state.Context.EnumTypes)
		{
			if (!key.Contains('<'))
				types.Add((symbol, key));
		}

		foreach (var (key, symbol) in state.Context.InterfaceTypes)
		{
			if (!key.Contains('<'))
				types.Add((symbol, key));
		}

		foreach (var (key, symbol) in state.Context.ProtocolTypes)
		{
			if (!key.Contains('<'))
				types.Add((symbol, key));
		}

		// Generic struct/union templates already have placeholder TypeSymbols in StructTypes /
		// UnionTypes with the declaration visibility attached. Keeping one authoritative path
		// avoids bypassing VisibilityChecker and also deduplicates generic template labels.
		foreach (var (key, declaration) in state.Context.TypeAliases.OrderBy(e => e.Key, StringComparer.Ordinal))
		{
			if (IsReachableNamespace(state, key))
				AddCandidate(candidates, seen, Leaf(key), CompletionKind.Type);
		}

		foreach (var (symbol, key) in types.OrderBy(t => t.Symbol.Name, StringComparer.Ordinal))
		{
			if (!IsReachableNamespace(state, key))
				continue;

			var label = symbol.Name.Contains('.') ? symbol.Name[(symbol.Name.LastIndexOf('.') + 1)..] : symbol.Name;
			if (state.Context.LegacyVisibility || VisibilityChecker.IsAccessible(symbol.Visibility, state.Context.CurrentUnit, GetDeclaringUnit(state, symbol)))
				AddCandidate(candidates, seen, label, CompletionKind.Type);
		}
	}

	private static void AddNamespaceCandidates(QueryState state, List<CompletionCandidate> candidates, HashSet<(string Label, CompletionKind Kind)> seen)
	{
		var labels = new SortedSet<string>(StringComparer.Ordinal);
		var currentNamespace = state.Context.CurrentNamespace;
		var activeUsings = state.Context.GetActiveUsings(state.Unit);

		foreach (var declared in state.Context.DeclaredNamespaces)
		{
			if (string.IsNullOrEmpty(declared))
				continue;

			// A fully-qualified path can always start at its root segment.
			AddFirstNamespaceSegment(labels, declared);

			// ResolveType/ResolveQualifiedGlobal also allow names relative to the current namespace
			// and active usings. Offer only the next segment from those same prefixes instead of
			// leaking an unrelated nested leaf that would not resolve on its own.
			if (!string.IsNullOrEmpty(currentNamespace) && TryGetRelativeNamespace(declared, currentNamespace!, out var localRelative))
				AddFirstNamespaceSegment(labels, localRelative);

			foreach (var usingNamespace in activeUsings)
			{
				if (TryGetRelativeNamespace(declared, usingNamespace, out var importedRelative))
					AddFirstNamespaceSegment(labels, importedRelative);
			}
		}

		foreach (var label in labels)
			AddCandidate(candidates, seen, label, CompletionKind.Namespace);
	}

	private static bool TryGetRelativeNamespace(string declared, string prefix, out string relative)
	{
		if (declared.Length > prefix.Length &&
			declared.StartsWith(prefix, StringComparison.Ordinal) &&
			declared[prefix.Length] == '.')
		{
			relative = declared[(prefix.Length + 1)..];
			return true;
		}

		relative = string.Empty;
		return false;
	}

	private static void AddFirstNamespaceSegment(SortedSet<string> labels, string path)
	{
		if (string.IsNullOrEmpty(path))
			return;

		var dot = path.IndexOf('.');
		labels.Add(dot < 0 ? path : path[..dot]);
	}

	private static IReadOnlyList<CompletionCandidate> EnumerateMemberCandidates(
		MemberAccessExpressionSyntax memberNode,
		Dictionary<string, ScopedVariable> visible,
		QueryState state)
	{
		var candidates = new List<CompletionCandidate>();
		var receiver = memberNode.Expression;
		TypeSymbol? type = null;
		var isEnumTypeNameReceiver = false;

		if (ExpressionTypeResolver.GetDottedName(receiver) is { } dotted)
		{
			var dotIndex = dotted.LastIndexOf('.');
			if (dotIndex >= 0)
			{
				type = state.Context.ResolveQualifiedGlobal(dotted[..dotIndex], dotted[(dotIndex + 1)..])?.Type;
			}
			else
			{
				type = state.Context.ResolveGlobalReference(dotted, out _)?.Type;
			}

			if (type is null && state.Context.ResolveType(dotted) is EnumTypeSymbol enumType)
			{
				type = enumType;
				isEnumTypeNameReceiver = true;
			}
		}

		if (type is null)
			type = ExpressionTypeResolver.Resolve(state.Context, visible, receiver);

		if (type is PointerTypeSymbol pointer)
			type = pointer.ReferencedType;

		if (type is null)
			return candidates;

		switch (type)
		{
			case EnumTypeSymbol enumType when isEnumTypeNameReceiver:
				{
					var seen = new HashSet<string>(StringComparer.Ordinal);
					foreach (var variant in enumType.Variants)
						AddCandidate(candidates, seen, variant.Name, variant.Name, CompletionKind.EnumVariant);
					foreach (var metaName in new[] { "Min", "Max", "Count", "Values" })
						AddCandidate(candidates, seen, metaName, metaName, CompletionKind.EnumMetadata);
					break;
				}

			case EnumTypeSymbol:
				AddExtensionMethods(candidates, type, state);
				break;

			case UnionTypeSymbol unionType:
				foreach (var field in unionType.Fields)
				{
					if (!IsFieldVisible(state, unionType, field.Visibility))
						continue;
					AddCandidate(candidates, field.Name, field.Name, CompletionKind.UnionVariant);
				}

				AddExtensionMethods(candidates, type, state);
				break;

			case StructTypeSymbol structType:
				foreach (var field in structType.Fields)
				{
					if (!IsFieldVisible(state, structType, field.Visibility))
						continue;
					AddCandidate(candidates, field.Name, field.Name, CompletionKind.StructField);
				}

				AddExtensionMethods(candidates, type, state);
				break;

			default:
				if (type is SliceTypeSymbol or ArrayTypeSymbol || type.Name.EndsWith("[]"))
					AddCandidate(candidates, "Length", "Length", CompletionKind.ArrayLength);
				break;
		}

		return candidates;
	}

	private static void AddExtensionMethods(List<CompletionCandidate> candidates, TypeSymbol type, QueryState state)
	{
		// BindingContext owns the extension lookup key families used by ordinary dotted calls
		// and protocol matching. Completion only applies editor-facing visibility/dedup here.
		var visibleMethods = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var (memberName, function) in state.Context.GetExtensionMethodCandidates(type, state.Unit))
		{
			if (IsVisible(state, function.Visibility, function.DeclaringUnit))
				visibleMethods.Add(memberName);
		}

		foreach (var methodName in visibleMethods)
			AddCandidate(candidates, methodName, methodName, CompletionKind.Method);
	}

	private static bool IsReceiverBacked(FunctionSymbol function) =>
		function.Parameters.Count > 0 && string.Equals(function.Parameters[0].Name, "this", StringComparison.Ordinal);

	private static void AddCandidate(
		List<CompletionCandidate> candidates,
		HashSet<(string Label, CompletionKind Kind)> seen,
		string label,
		CompletionKind kind)
	{
		if (!seen.Add((label, kind)))
			return;
		candidates.Add(new CompletionCandidate(label, label, kind));
	}

	private static void AddCandidate(
		List<CompletionCandidate> candidates,
		HashSet<string> seen,
		string label,
		string insertText,
		CompletionKind kind)
	{
		if (!seen.Add(label))
			return;
		candidates.Add(new CompletionCandidate(label, insertText, kind));
	}

	private static void AddCandidate(List<CompletionCandidate> candidates, string label, CompletionKind kind)
		=> candidates.Add(new CompletionCandidate(label, label, kind));

	private static void AddCandidate(List<CompletionCandidate> candidates, string label, string insertText, CompletionKind kind)
		=> candidates.Add(new CompletionCandidate(label, insertText, kind));

	private static bool IsVisible(QueryState state, Visibility visibility, CompilationUnitSyntax? declaringUnit)
	{
		if (state.Context.LegacyVisibility)
			return true;
		return VisibilityChecker.IsAccessible(visibility, state.Context.CurrentUnit, declaringUnit);
	}

	private static bool IsFieldVisible(QueryState state, TypeSymbol owner, Visibility visibility)
	{
		if (state.Context.LegacyVisibility)
			return true;
		return VisibilityChecker.IsAccessible(visibility, state.Context.CurrentUnit, GetDeclaringUnit(state, owner));
	}

	private static bool IsReachableNamespace(QueryState state, string mangledName)
	{
		var dotIndex = mangledName.LastIndexOf('.');
		var namespacePart = dotIndex < 0 ? "" : mangledName[..dotIndex];
		if (namespacePart.Length == 0)
			return true;
		if (string.Equals(namespacePart, state.Context.CurrentNamespace, StringComparison.Ordinal))
			return true;
		foreach (var usingNamespace in state.Context.GetActiveUsings(state.Unit))
		{
			if (string.Equals(namespacePart, usingNamespace, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	private static CompilationUnitSyntax? GetDeclaringUnit(QueryState state, TypeSymbol type)
	{
		if (state.Context.SymbolUnits.TryGetValue(type.Name, out var direct))
			return direct;

		var genericIndex = type.Name.IndexOf('<');
		if (genericIndex > 0 && state.Context.SymbolUnits.TryGetValue(type.Name[..genericIndex], out var template))
			return template;

		return null;
	}

	private static TypeSymbol? ResolveTypeOrNull(QueryState state, string? typeName)
	{
		if (string.IsNullOrWhiteSpace(typeName))
			return null;
		return state.Context.ResolveType(state.Context.NormalizeGenericName(typeName));
	}

	private static TypeSymbol? ResolveVariableType(QueryState state, VariableDeclarationSyntax v)
	{
		if (state.Context.VariableSymbols.TryGetValue(v, out var symbol) && symbol.Type is not null)
			return symbol.Type;
		return ResolveTypeOrNull(state, v.Type);
	}

	private static bool StrictContains(TextSpan span, int position) => span.Start <= position && position < span.End;

	private static bool Touching(TextSpan span, int position) => span.Start <= position && position <= span.End;

	private static string Leaf(string mangledName)
	{
		var dotIndex = mangledName.LastIndexOf('.');
		return dotIndex < 0 ? mangledName : mangledName[(dotIndex + 1)..];
	}

	private sealed class QueryState(BindingContext context, CompilationUnitSyntax unit, int position)
	{
		public BindingContext Context { get; } = context;

		public CompilationUnitSyntax Unit { get; } = unit;

		public int Position { get; } = position;
	}
}
