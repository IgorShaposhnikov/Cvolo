using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns safe-delegate provenance state and expression classification, including propagation through
/// delegate declarations and reassignments while escape policy remains in <see cref="SafeDelegateAnalyzer"/>.
/// </summary>
internal sealed class SafeDelegateProvenanceTracker(
	BindingContext context,
	LambdaCaptureAnalyzer lambdaCaptures,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>
	/// Delegate provenance records for delegate-typed variables declared so far in the current
	/// function. Copy assignments propagate the carried closure or receiver context.
	/// </summary>
	private readonly Dictionary<string, DelegateProvenance> _delegateProvenances = [];

	/// <summary>Names of delegate-typed locals/globals whose context must not escape.</summary>
	private readonly Dictionary<string, DelegateProvenance> _nonEscapingDelegates = [];

	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;

	internal enum DelegateProvenanceKind
	{
		Free,       // context == null (free function / non-capturing lambda): may escape
		Capturing,  // context == &closureEnv: may NOT escape (CVL1318)
		RefBorrow,  // context == &closureEnv holding ref borrows: may NOT escape (CVL1316)
		BoundMethod,// context == &receiver: may escape only as far as the receiver (CVL1321/CVL1319)
	}

	internal sealed record DelegateProvenance(
		DelegateProvenanceKind Kind,
		IReadOnlyList<string> CapturedNames,
		string? ReceiverBase);

	/// <summary>Clears per-function delegate provenance while preserving the existing state model.</summary>
	public void Reset()
	{
		_delegateProvenances.Clear();
	}

	/// <summary>
	/// Records declaration-time provenance for a delegate-typed local after its initializer has
	/// already been checked by the enclosing safety traversal.
	/// </summary>
	public void TrackDeclaration(VariableDeclarationSyntax declaration, VariableSymbol symbol, SymbolTable scope)
	{
		if (symbol.Type is DelegateTypeSymbol && declaration.Initializer != null)
			_delegateProvenances[declaration.Name] = GetDelegateProvenance(declaration.Initializer, scope);
	}

	/// <summary>Propagates delegate provenance through reassignment of a delegate-typed variable.</summary>
	public void TrackAssignment(BinaryExpressionSyntax assignment, SymbolTable scope)
	{
		if (_getBaseIdentifierName(assignment.Left) is { } lhsName
			&& scope.Lookup(lhsName) is VariableSymbol reSym
			&& reSym.Type is DelegateTypeSymbol)
		{
			_delegateProvenances[lhsName] = GetDelegateProvenance(assignment.Right, scope);
		}
	}

	/// <summary>
	/// Computes the delegate provenance (context) of a delegate value expression. Capturing/ref
	/// lambdas carry closure environments, while bound methods carry receiver context.
	/// </summary>
	public DelegateProvenance GetDelegateProvenance(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case LambdaExpressionSyntax lam:
				var captures = lambdaCaptures.ComputeCapturedNames(lam, scope);
				if (captures.Count == 0)
					return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
				return new DelegateProvenance(
					lam.CaptureMode == LambdaCaptureMode.Ref ? DelegateProvenanceKind.RefBorrow : DelegateProvenanceKind.Capturing,
					[.. captures], null);
			case MemberAccessExpressionSyntax ma:
				// A delegate field or method group bound to a receiver: context == &receiver.
				// Only classify as a bound delegate value when the accessed member is genuinely a
				// delegate-typed field or a method group; a plain member read (e.g. 's.Id' where
				// Id is an int field) is not a delegate value.
				var memberReceiverType = _resolveExpressionType(ma.Expression, scope);
				if (memberReceiverType is PointerTypeSymbol memberReceiverPtr)
					memberReceiverType = memberReceiverPtr.ReferencedType;
				if (memberReceiverType is StructTypeSymbol receiverStruct && receiverStruct.FindField(ma.MemberName)?.Type is DelegateTypeSymbol)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], _getBaseIdentifierName(ma.Expression));
				if (memberReceiverType is UnionTypeSymbol receiverUnion && receiverUnion.FindField(ma.MemberName)?.Type is DelegateTypeSymbol)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], _getBaseIdentifierName(ma.Expression));
				if (memberReceiverType is not null &&
					context.GetExtensionMethodCandidates(memberReceiverType, context.CurrentUnit, ma.MemberName).Count > 0)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], _getBaseIdentifierName(ma.Expression));
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
			case IdentifierExpressionSyntax id when scope.Lookup(id.Name) is VariableSymbol dv && dv.Type is not DelegateTypeSymbol:
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
			case IdentifierExpressionSyntax id when _delegateProvenances.TryGetValue(id.Name, out var prior):
				return prior;
			default:
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
		}
	}

	/// <summary>Returns whether an expression denotes a safe-delegate value.</summary>
	public bool IsDelegateValueExpr(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			LambdaExpressionSyntax => true,
			MemberAccessExpressionSyntax ma =>
				GetDelegateProvenance(ma, scope).Kind == DelegateProvenanceKind.BoundMethod,
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol { Type: DelegateTypeSymbol },
			_ => false,
		};
	}
}
