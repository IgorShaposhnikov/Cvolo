using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers global variables and enforces declaration-time rules for constant initialization,
/// delegate nullability, and public multi-word storage.
/// </summary>
internal sealed class GlobalVariableRegistrar(BindingContext context)
{
	/// <summary>
	/// Validates and registers one global variable in the qualified and short-name symbol indexes.
	/// </summary>
	public void Declare(GlobalVariableDeclarationSyntax globalDecl)
	{
		// Reject 'global var ref/refvar ...' — reference types use ref/refvar directly, not var
		if (globalDecl.IsMutable && globalDecl.Type is not null && globalDecl.Type.StartsWith("ref"))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Cannot use 'var' with reference type in global declaration. Use 'global ref' or 'global refvar' instead.");
			return;
		}

		var type = context.ResolveType(globalDecl.Type);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Unknown type '{globalDecl.Type}' in global variable '{globalDecl.Name}'.");
			return;
		}

		var qualifiedName = context.GetMangledName(globalDecl.Name, context.CurrentNamespace);
		if (context.GlobalsByQualifiedName.ContainsKey(qualifiedName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Duplicate definition of global variable '{globalDecl.Name}'.");
			return;
		}

		if (ContainsConstantIntegerDivisionByZero(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Division by zero in constant initializer for global variable '{globalDecl.Name}'.", DiagnosticIds.ConstantIntegerDivisionByZero);
			return;
		}

		// §16: safe delegates are non-null / non-default-initializable; a delegate-typed
		// global must carry an explicit initializer, and 'null' is never a legal value.
		if (type is DelegateTypeSymbol)
		{
			if (globalDecl.Initializer is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, globalDecl.Span,
					$"Global delegate '{globalDecl.Name}' requires an initializer; delegates are non-null and cannot be default-initialized.");
				return;
			}

			if (globalDecl.Initializer is NullLiteralExpressionSyntax)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, globalDecl.Initializer.Span,
					$"Cannot initialize delegate '{globalDecl.Name}' with 'null'; delegates are non-null values.",
					DiagnosticIds.NullLiteralForDelegate);
				return;
			}
		}

		// Globals may reference a function group when the slot is delegate-typed; the actual
		// conversion is resolved and recorded during validation for the emitter.
		if (type is DelegateTypeSymbol && globalDecl.Initializer is IdentifierExpressionSyntax)
		{
			// Fall through: function references are treated as usable global initializers.
		}
		else if (!IsCompileTimeConstant(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Global variable '{globalDecl.Name}' must be initialized with a compile-time constant.", DiagnosticIds.GlobalInitializerNotConstant);
			return;
		}

		// CVL1036 (§TBAA): a multi-word container exposed with module-wide (ABI) visibility
		// invites unsynchronized 16-byte register tearing. Single-word public scalars and small
		// (<9 byte) aggregates are fine; anything wider must live behind a Lock/Mutex wrapper.
		if (!context.LegacyVisibility && globalDecl.Visibility == Visibility.Public && IsMultiWordContainer(type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Shared multi-word container '{globalDecl.Name}' cannot be exposed publicly without synchronization. Potential 16-byte register tearing and Type Confusion detected. Wrap the global in a 'Lock' or 'Mutex'.",
				DiagnosticIds.MultiWordPublicGlobal);
		}

		// ref/refvar globals are inherently re-assignable (mutable references)
		var isRefType = globalDecl.Type is not null && globalDecl.Type.StartsWith("ref");
		var symbol = new VariableSymbol(globalDecl.Name, type, isMutable: globalDecl.IsMutable || isRefType)
		{
			IsInitialized = true,
			IsGlobal = true,
			Origin = OriginKind.Global,
			Visibility = globalDecl.Visibility,
			DeclaringUnit = context.CurrentUnit,
			DeclaringNamespace = context.CurrentNamespace
		};
		context.GlobalsByQualifiedName[qualifiedName] = symbol;
		if (!context.GlobalsByShortName.TryGetValue(globalDecl.Name, out var shortNameList))
		{
			shortNameList = [];
			context.GlobalsByShortName[globalDecl.Name] = shortNameList;
		}
		shortNameList.Add(symbol);
		context.GlobalVariables.Add((globalDecl, symbol));
	}

	/// <summary>
	/// Returns whether a global's value representation is wider than one machine word for the
	/// public-global tearing diagnostic.
	/// </summary>
	private bool IsMultiWordContainer(TypeSymbol type)
	{
		switch (type)
		{
			case SliceTypeSymbol or InterfaceTypeSymbol:
				return true;
			case StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol:
				return ComputeByteSize(type, new HashSet<string>()) > 8;
			default:
				return false;
		}
	}

	/// <summary>
	/// Computes the existing best-effort recursive byte size used only by the public-global
	/// multi-word check. Unknown and recursive payloads remain conservatively one word.
	/// </summary>
	private int ComputeByteSize(TypeSymbol type, HashSet<string> seen)
	{
		if (type is PointerTypeSymbol or SliceTypeSymbol)
			return type is SliceTypeSymbol ? 16 : 8;

		if (type is ArrayTypeSymbol array)
			return array.Size == int.MaxValue ? 8 : array.Size * ComputeByteSize(array.ElementType, seen);

		if (type is EnumTypeSymbol enumType)
			return TypeSymbol.PrimitiveByteSize(enumType.StorageType);
		if (type is UnionTypeSymbol unionType)
		{
			if (!seen.Add(unionType.Name))
				return 8;
			var max = 0;
			foreach (var field in unionType.Fields)
				max = Math.Max(max, field.IsVoidVariant ? 0 : ComputeByteSize(field.Type, seen));
			return max;
		}
		if (type is StructTypeSymbol structType)
		{
			if (!seen.Add(structType.Name))
				return 8;
			var total = 0;
			foreach (var field in structType.Fields)
				total += ComputeByteSize(field.Type, seen);
			return total;
		}

		return TypeSymbol.PrimitiveByteSize(type);
	}

	/// <summary>
	/// Returns whether an initializer is one of the compile-time constant forms accepted for
	/// global storage by the existing declaration rules.
	/// </summary>
	private static bool IsCompileTimeConstant(ExpressionSyntax? expr)
	{
		if (expr is null)
			return true; // zero-initialized

		return expr switch
		{
			IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or BooleanLiteralExpressionSyntax or CharacterLiteralExpressionSyntax or NullLiteralExpressionSyntax => true,
			UnaryExpressionSyntax { Operator: "-" } unary => IsCompileTimeConstant(unary.Operand),
			BinaryExpressionSyntax { Operator: "+" or "-" or "*" or "/" or "%" } bin
				=> IsCompileTimeConstant(bin.Left) && IsCompileTimeConstant(bin.Right),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.All(static m => IsCompileTimeConstant(m.Expression)),
			_ => false
		};
	}

	/// <summary>
	/// True when the initializer divides or takes the remainder of a PURE-INTEGER constant by zero.
	/// Float literals switch the expression into IEEE context (1.0 / 0.0 yields +Inf and is legal),
	/// so the fold bails the moment it meets a non-integer node. Mirrors the wrapping arithmetic that
	/// CodeGenerator.BuildGlobalInitializer uses when it folds the same tree.
	/// </summary>
	private static bool ContainsConstantIntegerDivisionByZero(ExpressionSyntax? expr)
	{
		return expr switch
		{
			BinaryExpressionSyntax { Operator: "/" or "%" } bin
				=> (TryFoldIntegerConstant(bin.Right, out var divisor) && divisor == 0)
					|| ContainsConstantIntegerDivisionByZero(bin.Left)
					|| ContainsConstantIntegerDivisionByZero(bin.Right),
			BinaryExpressionSyntax { Operator: "+" or "-" or "*" } bin
				=> ContainsConstantIntegerDivisionByZero(bin.Left) || ContainsConstantIntegerDivisionByZero(bin.Right),
			UnaryExpressionSyntax { Operator: "-" } unary => ContainsConstantIntegerDivisionByZero(unary.Operand),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.Any(static m => ContainsConstantIntegerDivisionByZero(m.Expression)),
			_ => false
		};
	}

	/// <summary>
	/// Folds a subtree of integer literals with the same wrapping arithmetic used by global
	/// initializer lowering; returns false as soon as any non-integer node is encountered.
	/// </summary>
	private static bool TryFoldIntegerConstant(ExpressionSyntax expr, out long value)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax lit:
				value = unchecked((long)lit.Value);
				return true;
			case UnaryExpressionSyntax { Operator: "-" } unary when TryFoldIntegerConstant(unary.Operand, out var inner):
				value = unchecked(-inner);
				return true;
			case BinaryExpressionSyntax bin when bin.Operator is "+" or "-" or "*" or "/" or "%"
				&& TryFoldIntegerConstant(bin.Left, out var l) && TryFoldIntegerConstant(bin.Right, out var r):
				value = bin.Operator switch
				{
					"+" => unchecked(l + r),
					"-" => unchecked(l - r),
					"*" => unchecked(l * r),
					"/" => unchecked(l / r),
					"%" => r == 0 ? 0 : unchecked(l % r),
					_ => 0
				};
				return true;
			default:
				value = 0;
				return false;
		}
	}
}
