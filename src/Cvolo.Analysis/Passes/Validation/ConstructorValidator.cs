using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Validates constructor bodies, constructor delegation, and defensive field initialization while
/// reusing the validation pass's existing expression and extension-body traversal.
/// </summary>
/// <remarks>
/// The validator deliberately does not perform an independent AST pass. It is invoked by
/// <see cref="ValidationPass"/> during the existing validation traversal and delegates general
/// expression/body checks back to that traversal so constructor-specific semantics are isolated
/// without changing validation order or behavior.
/// </remarks>
internal sealed class ConstructorValidator(
	BindingContext context,
	ValidationContext validation,
	OverloadResolver overloads,
	Action<string, FunctionDeclarationSyntax, bool> validateExtensionBody,
	Action<ExpressionSyntax, SymbolTable> validateExpression,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> getExpressionType)
{
	/// <summary>
	/// Validates a constructor as a mutable extension body, then checks constructor delegation or
	/// verifies that every field is definitely assigned by the constructor body.
	/// </summary>
	/// <param name="extendedTypeName">Semantic name of the struct being constructed.</param>
	/// <param name="ctor">Constructor declaration encountered by the validation traversal.</param>
	public void ValidateBody(string extendedTypeName, ConstructorDeclarationSyntax ctor)
	{
		var extendedType = context.ResolveType(extendedTypeName) as StructTypeSymbol;
		if (extendedType is null)
			return;

		var wrapper = ctor.ToFunctionDeclaration();

		// Validate the body like any extension method, but "this" is always mutable:
		// a constructor's purpose is to populate fields.
		validateExtensionBody(extendedTypeName, wrapper, true);

		// Constructor chaining via ': this(...)': the delegating constructor forwards
		// to a sibling constructor of the same type, which is responsible for
		// initialising all fields. The delegating body should therefore be empty.
		if (ctor.HasConstructorInitializer)
		{
			ValidateInitializer(extendedType, ctor);
			return;
		}

		// Defensive Initialization: every field of 'this' must be populated before
		// the constructor exits, preventing uninitialized-memory bugs.
		var assignedFields = new HashSet<string>();
		CollectFieldAssignments(ctor.Body, assignedFields);

		foreach (var field in extendedType.Fields)
		{
			if (!assignedFields.Contains(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					ctor.NameSpan,
					$"Defensive initialization: constructor '{extendedTypeName}' does not initialize field '{field.Name}'."
				);
			}
		}
	}

	/// <summary>
	/// Validates a <c>: this(...)</c> initializer, records its resolved sibling constructor, checks
	/// accessibility and delegation cycles, and warns when the delegating body is non-empty.
	/// </summary>
	private void ValidateInitializer(StructTypeSymbol extendedType, ConstructorDeclarationSyntax ctor)
	{
		// 1. Build a scope mirroring the delegating constructor's own parameters plus
		//    the mutable 'this' destination storage, then type-check the initializer args.
		var scope = new SymbolTable(context.Globals);
		scope.Declare(new VariableSymbol("this", new PointerTypeSymbol(extendedType, isMutable: true), isMutable: true)
		{
			IsInitialized = true,
			Origin = OriginKind.Parameter
		});

		var ctorParamTypes = new List<TypeSymbol>();
		foreach (var p in ctor.Parameters)
		{
			var pt = context.ResolveType(p.Type);
			if (pt is null)
				continue;

			ctorParamTypes.Add(pt);
			scope.Declare(new VariableSymbol(p.Name, pt, isMutable: false)
			{
				IsInitialized = true,
				Origin = OriginKind.Parameter
			});
		}

		var argTypes = new List<TypeSymbol>();
		foreach (var arg in ctor.ConstructorArguments!)
		{
			validateExpression(arg, scope);
			argTypes.Add(getExpressionType(arg, scope) ?? TypeSymbol.Int);
		}

		// 2. Overload resolution: pick the sibling constructor (same type, by
		//    construction) whose signature best matches the initializer arguments.
		var target = ResolveInitializerTarget(extendedType, argTypes);

		// 3. The delegating constructor's own registerable name (for the code generator).
		var callerParams = new List<TypeSymbol> { new PointerTypeSymbol(extendedType, isMutable: true) };
		callerParams.AddRange(ctorParamTypes);
		var callerName = context.GetOverloadedMangledName(extendedType.Name, callerParams);

		if (target is not null)
		{
			context.ConstructorDelegationTargets[callerName] = target;

			// 4. Accessibility: the target must be visible from the delegating constructor.
			if (target.Visibility == Visibility.Private
				&& target.DeclaringUnit is not null
				&& target.DeclaringUnit != context.CurrentUnit)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					ctor.ConstructorInitializerSpan ?? ctor.NameSpan,
					$"Constructor '{ctor.StructName}' is not accessible from the current constructor.");
			}

			// 5. Cycle detection: follow the delegation chain; if it loops back onto
			//    this constructor's chain, report cyclic delegation.
			if (HasDelegationCycle(extendedType, target, callerName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					ctor.ConstructorInitializerSpan ?? ctor.NameSpan,
					"Constructor initializer `this(...)` cannot call itself (cyclic delegation).",
					DiagnosticIds.CyclicConstructorDelegation);
				return;
			}
		}
		else
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			var sigString = string.Join(", ", argTypes.Select(t => t.Name));
			context.Diagnostics.Report(
				currentFileContext,
				ctor.ConstructorInitializerSpan ?? ctor.NameSpan,
				$"No constructor of '{extendedType.Name}' matches initializer argument types ({sigString}).");
			return;
		}

		// 6. Empty-body warning: a delegating constructor's body should defer all logic.
		if (ctor.Body.Statements.Count > 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.ReportWarning(
				currentFileContext,
				ctor.Body.Span,
				"Constructor body is not empty after `this(...)`; consider moving logic to the target constructor.",
				DiagnosticIds.NonEmptyDelegatingConstructorBody);
		}
	}

	/// <summary>
	/// Follows sibling-constructor delegation until the chain terminates or repeats a constructor
	/// symbol already present on the caller's chain.
	/// </summary>
	private bool HasDelegationCycle(StructTypeSymbol extendedType, FunctionSymbol target, string callerChainKey)
	{
		var visiting = new HashSet<string> { callerChainKey };
		var current = target;
		var steps = 0;

		while (current is not null && steps < 1024)
		{
			if (!visiting.Add(current.Name))
				return true;

			var targetDecl = FindConstructorDeclaration(current);
			if (targetDecl is null || !targetDecl.HasConstructorInitializer)
				return false;

			var nextArgs = new List<TypeSymbol>();
			foreach (var arg in targetDecl.ConstructorArguments!)
				nextArgs.Add(getExpressionType(arg, new SymbolTable(context.Globals)) ?? TypeSymbol.Int);

			current = ResolveInitializerTarget(extendedType, nextArgs);
			steps++;
		}

		return false;
	}

	/// <summary>
	/// Maps a registered constructor symbol back to its source or monomorphized constructor
	/// declaration so delegation-cycle analysis can inspect the next initializer.
	/// </summary>
	private ConstructorDeclarationSyntax? FindConstructorDeclaration(FunctionSymbol symbol)
	{
		var paramSig = symbol.Parameters.Skip(1).Select(p => p.Type.Name).ToList();
		var extendedTypeName = symbol.Parameters[0].Type.Name;

		foreach (var unit in validation.Units)
		{
			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var decl in members)
			{
				if (decl is not ExtensionDeclarationSyntax ext || ext.ExtendedTypeName != extendedTypeName)
					continue;

				foreach (var ctor in ext.Constructors)
				{
					if (SignaturesMatch(ctor, paramSig))
						return ctor;
				}
			}
		}

		foreach (var mono in context.MonomorphizedExtensionDecls)
		{
			if (mono is not ConstructorDeclarationSyntax monoCtor || monoCtor.StructName != extendedTypeName)
				continue;

			if (monoCtor.Parameters.Count == paramSig.Count)
				return monoCtor;
		}

		return null;
	}

	/// <summary>
	/// Returns whether a source constructor's resolved parameter names match a registered constructor
	/// symbol signature exactly.
	/// </summary>
	private bool SignaturesMatch(ConstructorDeclarationSyntax ctor, List<string> paramSig)
	{
		if (ctor.Parameters.Count != paramSig.Count)
			return false;

		for (var i = 0; i < paramSig.Count; i++)
		{
			if (context.ResolveType(ctor.Parameters[i].Type)?.Name != paramSig[i])
				return false;
		}

		return true;
	}

	/// <summary>
	/// Selects the registered sibling constructor whose explicit parameter signature best matches the
	/// initializer argument types using the shared exact-signature scoring rules.
	/// </summary>
	private FunctionSymbol? ResolveInitializerTarget(StructTypeSymbol extendedType, IReadOnlyList<TypeSymbol> argTypes)
	{
		FunctionSymbol? target = null;
		if (context.Constructors.TryGetValue(extendedType.Name, out var registeredCtors))
		{
			var bestScore = -1;
			foreach (var candidate in registeredCtors)
			{
				if (candidate is null)
					continue;

				var candParams = candidate.Parameters.Skip(1).Select(p => p.Type).ToList();
				if (candParams.Count != argTypes.Count)
					continue;

				var score = overloads.CompareSignatureExactly(candParams, argTypes);
				if (score > bestScore)
				{
					bestScore = score;
					target = candidate;
				}
			}
		}

		return target;
	}

	/// <summary>
	/// Recursively collects fields assigned by a constructor, accepting both explicit
	/// <c>this.field = ...</c> syntax and the existing flat-field extension scope syntax.
	/// </summary>
	private void CollectFieldAssignments(SyntaxNode node, HashSet<string> assigned)
	{
		if (node is BinaryExpressionSyntax bin && bin.Operator == "=")
		{
			if (bin.Left is MemberAccessExpressionSyntax member && GetBaseIdentifierName(member.Expression) == "this")
			{
				assigned.Add(member.MemberName);
			}
			else
			{
				var baseName = GetBaseIdentifierName(bin.Left);
				if (baseName != null && baseName != "this")
					assigned.Add(baseName);
			}
		}

		foreach (var child in node.GetChildren())
			CollectFieldAssignments(child, assigned);
	}

	/// <summary>
	/// Returns the left-most identifier underlying an identifier/member/borrow expression chain.
	/// </summary>
	private static string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax member)
			return GetBaseIdentifierName(member.Expression);
		if (expr is BorrowExpressionSyntax borrow)
			return GetBaseIdentifierName(borrow.Expression);
		return null;
	}
}
