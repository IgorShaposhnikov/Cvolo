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
	Initializer,
	DeclarationName,
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

public sealed record CompletionCandidate(
	string Label,
	string InsertText,
	CompletionKind Kind,
	string? CallableGroupKey = null,
	string? Detail = null);

public sealed record CompletionQueryResult(CompletionQueryContext Context, IReadOnlyList<CompletionCandidate> Candidates);

public static class CompletionQuery
{
	public static CompletionQueryResult Compute(BindingContext context, CompilationUnitSyntax unit, int position, bool inAggregateInitializerValue = false)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(unit);
		ArgumentOutOfRangeException.ThrowIfNegative(position);

var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
		var genericScopeDepth = EnterEnclosingGenericScope(context, unit, position);

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

			// A cursor inside an aggregate literal (`Window { g|`) is a field-initializer position.
			// Offer only the resolved aggregate's own instance fields \u2014 never the ambient globals
			// and functions that an expression position would surface (e.g. `getchar`).
			if (!inAggregateInitializerValue && FindEnclosingInitializer(unit, position) is { } initializer)
				return new CompletionQueryResult(CompletionQueryContext.Initializer, EnumerateInitializerCandidates(initializer, state));

			var visibleMap = BuildVisibleScopeMap(state);
			return new CompletionQueryResult(contextKind, EnumerateIdentifierCandidates(state, visibleMap));
		}
finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
			ExitEnclosingGenericScope(context, genericScopeDepth);
		}
	}

	/// <summary>
	/// Resolves the semantic symbol bound at <paramref name="position"/> using the same binding
	/// state as completion. Returns null when no trustworthy symbol is resolved.
	/// </summary>
	public static ResolvedSymbol? ResolveSymbol(BindingContext context, CompilationUnitSyntax unit, int position)
		=> ResolveSymbol(context, unit, position, includeDocumentationAndType: true);

	/// <summary>
	/// Resolves the semantic symbol bound at <paramref name="position"/> using the same binding
	/// state as completion. Returns null when no trustworthy symbol is resolved. Callers that only
	/// need the symbol's identity and kind (for example semantic-token classification) can pass
	/// <paramref name="includeDocumentationAndType"/> as false to skip the documentation scan and
	/// declared-type resolution that hover-grade results require.
	/// </summary>
	public static ResolvedSymbol? ResolveSymbol(BindingContext context, CompilationUnitSyntax unit, int position, bool includeDocumentationAndType)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(unit);
		ArgumentOutOfRangeException.ThrowIfNegative(position);

		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
		var genericScopeDepth = EnterEnclosingGenericScope(context, unit, position);

		try
		{
			var state = new QueryState(context, unit, position);
			var visible = BuildVisibleScopeMap(state);
			return SymbolResolver.Resolve(context, unit, position, visible, includeDocumentationAndType);
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
			ExitEnclosingGenericScope(context, genericScopeDepth);
		}
	}

	/// <summary>
	/// Binds the type parameters of the generic declaration the position sits inside, for the
	/// duration of one query. The compiler pushes that scope while it declares the template, so the
	/// parameter names already exist as symbols; a query runs long after declaration and would
	/// otherwise see an uninstantiated template body with its own parameters unresolved, which is
	/// what left <c>default(A)</c> and <c>allocator.Alloc</c> untyped.
	/// </summary>
	private static int EnterEnclosingGenericScope(BindingContext context, CompilationUnitSyntax unit, int position)
	{
		var depth = context.GenericParameterScopes.Count;

		foreach (var declaration in EnclosingGenericDeclarations(unit, position))
		{
			context.PushGenericScope(OwnerKind(declaration), OwnerName(declaration), GenericParametersOf(declaration));
		}

		return depth;
	}

	private static void ExitEnclosingGenericScope(BindingContext context, int depth)
	{
		while (context.GenericParameterScopes.Count > depth)
		{
			context.PopGenericScope();
		}
	}

	/// <summary>
	/// The generic declarations enclosing <paramref name="position"/>, outermost first, so they are
	/// entered and left as a stack.
	/// </summary>
	private static IEnumerable<SyntaxNode> EnclosingGenericDeclarations(CompilationUnitSyntax unit, int position)
	{
		var enclosing = new List<SyntaxNode>();

		foreach (var member in TopLevelMembers(unit))
		{
			if (!StrictContains(member.Span, position))
				continue;

			if (GenericParametersOf(member).Count > 0)
				enclosing.Add(member);

			foreach (var nested in NestedGenericDeclarations(member, position))
				enclosing.Add(nested);

			break;
		}

		return enclosing;
	}

	private static IEnumerable<SyntaxNode> TopLevelMembers(CompilationUnitSyntax unit)
		=> unit.NamespaceDeclaration is { } declaration
			? declaration.Members.Cast<SyntaxNode>()
			: unit.Members.Cast<SyntaxNode>();

	private static IEnumerable<SyntaxNode> NestedGenericDeclarations(SyntaxNode member, int position)
	{
		if (member is not ExtensionDeclarationSyntax extension)
			yield break;

		foreach (var nested in extension.Methods)
		{
			if (StrictContains(nested.Span, position) && GenericParametersOf(nested).Count > 0)
				yield return nested;
		}
	}

	private static IReadOnlyList<string> GenericParametersOf(SyntaxNode declaration) => declaration switch
	{
		StructDeclarationSyntax structDeclaration => structDeclaration.GenericParameters,
		UnionDeclarationSyntax unionDeclaration => unionDeclaration.GenericParameters,
		ExtensionDeclarationSyntax extension => extension.GenericParameters,
		FunctionDeclarationSyntax function => function.GenericParameters,
		ProtocolDeclarationSyntax protocol => protocol.GenericParameters,
		InterfaceDeclarationSyntax interfaceDeclaration => interfaceDeclaration.GenericParameters,
		DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.GenericParameters,
		TypeAliasDeclarationSyntax typeAlias => typeAlias.GenericParameters,
		_ => [],
	};

	private static string OwnerKind(SyntaxNode declaration) => declaration switch
	{
		StructDeclarationSyntax => "struct",
		UnionDeclarationSyntax => "union",
		ExtensionDeclarationSyntax => "extension",
		FunctionDeclarationSyntax => "function",
		ProtocolDeclarationSyntax => "protocol",
		InterfaceDeclarationSyntax => "interface",
		DelegateDeclarationSyntax => "delegate",
		TypeAliasDeclarationSyntax => "alias",
		_ => "declaration",
	};

	private static string OwnerName(SyntaxNode declaration) => declaration switch
	{
		StructDeclarationSyntax structDeclaration => structDeclaration.Name,
		UnionDeclarationSyntax unionDeclaration => unionDeclaration.Name,
		ExtensionDeclarationSyntax extension => extension.ExtendedTypeName,
		FunctionDeclarationSyntax function => function.Name,
		ProtocolDeclarationSyntax protocol => protocol.Name,
		InterfaceDeclarationSyntax interfaceDeclaration => interfaceDeclaration.Name,
		DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Name,
		TypeAliasDeclarationSyntax typeAlias => typeAlias.Name,
		_ => "",
	};

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

			// A receiver written with explicit type arguments followed by a dot (Option<int>.) is
			// recovered by the parser as a call whose callee text ends in '.'. Treat it as a member
			// access so the written type is resolved instead of a call that binds to nothing.
			if (current is CallExpressionSyntax trailingCall
				&& trailingCall.FunctionName.EndsWith(".", StringComparison.Ordinal)
				&& position >= trailingCall.Span.End - 1
				&& position <= trailingCall.Span.End)
			{
				member = SyntheticMemberAccess(trailingCall);
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

				// The cursor sits exactly at the end of a type-qualified receiver whose callee text
				// ends in '.', so the recovered call is not strictly contained - detect it here.
				if (child is CallExpressionSyntax trailingChild
					&& trailingChild.FunctionName.EndsWith(".", StringComparison.Ordinal)
					&& position >= trailingChild.Span.End - 1
					&& position <= trailingChild.Span.End)
				{
					member = SyntheticMemberAccess(trailingChild);
					return CompletionQueryContext.Member;
				}

				if (StrictContains(child.Span, position))
					next = child;
			}

			if (next is null)
			{
				foreach (var child in current.GetChildren())
				{
					if (FindTrailingDotCall(child, position) is { } recovered)
					{
						member = SyntheticMemberAccess(recovered);
						return CompletionQueryContext.Member;
					}
				}
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

	/// <summary>
	/// Rebuilds a member access from a call the parser recovered from a type-qualified receiver with
	/// explicit type arguments (<c>Option&lt;int&gt;.</c>). The receiver text is the callee name up to
	/// the trailing dot; it is resolved as a written type by the member-candidate path.
	/// </summary>
	private static MemberAccessExpressionSyntax SyntheticMemberAccess(CallExpressionSyntax call)
	{
		var receiverName = call.FunctionName[..^1];
		var receiverSpan = new TextSpan(call.Span.Start, receiverName.Length);
		return new MemberAccessExpressionSyntax(
			call.Span,
			new IdentifierExpressionSyntax(receiverSpan, receiverName),
			string.Empty);
	}

	/// <summary>
	/// Finds a recovered type-qualified receiver - a call whose callee text ends in '.' - anywhere
	/// under <paramref name="node"/> whose span ends at the cursor. The cursor often lands exactly on
	/// the end of such a node, which the strict containment walk cannot select, so this searches the
	/// subtree instead.
	/// </summary>
	private static CallExpressionSyntax? FindTrailingDotCall(SyntaxNode node, int position)
	{
		if (position < node.Span.Start || position > node.Span.End)
			return null;

		if (node is CallExpressionSyntax call
			&& call.FunctionName.EndsWith(".", StringComparison.Ordinal)
			&& position >= call.Span.End - 1
			&& position <= call.Span.End)
			return call;

		foreach (var child in node.GetChildren())
			if (FindTrailingDotCall(child, position) is { } found)
				return found;

		return null;
	}

	/// <summary>
	/// True for literal expression nodes, which produce no completion candidates.
	/// </summary>
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
			// Extension bodies resolve fields of the extended aggregate as implicit receiver
			// members (`field`, not only `this.field`). Mirror that compiler rule in the
			// completion-only scope so an unqualified field can itself become the receiver
			// of a chained member query such as `converter.`. Locals and parameters are
			// added afterwards and therefore retain the language's normal shadowing rules.
			if (FindEnclosingExtension(state.Unit, state.Position) is { } extension
				&& ResolveTypeOrNull(state, extension.ExtendedTypeName) is { } receiverType)
			{
				switch (receiverType)
				{
					case StructTypeSymbol structType:
						foreach (var field in structType.Fields)
						{
							if (IsFieldVisible(state, structType, field.Visibility))
								root.Add(new ScopedVariable(field.Name, field.Origin, field.Type, extension));
						}
						break;

					case UnionTypeSymbol unionType:
						foreach (var field in unionType.Fields)
						{
							if (IsFieldVisible(state, unionType, field.Visibility))
								root.Add(new ScopedVariable(field.Name, OriginKind.Local, field.Type, extension));
						}
						break;
				}

				root.Add(new ScopedVariable(
					"this",
					OriginKind.Parameter,
					new PointerTypeSymbol(receiverType, isMutable: true),
					extension));
			}

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

	private static ExtensionDeclarationSyntax? FindEnclosingExtension(SyntaxNode node, int position)
	{
		if (node is ExtensionDeclarationSyntax extension && Touching(extension.Span, position))
			return extension;

		foreach (var child in node.GetChildren())
		{
			if (!Touching(child.Span, position))
				continue;

			if (FindEnclosingExtension(child, position) is { } nested)
				return nested;
		}

		return null;
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
		if (node is ConstructorDeclarationSyntax constructor && Touching(constructor.Body.Span, position))
			return constructor.ToFunctionDeclaration();
		if (node is DestructorDeclarationSyntax destructor && Touching(destructor.Body.Span, position))
			return destructor.ToFunctionDeclaration();

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
			frame.Add(new ScopedVariable(v.Name, OriginKind.Local, ResolveVariableType(state, v, frame), v));
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
					frame.Add(new ScopedVariable(v.Name, OriginKind.Local, ResolveVariableType(state, v, frame), v));
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
						child.Add(new ScopedVariable(forStatement.Initializer.Name, OriginKind.Local, ResolveVariableType(state, forStatement.Initializer, frame), forStatement.Initializer));
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

				if (tryStatement.FinallyBody is { } finallyBody)
					WalkBody(finallyBody, frame, state, frames);

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
			if (!entry.Value.Any(fn => !IsReceiverBacked(fn) && !fn.IsAssociated && IsVisible(state, fn.Visibility, fn.DeclaringUnit)))
				continue;

			AddCandidate(candidates, seen, Leaf(entry.Key), CompletionKind.Function, entry.Key);
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

		foreach (var (key, symbol) in state.Context.DelegateTypes)
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

		// A namespace alias declared with `using Alias = Namespace;` is itself usable as the
		// prefix of a qualified name (`Alias.Member`), so it is offered alongside declared
		// namespaces. GetActiveUsings intentionally excludes aliases, hence the separate source.
		foreach (var alias in state.Context.GetNamespaceAliases(state.Unit).Keys)
			labels.Add(alias);

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
		var isTypeNameReceiver = false;
		string? dotted = null;

		if (ExpressionTypeResolver.GetDottedName(receiver) is { } dottedName)
		{
			dotted = dottedName;
			var dotIndex = dottedName.LastIndexOf('.');
			if (dotIndex >= 0)
			{
				type = state.Context.ResolveQualifiedGlobal(dottedName[..dotIndex], dottedName[(dotIndex + 1)..])?.Type;
			}
			else
			{
				type = state.Context.ResolveGlobalReference(dottedName, out _)?.Type;
			}

			// A receiver written as a *written type name* - including one with explicit generic
			// arguments such as Option<int>. - resolves through the same written-type path the
			// binder uses. A value that shadows the name is never re-read as a type.
			if (type is null
				&& !IsValueName(dottedName, visible, state)
				&& ResolveWrittenType(state, dottedName) is { } writtenType
				&& writtenType is StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol)
			{
				type = writtenType;
				isEnumTypeNameReceiver = writtenType is EnumTypeSymbol;
				isTypeNameReceiver = !isEnumTypeNameReceiver;
			}

			// A receiver written as a *type* name (Layout. / System.Memory.Layout.) selects the
			// associated callables of that owner. A receiver that resolves to a value is never
			// re-read as a type name, so a local that shadows a type name stays a value receiver.
			// The owner is resolved semantically by walking the receiver's own prefixes, so a
			// qualified owner works even when the type was already found above by a bare lookup.
			if (!isEnumTypeNameReceiver
				&& !IsValueName(dottedName, visible, state)
				&& TryResolveOwnerType(state, dottedName, out var ownerType))
			{
				type = ownerType;
				isTypeNameReceiver = true;
			}
		}

		// A namespace receiver completes to the namespace's own declarations (free functions,
		// types, nested namespaces) using the compiler's namespace/using semantics. An
		// unresolved namespace yields no members - never a global/keyword fallback.
		if (type is null && !isEnumTypeNameReceiver && dotted is not null && TryResolveNamespace(state, dotted, visible, out var namespaceName))
			return EnumerateNamespaceMembers(namespaceName, state);

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
				if (!isTypeNameReceiver)
					AddExtensionMethods(candidates, type, state);
				break;

			case UnionTypeSymbol unionType:
				foreach (var field in unionType.Fields)
				{
					if (!IsFieldVisible(state, unionType, field.Visibility))
						continue;
					AddCandidate(candidates, field.Name, field.Name, CompletionKind.UnionVariant);
				}

				if (isTypeNameReceiver)
					AddAssociatedFunctions(candidates, type, state);
				else
					AddExtensionMethods(candidates, type, state);
				break;

			case StructTypeSymbol structType:
				if (!isTypeNameReceiver)
				{
					foreach (var field in structType.Fields)
					{
						if (!IsFieldVisible(state, structType, field.Visibility))
							continue;
						AddCandidate(candidates, field.Name, field.Name, CompletionKind.StructField);
					}
				}

				if (isTypeNameReceiver)
					AddAssociatedFunctions(candidates, type, state);
				else
					AddExtensionMethods(candidates, type, state);
				break;

			default:
				if (type is SliceTypeSymbol or ArrayTypeSymbol || type.Name.EndsWith("[]"))
					AddCandidate(candidates, "Length", "Length", CompletionKind.ArrayLength);
				break;
		}

		return candidates;
	}

	/// <summary>
	/// Whether a written receiver name denotes a value (completion-visible local/parameter or a
	/// declared global) rather than a type name. Value receivers never gain associated callables.
	/// </summary>
	private static bool IsValueName(string dottedName, Dictionary<string, ScopedVariable> visible, QueryState state)
	{
		var leaf = dottedName[(dottedName.LastIndexOf('.') + 1)..];
		return visible.ContainsKey(leaf) || state.Context.ResolveGlobalReference(leaf, out _) is not null;
	}

	/// <summary>
	/// Resolves the longest written prefix of a dotted receiver that names a struct, union, or
	/// enum type, using the compiler's own type lookup (current namespace, then active usings).
	/// The whole receiver is tried first because at <c>Owner.</c> the owner is the entire written
	/// name — a bare owner has no member separator, and a generic owner
	/// (<c>Result&lt;int, MyError&gt;.</c>) has none outside its argument list.
	/// </summary>
	private static bool TryResolveOwnerType(QueryState state, string dottedName, out TypeSymbol ownerType)
	{
		ownerType = null!;

		var dot = LastTopLevelDotIndex(dottedName);
		for (var end = dot < 0 ? dottedName.Length : dot; end > 0;)
		{
			if (state.Context.TryResolveTypeByLookupName(dottedName[..end], state.Unit, out var resolved)
				&& resolved is StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol)
			{
				ownerType = resolved;
				return true;
			}

			dot = LastTopLevelDotIndex(dottedName[..end]);
			if (dot < 0)
				return false;

			end = dot;
		}

		return false;
	}

	/// <summary>
	/// Index of the last '.' that is not inside a generic argument list, or -1 when there is none.
	/// A qualified owner may itself contain dots (<c>System.Memory.Layout</c>, and the qualified
	/// names inside <c>Result&lt;int, MyError&gt;</c>), so only a top-level dot separates owner from
	/// member.
	/// </summary>
	private static int LastTopLevelDotIndex(string name)
	{
		var depth = 0;
		var last = -1;

		for (var i = 0; i < name.Length; i++)
		{
			switch (name[i])
			{
				case '<':
					depth++;
					break;
				case '>':
					if (depth > 0)
						depth--;
					break;
				case '.' when depth == 0:
					last = i;
					break;
			}
		}

		return last;
	}

	/// <summary>
	/// Resolves a (possibly dotted) receiver name to a declared namespace, first as written, then
	/// through the current unit's namespace aliases, then through its active <c>using</c> namespaces.
	/// Returns false when the name does not denote a namespace.
	/// </summary>
	private static bool TryResolveNamespace(QueryState state, string dotted, Dictionary<string, ScopedVariable> visible, out string namespaceName)
	{
		if (IsNamespace(state, dotted))
		{
			namespaceName = dotted;
			return true;
		}

		// 'using Alias = Namespace.Path;' makes Alias a namespace prefix and nothing more, so it
		// completes to the target's members - never to the alias' own scope. A local that already
		// owns the spelling keeps the value path, matching the compiler's resolution order.
		if (!IsValueName(dotted, visible, state)
			&& state.Context.GetNamespaceAliases(state.Unit).TryGetValue(dotted, out var alias)
			&& IsNamespace(state, alias.NamespaceName))
		{
			namespaceName = alias.NamespaceName;
			return true;
		}

		foreach (var active in state.Context.GetActiveUsings(state.Unit))
		{
			var candidate = active + "." + dotted;
			if (IsNamespace(state, candidate))
			{
				namespaceName = candidate;
				return true;
			}
		}

		namespaceName = string.Empty;
		return false;
	}

	/// <summary>
	/// Whether <paramref name="candidate"/> denotes a namespace: it is declared, or it is the
	/// dotted prefix of a declared global/function-template/type. Uses the compiler's own tables.
	/// </summary>
	private static bool IsNamespace(QueryState state, string candidate)
	{
		if (state.Context.DeclaredNamespaces.Contains(candidate))
			return true;

		var prefix = candidate + ".";
		return state.Context.OverloadedFunctions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.AssociatedFunctions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.GenericFunctionTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.GenericExtensionMethodTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.InterfaceFunctionTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.ProtocolFunctionTemplates.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.StructTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.UnionTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.EnumTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.InterfaceTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
			|| state.Context.ProtocolTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
	}

	/// <summary>
	/// Enumerates the declarations directly inside a namespace: free functions, function
	/// templates, named types and nested namespaces. Uses the compiler's own symbol tables and
	/// visibility rules; no name is hardcoded.
	/// </summary>
	private static IReadOnlyList<CompletionCandidate> EnumerateNamespaceMembers(string namespaceName, QueryState state)
	{
		var candidates = new List<CompletionCandidate>();
		var seen = new HashSet<(string Label, CompletionKind Kind)>();
		var prefix = namespaceName + ".";

		foreach (var (key, overloads) in state.Context.OverloadedFunctions)
		{
			if (!IsDirectNamespaceChild(key, prefix))
				continue;
			if (!overloads.Any(fn => IsVisible(state, fn.Visibility, fn.DeclaringUnit)))
				continue;
			AddCandidate(candidates, seen, key[prefix.Length..], CompletionKind.Function, key);
		}

		foreach (var (key, declaration) in state.Context.GenericFunctionTemplates)
			AddNamespaceFunctionTemplate(candidates, seen, key, prefix, declaration, state);
		foreach (var (key, declaration) in state.Context.InterfaceFunctionTemplates)
			AddNamespaceFunctionTemplate(candidates, seen, key, prefix, declaration, state);
		foreach (var (key, declaration) in state.Context.ProtocolFunctionTemplates)
			AddNamespaceFunctionTemplate(candidates, seen, key, prefix, declaration, state);

		AddNamespaceTypeCandidates(candidates, seen, prefix, state.Context.StructTypes, state);
		AddNamespaceTypeCandidates(candidates, seen, prefix, state.Context.UnionTypes, state);
		AddNamespaceTypeCandidates(candidates, seen, prefix, state.Context.EnumTypes, state);
		AddNamespaceTypeCandidates(candidates, seen, prefix, state.Context.InterfaceTypes, state);
		AddNamespaceTypeCandidates(candidates, seen, prefix, state.Context.ProtocolTypes, state);

		foreach (var declared in state.Context.DeclaredNamespaces)
		{
			if (IsDirectNamespaceChild(declared, prefix))
				AddCandidate(candidates, seen, declared[prefix.Length..], CompletionKind.Namespace);
		}

		return candidates;
	}

	private static void AddNamespaceFunctionTemplate(
		List<CompletionCandidate> candidates,
		HashSet<(string Label, CompletionKind Kind)> seen,
		string key,
		string prefix,
		FunctionDeclarationSyntax declaration,
		QueryState state)
	{
		if (!IsDirectNamespaceChild(key, prefix))
			return;

		var declaringUnit = state.Context.SymbolUnits.TryGetValue(key, out var unit) ? unit : null;
		if (!IsVisible(state, declaration.Visibility, declaringUnit))
			return;

		AddCandidate(candidates, seen, key[prefix.Length..], CompletionKind.Function, key);
	}

	private static void AddNamespaceTypeCandidates<T>(
		List<CompletionCandidate> candidates,
		HashSet<(string Label, CompletionKind Kind)> seen,
		string prefix,
		IReadOnlyDictionary<string, T> types,
		QueryState state) where T : TypeSymbol
	{
		foreach (var (key, symbol) in types)
		{
			if (!IsDirectNamespaceChild(key, prefix))
				continue;
			if (!IsVisible(state, symbol.Visibility, GetDeclaringUnit(state, symbol)))
				continue;
			AddCandidate(candidates, seen, key[prefix.Length..], CompletionKind.Type);
		}
	}

	private static bool IsDirectNamespaceChild(string mangledName, string prefix)
	{
		if (!mangledName.StartsWith(prefix, StringComparison.Ordinal))
			return false;

		return mangledName.IndexOf('.', prefix.Length) < 0;
	}

	private static void AddExtensionMethods(List<CompletionCandidate> candidates, TypeSymbol type, QueryState state)
	{
		// Preserve the exact overload-group key selected by the compiler. Rich completion must
		// never rebuild a member overload set from the short source name because unrelated
		// receiver types can legitimately expose methods with the same spelling.
		var visibleMethods = new SortedDictionary<string, string?>(StringComparer.Ordinal);
		foreach (var (memberName, function) in state.Context.GetExtensionMethodCandidates(type, state.Unit))
		{
			if (!IsVisible(state, function.Visibility, function.DeclaringUnit))
				continue;

			visibleMethods.TryAdd(memberName, FindCallableGroupKey(state.Context, function));
		}

		foreach (var (methodName, groupKey) in visibleMethods)
			candidates.Add(new CompletionCandidate(methodName, methodName, CompletionKind.Method, groupKey));
	}

	/// <summary>
	/// Adds the associated (receiverless) callables of a type written as a receiver. Associated
	/// functions are offered only for a type receiver; a value receiver never sees them.
	/// </summary>
	private static void AddAssociatedFunctions(List<CompletionCandidate> candidates, TypeSymbol type, QueryState state)
	{
		var visibleFunctions = new SortedDictionary<string, string?>(StringComparer.Ordinal);
		foreach (var (memberName, function) in state.Context.GetAssociatedFunctionCandidates(type, state.Unit))
		{
			// Operators are associated callables but are never named in source: they are reached
			// through the operator token in an expression, so offering their mangled token
			// (`op_add`) as a member label would be both misleading and non-insertable.
			if (OperatorTokens.IsOperatorToken(memberName))
				continue;

			if (!IsVisible(state, function.Visibility, function.DeclaringUnit))
				continue;

			visibleFunctions.TryAdd(memberName, FindCallableGroupKey(state.Context, function));
		}

		// An associated member that declares its own type parameters (`Layout .FromType<T>()`) is
		// parked as a template on its owner instead of entering the associated overload table, so it
		// is absent from the loop above. It is still a callable the reader can write at this cursor,
		// so it is offered from the template table by its source name.
		var genericDetails = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var template in state.Context.GetGenericExtensionMethodTemplates(type, state.Unit))
		{
			var method = template.Method;
			if (!method.IsAssociated || OperatorTokens.IsOperatorToken(method.Name))
				continue;

			if (!IsVisible(state, method.SyntacticVisibility ?? Visibility.Internal, state.Unit))
				continue;

			visibleFunctions.TryAdd(method.Name, template.BaseMangledName);
			genericDetails[method.Name] = AssociatedSignatureOf(method);
		}

		foreach (var (functionName, groupKey) in visibleFunctions)
		{
			genericDetails.TryGetValue(functionName, out var detail);
			candidates.Add(new CompletionCandidate(functionName, functionName, CompletionKind.Method, groupKey, detail));
		}
	}

	/// <summary>
	/// The signature shown for an associated member that declares its own type parameters
	/// (`FromType&lt;T&gt;()`). Its parameter and return types are not resolved yet - the template is
	/// kept generic precisely so the IDE need not instantiate it - so the display names the method
	/// and its type parameters, which is what a reader needs to recognise the call.
	/// </summary>
	private static string AssociatedSignatureOf(FunctionDeclarationSyntax method)
	{
		var typeParameters = method.GenericParameters.Count == 0
			? string.Empty
			: $"<{string.Join(", ", method.GenericParameters)}>";
		return $"{method.Name}{typeParameters}()";
	}

	private static string? FindCallableGroupKey(BindingContext context, FunctionSymbol function)
	{
		foreach (var (key, overloads) in context.OverloadedFunctions)
		{
			if (overloads.Any(candidate => ReferenceEquals(candidate, function)))
				return key;
		}

		foreach (var (key, overloads) in context.AssociatedFunctions)
		{
			if (overloads.Any(candidate => ReferenceEquals(candidate, function)))
				return key;
		}

		return null;
	}

	private static bool IsReceiverBacked(FunctionSymbol function) =>
		function.Parameters.Count > 0 && string.Equals(function.Parameters[0].Name, "this", StringComparison.Ordinal);

	private static void AddCandidate(
		List<CompletionCandidate> candidates,
		HashSet<(string Label, CompletionKind Kind)> seen,
		string label,
		CompletionKind kind,
		string? callableGroupKey = null)
	{
		if (!seen.Add((label, kind)))
			return;
		candidates.Add(new CompletionCandidate(label, label, kind, callableGroupKey));
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

	/// <summary>
	/// Resolves a receiver written as a type name, tolerating explicit generic arguments. A receiver
	/// such as <c>Option&lt;int&gt;.</c> carries type arguments that the plain name lookup may not
	/// accept, so when the full name does not resolve, retry with the arguments stripped to reach the
	/// generic template's members.
	/// </summary>
	private static TypeSymbol? ResolveWrittenType(QueryState state, string name)
	{
		if (state.Context.ResolveType(state.Context.NormalizeGenericName(name)) is { } resolved)
			return resolved;

		var genericIndex = name.IndexOf('<');
		if (genericIndex > 0)
			return state.Context.ResolveType(state.Context.NormalizeGenericName(name[..genericIndex]));

		return null;
	}

	/// <summary>
	/// Finds the innermost aggregate literal whose braces contain <paramref name="position"/>, so a
	/// cursor between <c>{</c> and <c>}</c> is treated as a field-initializer position rather than a
	/// free expression. The type-name prefix guard keeps a cursor on the type name itself (for
	/// example <c>Wi|ndow {</c>) out of the initializer context.
	/// </summary>
	private static StructInitializationExpressionSyntax? FindEnclosingInitializer(SyntaxNode node, int position)
	{
		foreach (var child in node.GetChildren())
		{
			if (!Touching(child.Span, position))
				continue;

			if (child is MemberInitializerSyntax member)
			{
				// Inside a member initializer the cursor is either in the field-name region (the
				// enclosing aggregate literal owns the completion) or in the value region (an
				// ordinary expression position, which may itself hold a nested aggregate literal).
				if (Touching(member.Expression.Span, position))
					return FindEnclosingInitializer(member.Expression, position);

				return node as StructInitializationExpressionSyntax;
			}

			var nested = FindEnclosingInitializer(child, position);
			if (nested is not null)
				return nested;
		}

		if (node is StructInitializationExpressionSyntax initializer
			&& position >= initializer.Span.Start + initializer.StructTypeName.Length)
		{
			return initializer;
		}

		return null;
	}

	/// <summary>
	/// Offers only the eligible instance fields of the aggregate being initialized. Supplied fields
	/// are excluded, accessibility is honored, and no ambient globals or functions leak in. The
	/// insertion text appends <c> = </c> so the field value can be filled immediately.
	/// </summary>
	private static IReadOnlyList<CompletionCandidate> EnumerateInitializerCandidates(
		StructInitializationExpressionSyntax initializer, QueryState state)
	{
		var candidates = new List<CompletionCandidate>();
		var type = ResolveTypeOrNull(state, initializer.StructTypeName);
		if (type is null)
			return candidates;

		var supplied = new HashSet<string>(StringComparer.Ordinal);
		foreach (var member in initializer.Initializers)
			supplied.Add(member.MemberName);

		var seen = new HashSet<string>(StringComparer.Ordinal);

		switch (type)
		{
			case StructTypeSymbol structType:
				foreach (var field in structType.Fields)
				{
					if (supplied.Contains(field.Name) || !IsFieldVisible(state, structType, field.Visibility))
						continue;
					if (!seen.Add(field.Name))
						continue;
					candidates.Add(new CompletionCandidate(
						field.Name, $"{field.Name} = ", CompletionKind.StructField, null,
						$"{field.Type.Name} {structType.Name}.{field.Name}"));
				}
				break;

			case UnionTypeSymbol unionType:
				foreach (var field in unionType.Fields)
				{
					if (field.IsVoidVariant || supplied.Contains(field.Name) || !IsFieldVisible(state, unionType, field.Visibility))
						continue;
					if (!seen.Add(field.Name))
						continue;
					candidates.Add(new CompletionCandidate(
						field.Name, $"{field.Name} = ", CompletionKind.UnionVariant, null,
						$"{field.Type.Name} {unionType.Name}.{field.Name}"));
				}
				break;
		}

		return candidates;
	}

	private static TypeSymbol? ResolveVariableType(QueryState state, VariableDeclarationSyntax v, List<ScopedVariable> frame)
	{
		if (state.Context.VariableSymbols.TryGetValue(v, out var symbol) && symbol.Type is not null)
			return symbol.Type;

		if (!string.IsNullOrWhiteSpace(v.Type))
			return ResolveTypeOrNull(state, v.Type);

		// A `val` written without a type takes its type from the initializer. Inside an
		// uninstantiated template body the compiler emitted no variable symbol at all, so without
		// this `default(A)` types nothing and every member written on the result stays unreachable.
		var preceding = new Dictionary<string, ScopedVariable>(StringComparer.Ordinal);
		foreach (var declared in frame)
			preceding[declared.Name] = declared;

		return ExpressionTypeResolver.Resolve(state.Context, preceding, v.Initializer);
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
