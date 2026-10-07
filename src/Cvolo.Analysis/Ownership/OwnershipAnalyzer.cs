using Cvolo.Analysis.Passes.Safety;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Ownership;

/// <summary>
/// Flow-sensitive ownership analysis. It tracks the availability of owned projections as it walks
/// a function body and binds one <see cref="ResolvedAssignmentOperation"/> per assignment whose
/// destination may contain an owned resource-move value. Code generation consumes those decisions
/// and must never rediscover ownership legality, cleanup plans, or overlap.
/// </summary>
internal sealed class OwnershipAnalyzer(BindingContext context)
{
	private readonly SafetyExpressionFacts _facts = new(context);
	private readonly Dictionary<MovePath, OwnershipState> _state = new();

	public void Analyze(FunctionDeclarationSyntax func)
	{
		if (!func.HasBody)
			return;

		_state.Clear();
		var scope = new SymbolTable(context.Globals);

		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type is null)
				continue;

			scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
			if (OwnershipTypeFacts.NeedsDestruction(type, context))
				_state[new MovePath(param.Name)] = OwnershipState.Live;
		}

		AnalyzeBlock(func.Body, scope);
	}

	private void AnalyzeBlock(BlockStatementSyntax? block, SymbolTable scope)
	{
		if (block is null)
			return;

		foreach (var stmt in block.Statements)
			AnalyzeStatement(stmt, scope);
	}

	private void AnalyzeStatement(SyntaxNode stmt, SymbolTable scope)
	{
		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				AnalyzeDeclaration(v, scope);
				break;

			case IfStatementSyntax i:
				AnalyzeExpression(i.Condition, scope);
				// Preserve the existing MVP sequential branch model.
				AnalyzeStatement(i.ThenStatement, scope);
				if (i.ElseClause is not null)
					AnalyzeStatement(i.ElseClause.Body, scope);
				break;

			case ExpressionStatementSyntax e:
				AnalyzeExpression(e.Expression, scope);
				break;

			case ReturnStatementSyntax r:
				if (r.Expression is not null)
					AnalyzeExpression(r.Expression, scope);
				break;

			case BlockStatementSyntax b:
				AnalyzeBlock(b, new SymbolTable(scope));
				break;

			case LabeledBlockStatementSyntax lb:
				AnalyzeBlock(lb.Body, new SymbolTable(scope));
				break;
		}
	}

	private void AnalyzeDeclaration(VariableDeclarationSyntax v, SymbolTable scope)
	{
		if (!context.VariableSymbols.TryGetValue(v, out var symbol))
			return;

		scope.Declare(symbol);
		var destroys = OwnershipTypeFacts.NeedsDestruction(symbol.Type, context);
		var root = new MovePath(symbol.Name);

		if (v.Initializer is null)
		{
			if (destroys)
				_state[root] = OwnershipState.Uninitialized;
			return;
		}

		AnalyzeExpression(v.Initializer, scope);

		if (!destroys)
			return;

		// Initializing an owned local from an owned source path transfers ownership (a move).
		var sourcePath = MovePathBuilder.TryBuild(v.Initializer);
		if (sourcePath is not null && OwnsDestructibleValue(sourcePath, scope))
		{
			if (HasDestructorAncestor(v.Initializer, scope))
			{
				Report(v, $"Cannot move '{sourcePath}' out of an aggregate with a user-defined destructor.",
					DiagnosticIds.PartialMoveFromDestructorAggregate);
			}
			else
			{
				MarkMoved(sourcePath);
				if (sourcePath.Depth > 0)
					context.ResolvedDeclarationMoves[v] = sourcePath;
			}
		}

		_state[root] = OwnershipState.Live;
	}

	private void AnalyzeExpression(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case BinaryExpressionSyntax bin when bin.Operator == "=":
				AnalyzeAssignment(bin, scope);
				break;

			case CallExpressionSyntax call:
				AnalyzeCall(call, scope);
				break;

			case BinaryExpressionSyntax bin:
				AnalyzeExpression(bin.Left, scope);
				AnalyzeExpression(bin.Right, scope);
				break;

			case MemberAccessExpressionSyntax m:
				AnalyzeExpression(m.Expression, scope);
				break;

			case IndexExpressionSyntax idx:
				AnalyzeExpression(idx.Left, scope);
				AnalyzeExpression(idx.Index, scope);
				break;

			case UnaryExpressionSyntax u:
				if (u.Operand is not null)
					AnalyzeExpression(u.Operand, scope);
				break;

			case BorrowExpressionSyntax b:
				AnalyzeExpression(b.Expression, scope);
				break;
		}
	}

	private void AnalyzeAssignment(BinaryExpressionSyntax bin, SymbolTable scope)
	{
		AnalyzeExpression(bin.Right, scope);

		var destType = ResolveType(bin.Left, scope);
		if (destType is null)
		{
			AnalyzeExpression(bin.Left, scope);
			return;
		}

		var destroys = OwnershipTypeFacts.NeedsDestruction(destType, context);
		if (!destroys)
			return;

		// Untracked raw-pointer boundary: an ownership transfer through a dereference cannot be
		// tracked, so it is rejected deterministically instead of guessing a tracked root.
		if (bin.Left is UnaryExpressionSyntax { Operator: "*" } || bin.Right is UnaryExpressionSyntax { Operator: "*" })
		{
			Report(bin, "Cannot perform an ownership transfer through a raw pointer dereference.",
				DiagnosticIds.UnknownAliasedMoveAssignment);
			return;
		}

		var destPath = MovePathBuilder.TryBuild(bin.Left);
		var sourcePath = MovePathBuilder.TryBuild(bin.Right);
		var sourceIsMove = sourcePath is not null && OwnsDestructibleValue(sourcePath, scope);

		// Dynamic-index destination: a non-constant index into an aggregate of owned elements does
		// not designate a stable identity, so the write is rejected until region analysis can prove
		// every candidate element is live and pre-store stable.
		if (destPath is null && bin.Left is IndexExpressionSyntax dynamicIndex
			&& ResolveType(dynamicIndex.Left, scope) is ArrayTypeSymbol
			&& !MovePathBuilder.TryGetConstantIndex(dynamicIndex.Index, out _))
		{
			Report(bin, "Dynamic-index ownership assignment is not supported: the target element is not statically identifiable.",
				DiagnosticIds.DynamicIndexOwnershipUnsupported);
			return;
		}

		var destState = ComputeDestinationState(destPath);
		var relation = MovePathRelation.None;

		if (sourceIsMove && destPath is not null && sourcePath is not null)
		{
			relation = MovePath.Classify(destPath, sourcePath);
			ReportOverlap(bin, relation);
		}

		if (HasDestructorAncestor(bin.Right, scope) && sourceIsMove)
		{
			Report(bin, $"Cannot move '{sourcePath}' out of an aggregate with a user-defined destructor.",
				DiagnosticIds.PartialMoveFromDestructorAggregate);
			return;
		}

		var isResourceUnion = destType is UnionTypeSymbol unionType && OwnershipTypeFacts.IsResourceOwningUnion(unionType, context);
		var plan = PlanCleanup(isResourceUnion, destState);

		context.ResolvedAssignments[bin] = new ResolvedAssignmentOperation(
			bin.Left,
			destType,
			destPath,
			null,
			destState,
			plan,
			isResourceUnion ? ReplacementKind.TagAwareUnionReplace : ReplacementKind.Ordinary,
			sourceIsMove ? SourceOwnershipAction.Move : SourceOwnershipAction.Copy,
			sourcePath,
			relation);

		if (destPath is not null)
			_state[destPath] = OwnershipState.Live;

		if (sourceIsMove && sourcePath is not null)
			MarkMoved(sourcePath);
	}

	private void AnalyzeCall(CallExpressionSyntax call, SymbolTable scope)
	{
		context.ResolvedCalls.TryGetValue(call, out var func);

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var argument = call.Arguments[i];
			if (func is not null && i < func.Parameters.Count)
			{
				var parameter = func.Parameters[i];
				var byValue = parameter.Type is not PointerTypeSymbol;
				var path = byValue ? MovePathBuilder.TryBuild(argument) : null;
				if (path is not null && OwnsDestructibleValue(path, scope))
					MarkMoved(path);
			}

			AnalyzeExpression(argument, scope);
		}
	}

	private DestinationOwnershipState ComputeDestinationState(MovePath? path)
	{
		if (path is null)
			return DestinationOwnershipState.Unavailable;

		return ComputeWholeState(path) switch
		{
			OwnershipState.Live => DestinationOwnershipState.WholeLive,
			OwnershipState.Partial => DestinationOwnershipState.Partial,
			_ => DestinationOwnershipState.Unavailable
		};
	}

	private OwnershipState ComputeWholeState(MovePath path)
	{
		var hasDirect = _state.TryGetValue(path, out var direct);
		var descendants = new List<OwnershipState>();
		foreach (var entry in _state)
			if (path.IsStrictAncestorOf(entry.Key))
				descendants.Add(entry.Value);

		if (descendants.Count == 0)
			return hasDirect ? direct : InheritFromAncestor(path);

		if (hasDirect && direct is OwnershipState.Moved or OwnershipState.Disposed)
			return direct;

		var anyLive = (hasDirect && direct == OwnershipState.Live) || descendants.Contains(OwnershipState.Live);
		var anyUnavailable = descendants.Exists(state => state != OwnershipState.Live);

		if (anyLive && anyUnavailable)
			return OwnershipState.Partial;
		if (anyLive)
			return OwnershipState.Live;
		return OwnershipState.Uninitialized;
	}

	private OwnershipState InheritFromAncestor(MovePath path)
	{
		MovePath? nearest = null;
		foreach (var key in _state.Keys)
			if (key.IsStrictAncestorOf(path) && (nearest is null || key.Depth > nearest.Depth))
				nearest = key;

		if (nearest is null)
			return OwnershipState.Uninitialized;

		return ComputeWholeState(nearest) switch
		{
			OwnershipState.Live or OwnershipState.Partial => OwnershipState.Live,
			var other => other
		};
	}

	private static DestinationCleanupPlan PlanCleanup(bool isResourceUnion, DestinationOwnershipState destState)
	{
		if (isResourceUnion)
			return destState == DestinationOwnershipState.Unavailable
				? DestinationCleanupPlan.None
				: DestinationCleanupPlan.TagAwareUnionCleanup;

		return destState switch
		{
			DestinationOwnershipState.WholeLive => DestinationCleanupPlan.WholeLive,
			DestinationOwnershipState.Partial => DestinationCleanupPlan.ProjectionAware,
			_ => DestinationCleanupPlan.None
		};
	}

	private void MarkMoved(MovePath path) => _state[path] = OwnershipState.Moved;

	private bool OwnsDestructibleValue(MovePath path, SymbolTable scope)
	{
		var type = ResolvePathType(path, scope);
		return type is not null && OwnershipTypeFacts.NeedsDestruction(type, context);
	}

	/// <summary>
	/// Resolves the semantic type of a move path by walking the expression form it was built from.
	/// Only the stable forms participate; anything else yields <c>null</c> (untracked).
	/// </summary>
	private TypeSymbol? ResolveType(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				if (scope.Lookup(id.Name) is VariableSymbol variable)
					return variable.Type;
				return context.ResolveGlobalReference(id.Name, out _)?.Type;

			case MemberAccessExpressionSyntax m:
				var baseType = ResolveType(m.Expression, scope);
				return baseType switch
				{
					StructTypeSymbol s => s.FindField(m.MemberName)?.Type,
					UnionTypeSymbol u => u.Fields.FirstOrDefault(f => f.Name == m.MemberName)?.Type,
					PointerTypeSymbol p when p.ReferencedType is StructTypeSymbol ps => ps.FindField(m.MemberName)?.Type,
					_ => null
				};

			case IndexExpressionSyntax idx:
				var container = ResolveType(idx.Left, scope);
				return container switch
				{
					ArrayTypeSymbol array => array.ElementType,
					SliceTypeSymbol slice => slice.ElementType,
					_ => null
				};

			case BorrowExpressionSyntax b:
				return ResolveType(b.Expression, scope);

			case UnaryExpressionSyntax u when u.Operator == "*":
				return ResolveType(u.Operand, scope) is PointerTypeSymbol pointer ? pointer.ReferencedType : null;

			default:
				return null;
		}
	}

	private TypeSymbol? ResolvePathType(MovePath path, SymbolTable scope)
	{
		// Re-resolve via a synthetic expression walk is unnecessary: the state does not store types,
		// so resolve the root and project through the recorded steps.
		var type = scope.Lookup(path.Root) is VariableSymbol variable
			? variable.Type
			: context.ResolveGlobalReference(path.Root, out _)?.Type;

		if (type is null)
			return null;

		foreach (var step in path.Steps)
		{
			type = step.Kind == ProjectionStepKind.Field
				? type switch
				{
					StructTypeSymbol s => s.FindField(step.Name!)?.Type,
					UnionTypeSymbol u => u.Fields.FirstOrDefault(f => f.Name == step.Name)?.Type,
					_ => null
				}
				: type switch
				{
					ArrayTypeSymbol array => array.ElementType,
					SliceTypeSymbol slice => slice.ElementType,
					_ => null
				};

			if (type is null)
				return null;
		}

		return type;
	}

	/// <summary>
	/// True when any container along the l-value chain is a struct with a user-defined destructor,
	/// which forbids moving an owned field out (the destructor's obligation could not be
	/// discharged).
	/// </summary>
	private bool HasDestructorAncestor(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case MemberAccessExpressionSyntax m:
				var baseType = ResolveType(m.Expression, scope);
				if (baseType is StructTypeSymbol s && OwnershipTypeFacts.HasUserDestructor(s, context))
					return true;
				return HasDestructorAncestor(m.Expression, scope);

			case IndexExpressionSyntax idx:
				return HasDestructorAncestor(idx.Left, scope);

			default:
				return false;
		}
	}

	private void ReportOverlap(BinaryExpressionSyntax bin, MovePathRelation relation)
	{
		if (relation == MovePathRelation.Identical)
		{
			Report(bin, "A resource value cannot be moved into itself.", DiagnosticIds.SelfMoveAssignment);
		}
		else if (relation == MovePathRelation.AncestorDescendantOverlap)
		{
			Report(bin, "A resource value cannot be moved into an overlapping part of itself.",
				DiagnosticIds.OverlappingMoveAssignment);
		}
	}

	private void Report(SyntaxNode node, string message, string diagnosticId)
	{
		var unit = context.CurrentUnit;
		if (unit is null)
			return;

		context.Diagnostics.Report(context.FileContexts[unit], node.Span, message, diagnosticId);
	}
}
