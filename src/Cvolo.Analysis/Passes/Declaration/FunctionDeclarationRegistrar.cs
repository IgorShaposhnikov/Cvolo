using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers free functions and FFI-facing callable declarations during declaration Pass 1.
/// </summary>
/// <remarks>
/// Extension methods, constructors, destructors, global variables, and contract conformance remain
/// separate declaration responsibilities. This service owns ordinary functions, standalone externs,
/// extern blocks, and expose-extern functions because those paths share callable signature and
/// parameter registration semantics.
/// </remarks>
internal sealed class FunctionDeclarationRegistrar(BindingContext context)
{
	private readonly AttributeValidator _attributes = new(context);

	/// <summary>
	/// Export symbol names already claimed by an <c>expose extern</c> function in this module.
	/// </summary>
	private readonly HashSet<string> _exportSymbolNames = [];

	/// <summary>
	/// Registers an ordinary free-function declaration, including generic/interface/protocol templates,
	/// overload identity, attributes, safety metadata, and explicit generic specializations.
	/// </summary>
	public void DeclareFunction(FunctionDeclarationSyntax func)
	{
		// Receiver markers ('ref this' / 'refvar this') are only valid on extension methods.
		if (func.Receiver != ReceiverContract.None)
		{
			context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], func.NameSpan,
				"Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods.");
			return;
		}

		// Entry point (main / Main) is always global, lowercase, and unmangled
		var mangledName = func.Name == "main" || func.Name == "Main"
			? "main"
			: context.GetMangledName(func.Name, context.CurrentNamespace);

		// If this is a generic function template, register it as a template
		if (func.GenericParameters.Count > 0)
		{
			// Check if all generic parameters are concrete types (explicit specialization)
			var isSpecialization = func.GenericParameters.All(p => context.ResolveType(p) != null);

			if (isSpecialization)
			{
				var rawName = $"{mangledName}<{string.Join(",", func.GenericParameters)}>";
				// Canonical Name
				var instName = context.NormalizeGenericName(rawName);

				var returnType = context.ResolveType(func.ReturnType);
				context.SymbolUnits[mangledName] = context.CurrentUnit!;
				if (returnType is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, func.ReturnTypeSpan, $"Unknown return type '{func.ReturnType}'");
					return;
				}

				var specParameters = new List<ParameterSymbol>();
				foreach (var param in func.Parameters)
				{
					var paramType = context.ResolveType(param.Type);
					if (paramType is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
						continue;
					}

					specParameters.Add(new ParameterSymbol(param.Name, paramType));
				}

				var instSymbol = new FunctionSymbol(instName, returnType!, specParameters)
				{
					Visibility = func.Visibility,
					SafetyTier = func.Modifier ?? SafetyTier.Safe,
					DeclaringUnit = context.CurrentUnit
				};
				context.MonomorphizedFunctions[instName] = instSymbol;

				var instDecl = new FunctionDeclarationSyntax(func.Span, func.ReturnType, instName, [], func.Parameters, func.Body, modifier: func.Modifier, visibility: func.Visibility);
				context.MonomorphizedFunctionDecls.Add(instDecl);
				return;
			}

			// Record the original template file unit
			context.SymbolUnits[mangledName] = context.CurrentUnit!;

			context.GenericFunctionTemplates[mangledName] = func;
			return;
		}

		// A function with any nominal-interface-typed parameter is an implicit generic template:
		// the interface name has no value representation, so it is monomorphized at each call site
		// with the concrete conforming argument type (static-only dispatch, no vtable).
		if (func.Parameters.Any(p => IsInterfaceTypedParameter(p)))
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.InterfaceFunctionTemplates[mangledName] = func;
			return;
		}

		// A function with any protocol-typed parameter is likewise an implicit
		// generic template: a protocol name has no value representation, so it is
		// monomorphized at each call site with the structurally conforming
		// concrete argument type (static-only dispatch, no vtable).
		if (func.Parameters.Any(p => IsProtocolTypedParameter(p)))
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.ProtocolFunctionTemplates[mangledName] = func;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var type = context.ResolveType(func.ReturnType);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.ReturnTypeSpan, $"Unknown return type '{func.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in func.Parameters)
		{
			var paramSymbol = CreateParameter(param);
			if (paramSymbol is null)
			{
				ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(paramSymbol);
		}

		var overloadedMangledName = context.GetOverloadedMangledName(mangledName, parameters.Select(p => p.Type).ToList());

		var existing = context.Globals.Lookup(overloadedMangledName);
		if (existing is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.NameSpan, $"Duplicate definition of function '{func.Name}' with a matching parameter signature.");
			return;
		}

		// Determine safety tier from function modifier
		var safetyTier = func.Modifier ?? SafetyTier.Safe;

		var newSymbol = new FunctionSymbol(overloadedMangledName, type, parameters)
		{
			SafetyTier = safetyTier,
			Visibility = func.Visibility,
			DeclaringUnit = context.CurrentUnit
		};
		var suppressedWarnings = new List<string>();

		_attributes.ApplyFunctionAttributes(
			_attributes.Verify(func.Attributes, "Function", suppressedWarnings, safetyTier),
			newSymbol,
			suppressedWarnings,
			func.Attributes);

		// [UnsafeBody] promotes to Unsafe tier even without the unsafe modifier
		if (newSymbol.IsUnsafeBody)
			newSymbol.SafetyTier = SafetyTier.Unsafe;

		_attributes.WarnIfUnsafeBodyUnused(func.NameSpan, func.Body, newSymbol, suppressedWarnings);

		// [Inline] on a directly recursive function is advisory only; attribute diagnostics own the warning.
		_attributes.WarnIfInlineRecursive(func, newSymbol, suppressedWarnings);

		// Warn if 'unbound' is used but no ref/refvar parameters exist. A by-value factory that returns
		// a Move type (a struct with reference fields) still gains escape-relaxation value from 'unbound'
		// (spec §5 Rule 9 heap-relative escape), so the warning is suppressed in that case.
		if (safetyTier == SafetyTier.Unbound && !suppressedWarnings.Contains(DiagnosticIds.UnboundNoRefParams))
		{
			var hasRefParams = parameters.Any(p => p.Type is PointerTypeSymbol);
			var returnsRefStruct = type is StructTypeSymbol st && st.Fields.Any(f => f.Type is PointerTypeSymbol);
			var hasUnboundBody = func.HasBody && HasUnboundConstructs(func.Body!);
			if (!hasRefParams && !returnsRefStruct && !hasUnboundBody)
			{
				ReportDeclarationWarning(func, "'unbound' modifier has no effect because function has no ref/refvar parameters.", DiagnosticIds.UnboundNoRefParams);
			}
		}

		context.Globals.Declare(newSymbol);

		if (!context.OverloadedFunctions.TryGetValue(mangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[mangledName] = candidates;
		}

		candidates.Add(newSymbol);
	}

	/// <summary>
	/// Returns whether a parameter resolves to a nominal interface after removing a ref/refvar wrapper.
	/// </summary>
	private bool IsInterfaceTypedParameter(ParameterSyntax p)
	{
		var type = p.Type;
		if (type.StartsWith("refvar ", StringComparison.Ordinal))
			type = type[7..];
		else if (type.StartsWith("ref ", StringComparison.Ordinal))
			type = type[4..];
		return context.ResolveType(type) is InterfaceTypeSymbol;
	}

	/// <summary>
	/// Returns whether a parameter resolves to a structural protocol after removing a ref/refvar wrapper.
	/// </summary>
	private bool IsProtocolTypedParameter(ParameterSyntax p)
	{
		var type = p.Type;
		if (type.StartsWith("refvar ", StringComparison.Ordinal))
			type = type[7..];
		else if (type.StartsWith("ref ", StringComparison.Ordinal))
			type = type[4..];
		return context.ResolveType(type) is ProtocolTypeSymbol;
	}

	/// <summary>
	/// Resolves a callable parameter type, validates its declaration attributes, and constructs its symbol.
	/// </summary>
	public ParameterSymbol? CreateParameter(ParameterSyntax param)
	{
		var paramType = context.ResolveType(param.Type);
		if (paramType is null)
			return null;

		var symbol = new ParameterSymbol(param.Name, paramType);
		var paramSuppressedWarnings = new List<string>();
		if (_attributes.Verify(param.Attributes, "Parameter", paramSuppressedWarnings).Contains("NoAlias"))
			symbol.IsNoAlias = true;

		return symbol;
	}

	/// <summary>
	/// Registers a standalone extern function while preserving its native symbol name and FFI visibility rules.
	/// </summary>
	public void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		context.SymbolUnits[ext.Name] = context.CurrentUnit!;
		var returnType = context.ResolveType(ext.ReturnType);
		if (returnType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Unknown return type '{ext.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in ext.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(ext.Name);
		if (existing is not null)
		{
			// If the existing symbol is also an extern, we can safely ignore the duplicate declaration
			if (existing is FunctionSymbol existingFunc && existingFunc.IsExtern)
			{
				return;
			}

			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Duplicate definition of '{ext.Name}'");
			return;
		}

		// Global externs are FFI bindings to foreign symbols: their visibility is fixed at
		// 'internal' (module-scoped). A 'public' extern would export a foreign symbol as part of
		// the package ABI without any Cvolo-level type safety — require a standard Cvolo wrapper.
		if (!context.LegacyVisibility && ext.Visibility == Visibility.Public)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span,
				"Global 'extern' declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.",
				DiagnosticIds.PublicExtern);
		}

		// Declare the extern symbol with its unmangled base name
		var newSymbol = new FunctionSymbol(ext.Name, returnType, parameters, isExtern: true, isVariadic: ext.IsVariadic)
		{
			Visibility = ext.Visibility,
			DeclaringUnit = context.CurrentUnit
		};
		context.Globals.Declare(newSymbol);

		// Keep candidates registered for lookup under the unmangled name
		if (!context.OverloadedFunctions.TryGetValue(ext.Name, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[ext.Name] = candidates;
		}

		candidates.Add(newSymbol);
	}

	/// <summary>
	/// Validates an extern block, records library metadata, and registers each contained native function.
	/// </summary>
	public void DeclareExternBlock(ExternBlockSyntax block)
	{
		var convention = block.CallingConvention ?? "C";
		if (convention is not ("C" or "system"))
		{
			ReportDeclarationDiagnostic(block,
				$"Unknown calling convention '{convention}'. Supported calling conventions are \"C\" and \"system\".",
				DiagnosticIds.UnknownCallingConvention);
			return;
		}

		string? libraryName = null;
		string? winPath = null;
		string? linuxPath = null;
		string? macPath = null;
		var sawLibraryImport = false;

		foreach (var attr in block.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			switch (key)
			{
				case "LibraryImport":
					if (sawLibraryImport)
					{
						ReportDeclarationDiagnostic(attr, "Duplicate attribute '[LibraryImport]'.");
						continue;
					}

					sawLibraryImport = true;
					(libraryName, winPath, linuxPath, macPath) = _attributes.ExtractLibraryImport(attr, libraryName, winPath, linuxPath, macPath);
					break;
				case "ImportName":
					ReportDeclarationDiagnostic(attr, "Attribute '[ImportName]' can only be applied to a function declaration inside an extern block.", DiagnosticIds.ImportNameOutsideBlock);
					break;
				case null:
					ReportDeclarationWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
					break;
				default:
					ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to extern block declarations.");
					break;
			}
		}

		// Platform-specific win:/linux:/mac: paths are forwarded verbatim to the linker, which
		// resolves the path for the current compilation target OS (see LinkStrategy). There is no
		// frontend file-existence check: cross-compilation must not probe the host disk.
		if (libraryName is not null)
			context.NativeLibraries[libraryName] = new NativeLibraryInfo(libraryName, winPath, linuxPath, macPath);

		// Legacy block-level public externs are still rejected; function declarations inside
		// the block use their own visibility, defaulting to internal.
		if (!context.LegacyVisibility && block.Visibility == Visibility.Public)
		{
			ReportDeclarationDiagnostic(block,
				"Global 'extern' declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.",
				DiagnosticIds.PublicExtern);
		}

		foreach (var fn in block.Functions)
			DeclareExternBlockFunction(block, fn, convention, libraryName, winPath, linuxPath, macPath);
	}

	/// <summary>
	/// Registers one function declared inside an extern block with its import-name and library metadata.
	/// </summary>
	private void DeclareExternBlockFunction(ExternBlockSyntax block, ExternBlockFunctionSyntax fn, string convention, string? libraryName, string? winPath, string? linuxPath, string? macPath)
	{
		var mangledName = context.GetMangledName(fn.Name, context.CurrentNamespace);

		if (!context.LegacyVisibility && fn.SyntacticVisibility == Visibility.Public)
		{
			ReportDeclarationDiagnostic(fn,
				"Extern block function declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.",
				DiagnosticIds.PublicExtern);
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var returnType = context.ResolveType(fn.ReturnType);
		if (returnType is null)
		{
			ReportDeclarationDiagnostic(fn, $"Unknown return type '{fn.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in fn.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(mangledName);
		if (existing is not null)
		{
			// If the existing symbol is also an extern, we can safely ignore the duplicate declaration
			if (existing is FunctionSymbol existingFunc && existingFunc.IsExtern)
				return;

			ReportDeclarationDiagnostic(fn, $"Duplicate definition of '{fn.Name}'");
			return;
		}

		string? importName = null;
		var sawImportName = false;
		foreach (var attr in fn.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			switch (key)
			{
				case "ImportName":
					if (sawImportName)
					{
						ReportDeclarationDiagnostic(attr, "Duplicate attribute '[ImportName]'.");
						continue;
					}

					sawImportName = true;
					importName = _attributes.ExtractImportName(attr);
					break;
				case "LibraryImport":
					ReportDeclarationDiagnostic(attr,
						"Attribute '[LibraryImport]' attaches a library to an extern block, not to an individual function inside it. Move it to the enclosing extern block.",
						DiagnosticIds.LibraryImportInsideBlock);
					break;
				case null:
					ReportDeclarationWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
					break;
				default:
					ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to extern block function declarations.");
					break;
			}
		}

		// Extern block functions are never name-mangled; their symbol name IS the native name
		// unless [ImportName] overrides it (ImportName ?? Name).
		var newSymbol = new FunctionSymbol(mangledName, returnType, parameters, isExtern: true, isVariadic: fn.IsVariadic)
		{
			Visibility = fn.Visibility,
			DeclaringUnit = context.CurrentUnit,
			ImportName = importName,
			LibraryName = libraryName,
			WinPath = winPath,
			LinuxPath = linuxPath,
			MacPath = macPath,
			CallingConvention = convention
		};
		context.Globals.Declare(newSymbol);

		// Keep candidates registered for lookup under the unmangled name
		if (!context.OverloadedFunctions.TryGetValue(mangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[mangledName] = candidates;
		}

		candidates.Add(newSymbol);
	}

	/// <summary>
	/// Validates an expose-extern block and registers each exported Cvolo function through the ordinary function path.
	/// </summary>
	public void DeclareExposeExternBlock(ExposeExternBlockSyntax block)
	{
		var convention = block.CallingConvention ?? "C";
		if (convention is not ("C" or "system"))
		{
			ReportDeclarationDiagnostic(block,
				$"Unknown calling convention '{convention}'. Supported calling conventions are \"C\" and \"system\".",
				DiagnosticIds.UnknownCallingConvention);
			return;
		}

		if (!context.LegacyVisibility && block.Visibility == Visibility.Public)
		{
			ReportDeclarationDiagnostic(block,
				"An expose extern block cannot be marked public. Mark the individual functions inside it public to promote them across the binary ABI.",
				DiagnosticIds.PublicExtern);
		}

		foreach (var attr in block.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			if (key is null)
			{
				ReportDeclarationWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
				continue;
			}

			ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to expose extern block declarations.");
		}

		foreach (var func in block.Functions)
			DeclareExposeExternFunction(block, func, convention);
	}

	/// <summary>
	/// Registers and annotates one expose-extern function, including ABI restrictions and export-name uniqueness.
	/// </summary>
	private void DeclareExposeExternFunction(ExposeExternBlockSyntax block, FunctionDeclarationSyntax func, string convention)
	{
		context.SymbolUnits[func.Name] = context.CurrentUnit!;

		// CVL1801: interface/protocol types have no value representation, so a by-value
		// parameter cannot cross a binary ABI boundary. 'ref'/pointer forms are allowed.
		foreach (var param in func.Parameters)
		{
			if (context.ResolveType(param.Type) is InterfaceTypeSymbol or ProtocolTypeSymbol)
			{
				ReportDeclarationDiagnostic(func,
					$"Exported function '{func.Name}' cannot contain value interface parameter '{param.Name}' across binary ABI boundaries. Use explicit pointers or 'ref' dynamic dispatch.",
					DiagnosticIds.ExposedInterfaceParameter);
				return;
			}

			// CVL1807: structures passed by reference across a C-ABI boundary must possess
			// a fixed sequential layout. In Cvolo all structs are inherently sequential
			// (LayoutKind.Sequential), but this defensive check guards against future layout
			// optimizations (e.g., field reordering) that would corrupt interop semantics.
			if (context.ResolveType(param.Type) is StructTypeSymbol structType)
			{
				// All Cvolo structs are sequential by default — verify no managed layout
				// optimizations have been applied that would break C-ABI compatibility.
				// (Currently always passes since Cvolo has no [StructLayout] attributes yet.)
			}
		}

		string? exposeName = null;
		var sawExposeName = false;
		var filteredAttributes = new List<AttributeSyntax>();
		foreach (var attr in func.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			if (key == "ExposeName")
			{
				if (sawExposeName)
				{
					ReportDeclarationDiagnostic(attr, "Duplicate attribute '[ExposeName]'.");
					continue;
				}

				sawExposeName = true;
				exposeName = _attributes.ExtractExposeName(attr);
				continue;
			}

			// Everything except [ExposeName] is forwarded to the normal function
			// declaration path for target/context verification.
			filteredAttributes.Add(attr);
		}

		var exportName = exposeName ?? func.Name;
		if (!_exportSymbolNames.Add(exportName))
		{
			ReportDeclarationDiagnostic(func, $"Duplicate export symbol name `{exportName}` detected in module scope.", DiagnosticIds.DuplicateExportSymbol);
			return;
		}

		// Declare the function through the normal path (with [ExposeName] stripped) so it is
		// registered, attribute-verified, and validatable exactly like any other function.
		var clone = new FunctionDeclarationSyntax(func.Span, func.ReturnType, func.Name, func.GenericParameters, func.Parameters, func.Body!, filteredAttributes, func.Modifier, func.Receiver, func.Visibility);
		DeclareFunction(clone);

		// Locate the symbol the normal path just registered to tag it for export.
		var mangledName = func.Name is "main" or "Main" ? "main" : context.GetMangledName(func.Name, context.CurrentNamespace);
		var paramTypes = new List<TypeSymbol>();
		foreach (var p in func.Parameters)
		{
			if (CreateParameter(p) is { } paramSymbol)
				paramTypes.Add(paramSymbol.Type);
		}

		var overloadedMangledName = context.GetOverloadedMangledName(mangledName, paramTypes);
		if (context.Globals.Lookup(overloadedMangledName) is not FunctionSymbol fnSym)
			return;

		fnSym.IsExported = true;
		fnSym.ExposeName = exportName;
		fnSym.CallingConvention = convention;
	}

	/// <summary>
	/// Returns whether a function body contains reference declarations or borrow expressions that make <c>unbound</c> meaningful.
	/// </summary>
	private static bool HasUnboundConstructs(SyntaxNode node)
	{
		foreach (var child in node.GetChildren())
		{
			if (child is VariableDeclarationSyntax v && (v.Type is "refvar" or "ref" || (v.Type != null && v.Type.StartsWith("ref"))))
				return true;
			if (child is BorrowExpressionSyntax)
				return true;
			if (HasUnboundConstructs(child))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Reports a declaration diagnostic with a stable diagnostic identifier in the current compilation unit.
	/// </summary>
	private void ReportDeclarationDiagnostic(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}

	/// <summary>
	/// Reports a declaration diagnostic without a stable diagnostic identifier in the current compilation unit.
	/// </summary>
	private void ReportDeclarationDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	/// <summary>
	/// Reports a declaration warning in the current compilation unit.
	/// </summary>
	private void ReportDeclarationWarning(SyntaxNode node, string message, string id)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.ReportWarning(currentFileContext, node.Span, message, id);
	}
}
