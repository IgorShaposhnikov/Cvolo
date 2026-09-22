namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns the lazily composed safety-analysis service graph for a pass instance while preserving
/// the original initialization order and the single shared syntax traversal.
/// </summary>
internal sealed class SafetyAnalysisServices(BindingContext context)
{
	private BorrowTracker? _borrows;
	private UnsafeContextValidator? _unsafeContext;
	private ReferenceLifetimeAnalyzer? _referenceLifetimes;
	private MoveAnalyzer? _moves;
	private UnboundValidator? _unbound;
	private ForEachSafetyValidator? _forEachSafety;
	private LambdaCaptureResolver? _lambdaCaptureResolver;
	private LambdaCaptureAnalyzer? _lambdaCaptures;
	private SafeDelegateAnalyzer? _safeDelegates;
	private SafetyTraversal? _traversal;
	private FunctionSafetyAnalyzer? _functionSafety;
	private SafetyExpressionFacts? _expressionFacts;

	/// <summary>
	/// Lazily creates the shared read-only expression fact provider used by safety analyzers.
	/// </summary>
	private SafetyExpressionFacts ExpressionFacts => _expressionFacts ??= new SafetyExpressionFacts(context);

	/// <summary>
	/// Lazily creates the unsafe-context validator that owns tier-stack transitions and
	/// diagnostics for unsafe-only raw-pointer operations.
	/// </summary>
	private UnsafeContextValidator UnsafeContext => _unsafeContext ??= new UnsafeContextValidator(context);

	/// <summary>
	/// Lazily creates the per-function borrow tracker that owns borrow exclusivity, parent locks,
	/// and early-release bookkeeping while move analysis and delegate semantics live in dedicated layers.
	/// </summary>
	private BorrowTracker Borrows => _borrows ??= new BorrowTracker(
		context,
		ExpressionFacts.GetBaseIdentifierName,
		() => UnsafeContext.CurrentTier);

	/// <summary>
	/// Lazily creates the reference-lifetime service while borrow-lock state is owned by
	/// <see cref="BorrowTracker"/> and tier state is supplied by <see cref="UnsafeContextValidator"/>.
	/// </summary>
	private ReferenceLifetimeAnalyzer ReferenceLifetimes => _referenceLifetimes ??= new ReferenceLifetimeAnalyzer(
		context,
		ExpressionFacts.ResolveExpressionType,
		ExpressionFacts.GetBaseIdentifierName,
		Borrows.HasParentLock,
		() => UnsafeContext.CurrentTier);

	/// <summary>
	/// Lazily creates the value-move service that owns moved-state checks, by-value ownership
	/// transfer, and large-copy diagnostics while delegate capture policy lives in its dedicated analyzer.
	/// </summary>
	private MoveAnalyzer Moves => _moves ??= new MoveAnalyzer(
		context,
		Borrows,
		ExpressionFacts.ResolveExpressionType);

	/// <summary>
	/// Lazily creates the unbound-sandbox validator that owns structural reference-field mutation,
	/// visibility preservation, and local-reference escape checks while tier state comes from
	/// <see cref="UnsafeContextValidator"/>.
	/// </summary>
	private UnboundValidator Unbound => _unbound ??= new UnboundValidator(
		context,
		ExpressionFacts.GetBaseIdentifierName,
		() => UnsafeContext.CurrentTier,
		() => UnsafeContext.IsInsideUnbound);

	/// <summary>
	/// Lazily creates the foreach safety validator that enforces reference-item escape boundaries
	/// and the immutable collection contract for the duration of each loop body.
	/// </summary>
	private ForEachSafetyValidator ForEachSafety => _forEachSafety ??= new ForEachSafetyValidator(
		context,
		ExpressionFacts.GetBaseIdentifierName);

	/// <summary>
	/// Lazily creates the single safety traversal that coordinates the extracted analyzers while
	/// preserving the original recursive walk and diagnostic ordering.
	/// </summary>
	private SafetyTraversal Traversal => _traversal ??= new SafetyTraversal(
		context,
		Borrows,
		UnsafeContext,
		ReferenceLifetimes,
		Moves,
		Unbound,
		ForEachSafety,
		SafeDelegates,
		ExpressionFacts.ResolveExpressionType,
		ExpressionFacts.GetBaseIdentifierName);

	/// <summary>
	/// Lazily creates the shared lambda-capture resolver used by capture policy and delegate provenance.
	/// </summary>
	private LambdaCaptureResolver LambdaCaptureResolver => _lambdaCaptureResolver ??= new LambdaCaptureResolver();

	/// <summary>
	/// Lazily creates the lambda-capture analyzer that owns capture policy, move/ref capture
	/// transitions, and safe-tier lambda-body validation.
	/// </summary>
	private LambdaCaptureAnalyzer LambdaCaptures => _lambdaCaptures ??= new LambdaCaptureAnalyzer(
		context,
		Borrows,
		Moves,
		LambdaCaptureResolver,
		UnsafeContext,
		ExpressionFacts.GetBaseIdentifierName,
		(expr, scope) => Traversal.CheckExpressionSafety(expr, scope),
		(block, scope, func) => Traversal.CheckBlockSafety(block, scope, func));

	/// <summary>
	/// Lazily creates the safe-delegate coordinator that preserves the existing traversal hooks while
	/// capture, escape, and provenance responsibilities live in dedicated collaborators.
	/// </summary>
	private SafeDelegateAnalyzer SafeDelegates => _safeDelegates ??= new SafeDelegateAnalyzer(
		context,
		LambdaCaptures,
		LambdaCaptureResolver,
		ExpressionFacts.ResolveExpressionType,
		ExpressionFacts.GetBaseIdentifierName);

	/// <summary>
	/// Lazily creates the per-function safety session coordinator that resets analyzer state,
	/// establishes the function tier and scope, and starts the shared traversal.
	/// </summary>
	internal FunctionSafetyAnalyzer FunctionSafety => _functionSafety ??= new FunctionSafetyAnalyzer(
		context,
		() => Borrows,
		() => UnsafeContext,
		() => ReferenceLifetimes,
		() => Unbound,
		() => SafeDelegates,
		() => Traversal);
}
