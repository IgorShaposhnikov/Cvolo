using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.FFI;
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
	private readonly AttributeValidator _attributes = new(context);

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

		// Imported foreign globals: storage lives in an external native library. The declaration
		// never allocates or initializes storage — it binds an external mutable data symbol that is
		// read-only at the Cvolo source level but deliberately NOT an LLVM constant.
		if (globalDecl.IsForeign)
		{
			DeclareForeignGlobal(globalDecl, type, qualifiedName);
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
		// Native delegates are plain function pointers: implicit global zero-initialization is legal.
		// An explicit source-level `null` expression is still an unsafe operation and is validated
		// by SafetyPass; no native-delegate exception is granted here.
		if (type is DelegateTypeSymbol { IsNative: true } && globalDecl.Initializer is NullLiteralExpressionSyntax or DefaultExpressionSyntax)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Initializer.Span, $"Explicit null/default initialization of native delegate global '{globalDecl.Name}' requires unsafe executable initialization.", DiagnosticIds.NullForNativeDelegate);
			return;
		}

		if (type is DelegateTypeSymbol { IsNative: false })
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
	/// Registers a standalone imported foreign global (standalone <c>extern "C" global ...</c>).
	/// The declaration binds external storage; it never allocates or initializes anything.
	/// </summary>
	private void DeclareForeignGlobal(GlobalVariableDeclarationSyntax globalDecl, TypeSymbol type, string qualifiedName)
	{
		if (globalDecl.Initializer is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Initializer.Span,
				$"Imported foreign global '{globalDecl.Name}' cannot declare an initializer; its storage is external to the program.",
				DiagnosticIds.ForeignGlobalInitializer);
			return;
		}

		if (NativeAbiSemanticSafety.ContainsResourceBearingValue(context, type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Type '{type.Name}' is resource-bearing and cannot be foreign storage.", DiagnosticIds.NativeAbiResourceBearing);
			return;
		}
		if (NativeAbiRepresentability.ContainsEnumWithoutExplicitStorage(type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Type '{type.Name}' contains an enum without explicit ABI storage.", DiagnosticIds.NativeEnumRequiresExplicitStorage);
			return;
		}
		if (!NativeAbiRepresentability.IsNativeAbiRepresentable(type, NativeAbiPosition.ForeignGlobalStorage))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Type '{type.Name}' is not representable at the C ABI boundary as foreign global storage.",
				DiagnosticIds.ForeignGlobalNotAbiSafe);
			return;
		}

		if (type is TypeSymbol t && ReferenceEquals(t, TypeSymbol.String))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Type 'string' cannot be used as foreign global storage; use 'char*' or a fixed array of byte instead.",
				DiagnosticIds.ForeignGlobalNotAbiSafe);
			return;
		}

		string? importName = null;
		string? libraryName = null;
		string? winPath = null;
		string? linuxPath = null;
		string? macPath = null;
		var sawImportName = false;
		var sawLibraryImport = false;
		foreach (var attr in globalDecl.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			switch (key)
			{
				case "LibraryImport":
					if (sawLibraryImport)
					{
						ReportDiagnostic(attr, "Duplicate attribute '[LibraryImport]'.");
						continue;
					}
					sawLibraryImport = true;
					(libraryName, winPath, linuxPath, macPath) = _attributes.ExtractLibraryImport(attr, libraryName, winPath, linuxPath, macPath);
					if (libraryName is not null)
						context.NativeLibraries[libraryName] = new NativeLibraryInfo(libraryName, winPath, linuxPath, macPath);
					break;
				case "ImportName":
					if (sawImportName)
					{
						ReportDiagnostic(attr, "Duplicate attribute '[ImportName]'.");
						continue;
					}
					sawImportName = true;
					importName = _attributes.ExtractImportName(attr);
					break;
				case null:
					ReportWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
					break;
				default:
					ReportDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to foreign global declarations.");
					break;
			}
		}

		// A standalone foreign global must name its native library; without it the linker cannot
		// resolve the external data symbol.
		if (!sawLibraryImport)
		{
			ReportDiagnostic(globalDecl,
				$"Imported foreign global '{globalDecl.Name}' requires [LibraryImport] to bind its native library.",
				DiagnosticIds.ForeignGlobalRequiresLibrary);
			return;
		}

		var symbol = new VariableSymbol(globalDecl.Name, type, isMutable: globalDecl.IsMutable || globalDecl.Type.StartsWith("ref"))
		{
			IsInitialized = false,
			IsGlobal = true,
			Origin = OriginKind.Global,
			Visibility = globalDecl.Visibility,
			DeclaringUnit = context.CurrentUnit,
			DeclaringNamespace = context.CurrentNamespace,
			IsForeign = true,
			ImportName = importName,
			LibraryName = libraryName,
			WinPath = winPath,
			LinuxPath = linuxPath,
			MacPath = macPath,
			CallingConvention = globalDecl.CallingConvention ?? "C"
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
	/// Registers a foreign global declared inside an extern block. The block already carried the
	/// [LibraryImport] and calling-convention metadata; only [ImportName] is validated per member.
	/// </summary>
	public void DeclareExternBlockGlobal(GlobalVariableDeclarationSyntax globalDecl, string convention, string? libraryName, string? winPath, string? linuxPath, string? macPath)
	{
		var type = context.ResolveType(globalDecl.Type);
		if (type is null)
		{
			ReportDiagnostic(globalDecl.Span, $"Unknown type '{globalDecl.Type}' in foreign global '{globalDecl.Name}'.");
			return;
		}

		var qualifiedName = context.GetMangledName(globalDecl.Name, context.CurrentNamespace);
		if (context.GlobalsByQualifiedName.ContainsKey(qualifiedName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Duplicate definition of global variable '{globalDecl.Name}'.");
			return;
		}

		if (NativeAbiSemanticSafety.ContainsResourceBearingValue(context, type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Type '{type.Name}' is resource-bearing and cannot be foreign storage.", DiagnosticIds.NativeAbiResourceBearing);
			return;
		}
		if (NativeAbiRepresentability.ContainsEnumWithoutExplicitStorage(type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Type '{type.Name}' contains an enum without explicit ABI storage.", DiagnosticIds.NativeEnumRequiresExplicitStorage);
			return;
		}
		if (!NativeAbiRepresentability.IsNativeAbiRepresentable(type, NativeAbiPosition.ForeignGlobalStorage)
			|| ReferenceEquals(type, TypeSymbol.String))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Type '{type.Name}' is not representable at the C ABI boundary as foreign global storage.",
				DiagnosticIds.ForeignGlobalNotAbiSafe);
			return;
		}

		string? importName = null;
		var sawImportName = false;
		foreach (var attr in globalDecl.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			switch (key)
			{
				case "ImportName":
					if (sawImportName)
					{
						ReportDiagnostic(attr, "Duplicate attribute '[ImportName]'.");
						continue;
					}
					sawImportName = true;
					importName = _attributes.ExtractImportName(attr);
					break;
				case "LibraryImport":
					ReportDiagnostic(attr,
						"Attribute '[LibraryImport]' attaches a library to an extern block, not to a global inside it. Move it to the enclosing extern block.",
						DiagnosticIds.LibraryImportOnBlockGlobal);
					break;
				case null:
					ReportWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
					break;
				default:
					ReportDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to extern block global declarations.");
					break;
			}
		}

		var symbol = new VariableSymbol(globalDecl.Name, type, globalDecl.IsMutable)
		{
			IsInitialized = false,
			IsGlobal = true,
			Origin = OriginKind.Global,
			Visibility = globalDecl.Visibility,
			DeclaringUnit = context.CurrentUnit,
			DeclaringNamespace = context.CurrentNamespace,
			IsForeign = true,
			ImportName = importName,
			LibraryName = libraryName,
			WinPath = winPath,
			LinuxPath = linuxPath,
			MacPath = macPath,
			CallingConvention = convention
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

	/// <summary>Reads [LibraryImport] arguments from a foreign-global attribute and registers the library.</summary>
	private void RegisterLibraryImport(AttributeSyntax attr)
	{
		string? libraryName = null;
		string? winPath = null;
		string? linuxPath = null;
		string? macPath = null;
		(libraryName, winPath, linuxPath, macPath) = _attributes.ExtractLibraryImport(attr, libraryName, winPath, linuxPath, macPath);
		if (libraryName is not null)
			context.NativeLibraries[libraryName] = new NativeLibraryInfo(libraryName, winPath, linuxPath, macPath);
	}

	private void ReportDiagnostic(SyntaxNode node, string message, string? diagnosticId = null)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}

	private void ReportDiagnostic(TextSpan span, string message, string? diagnosticId = null)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, span, message, diagnosticId);
	}

	private void ReportWarning(SyntaxNode node, string message, string id)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.ReportWarning(currentFileContext, node.Span, message, id);
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
