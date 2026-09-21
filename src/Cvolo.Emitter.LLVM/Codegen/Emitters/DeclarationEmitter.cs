using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Predeclares module-level LLVM entities before function bodies are emitted.
/// </summary>
/// <remarks>
/// This emitter owns declaration-time work only: runtime support declarations, named aggregate
/// shells/bodies, extern signatures, Cvolo function signatures, exported aliases, and generic
/// specialization declarations. Function bodies remain the responsibility of
/// <see cref="FunctionEmitter"/>, while data-segment globals are emitted by
/// <see cref="GlobalEmitter"/>.
/// </remarks>
/// <remarks>
/// Creates a declaration emitter over the shared module context while preserving the existing
/// FFI type-lowering behavior through the existing migration callback and shared layout service.
/// </remarks>
internal sealed class DeclarationEmitter(
	CodegenContext codegen,
	Func<TypeSymbol, LLVMTypeRef> lowerFfiType,
	IReadOnlySet<string>? definedGlobalNames)
{
	private readonly Dictionary<string, ExternDeclarationSyntax> _externDeclarations = [];
	private readonly Dictionary<string, ExternBlockFunctionSyntax> _externBlockFunctions = [];
	private readonly Dictionary<string, ConstructorDeclarationSyntax> _constructorInitializers = [];
	private readonly HashSet<string> _exportedSymbols = [];

	/// <summary>
	/// Source extern declarations keyed by the Cvolo call-site name used by <see cref="CallEmitter"/>.
	/// </summary>
	public IReadOnlyDictionary<string, ExternDeclarationSyntax> ExternDeclarations => _externDeclarations;
	/// <summary>
	/// Source extern-block declarations keyed by their resolved Cvolo function name.
	/// </summary>
	public IReadOnlyDictionary<string, ExternBlockFunctionSyntax> ExternBlockFunctions => _externBlockFunctions;
	/// <summary>
	/// Constructor declarations that carry constructor-initializer syntax and must be consulted when
	/// the corresponding function body is emitted.
	/// </summary>
	public IReadOnlyDictionary<string, ConstructorDeclarationSyntax> ConstructorInitializers => _constructorInitializers;

	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Declaration emission requires an active binding context.");

	/// <summary>
	/// Declares the small runtime support surface that generated code calls directly and registers
	/// the matching semantic parameter/return types used by call emission.
	/// </summary>
	public void DeclareRuntimeSupport()
	{
		var mallocType = LLVMTypeRef.CreateFunction(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), [LLVMTypeRef.Int64]);
		codegen.FunctionTypes["malloc"] = mallocType;
		codegen.Globals["malloc"] = codegen.Module.AddFunction("malloc", mallocType);

		var freeType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)]);
		codegen.FunctionTypes["free"] = freeType;
		codegen.Globals["free"] = codegen.Module.AddFunction("free", freeType);

		var putsType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)]);
		codegen.FunctionTypes["puts"] = putsType;
		codegen.Globals["puts"] = codegen.Module.AddFunction("puts", putsType);

		var exitType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [LLVMTypeRef.Int32]);
		codegen.FunctionTypes["exit"] = exitType;
		codegen.Globals["exit"] = codegen.Module.AddFunction("exit", exitType);

		codegen.FunctionReturnTypes["malloc"] = TypeSymbol.String;
		codegen.FunctionReturnTypes["free"] = TypeSymbol.Void;
		codegen.FunctionReturnTypes["puts"] = TypeSymbol.Int;
		codegen.FunctionReturnTypes["exit"] = TypeSymbol.Void;
		codegen.FunctionReturnTypes["memset"] = TypeSymbol.String;

		codegen.FunctionParameterTypes["malloc"] = [TypeSymbol.ULong];
		codegen.FunctionParameterTypes["free"] = [TypeSymbol.String];
		codegen.FunctionParameterTypes["puts"] = [TypeSymbol.String];
		codegen.FunctionParameterTypes["exit"] = [TypeSymbol.Int];
		codegen.FunctionParameterTypes["memset"] = [TypeSymbol.String, TypeSymbol.Int, TypeSymbol.ULong];

		var memsetType = LLVMTypeRef.CreateFunction(
			LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
			[LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32, LLVMTypeRef.Int64]);
		codegen.FunctionTypes["memset"] = memsetType;
		codegen.Globals["memset"] = codegen.Module.AddFunction("memset", memsetType);
	}

	/// <summary>
	/// Creates opaque named struct/union shells first and then assigns their bodies, preserving the
	/// existing recursive aggregate-lowering order and NPO union representation.
	/// </summary>
	public void DeclareAggregateTypes()
	{
		foreach (var structType in BindingContext.StructTypes.Values)
		{
			if (!codegen.LlvmStructTypes.ContainsKey(structType.Name))
			{
				codegen.LlvmStructTypes[structType.Name] = codegen.LLVMContext.CreateNamedStruct(structType.Name);
			}
		}

		foreach (var unionType in BindingContext.UnionTypes.Values)
		{
			if (!codegen.LlvmStructTypes.ContainsKey(unionType.Name))
			{
				codegen.LlvmStructTypes[unionType.Name] = codegen.LLVMContext.CreateNamedStruct(unionType.Name);
			}
		}

		foreach (var structType in BindingContext.StructTypes.Values)
		{
			var llvmStruct = codegen.LlvmStructTypes[structType.Name];
			var fieldTypes = structType.Fields.Select(f => codegen.Types.Lower(f.Type)).ToArray();
			llvmStruct.StructSetBody(fieldTypes, false);
		}

		foreach (var unionType in BindingContext.UnionTypes.Values)
		{
			if (unionType.IsNpoEligible)
			{
				continue;
			}

			var llvmUnion = codegen.LlvmStructTypes[unionType.Name];
			var maxPayloadSize = unionType.Fields
				.Where(f => !f.IsVoidVariant)
				.Select(f => codegen.AggregateLayout.GetByteSize(f.Type))
				.DefaultIfEmpty(0)
				.Max();
			llvmUnion.StructSetBody(
				[LLVMTypeRef.Int8, LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)maxPayloadSize)],
				false);
		}
	}

	/// <summary>
	/// Declares externs, ordinary functions, exposed functions, extension methods, and constructors
	/// from source units plus declaration-only external package units.
	/// </summary>
	public void DeclareSourceSignatures(IReadOnlyList<CompilationUnitSyntax> units)
	{
		var declarationUnits = units.Concat(BindingContext.ExternalPackageUnits.Where(unit => !units.Contains(unit)));
		foreach (var unit in declarationUnits)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			BindingContext.CurrentUnit = unit;
			BindingContext.CurrentNamespace = ns;
			var isExternalPackageUnit = BindingContext.ExternalPackageUnits.Contains(unit);
			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				switch (member)
				{
					case ExternDeclarationSyntax ext:
						_externDeclarations[ext.Name] = ext;
						DeclareExternFunction(ext);
						break;
					case ExternBlockSyntax extBlock:
						foreach (var fn in extBlock.Functions)
						{
							var sourceName = BindingContext.GetMangledName(fn.Name, ns);
							if (BindingContext.Globals.Lookup(sourceName) is not FunctionSymbol funcSym)
							{
								continue;
							}

							_externBlockFunctions[sourceName] = fn;
							DeclareExternBlockFunction(fn, funcSym);
						}

						break;
					case FunctionDeclarationSyntax func when func.GenericParameters.Count == 0 && !func.Name.Contains('<'):
						var ifaceTemplateName = BindingContext.GetMangledName(func.Name, ns);
						if (BindingContext.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName)
							|| BindingContext.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName)
							|| (!func.HasBody && !isExternalPackageUnit)
							|| func.Attributes.Any(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute"))
						{
							continue;
						}

						var mangledName = func.Name is "main" or "Main"
							? "main"
							: BindingContext.GetMangledName(func.Name, ns);
						var paramTypes = func.Parameters.Select(p => BindingContext.ResolveType(p.Type)!).ToList();
						DeclareFunction(func, BindingContext.GetOverloadedMangledName(mangledName, paramTypes));
						break;
					case ExposeExternBlockSyntax exportBlock:
						DeclareExportBlock(exportBlock, ns);
						break;
					case ExtensionDeclarationSyntax extDecl:
						DeclareExtension(extDecl, ns);
						break;
				}
			}
		}
	}

	/// <summary>
	/// Declares monomorphized generic functions, extension methods, and constructors after ordinary
	/// source signatures have been registered.
	/// </summary>
	public void DeclareGenericSpecializations(IReadOnlyList<CompilationUnitSyntax> units)
	{
		foreach (var instDecl in BindingContext.MonomorphizedFunctionDecls)
		{
			var baseMangledName = instDecl.Name.Split('<')[0];
			var originalUnit = (BindingContext.SymbolUnits.TryGetValue(baseMangledName, out var unit) ? unit : null) ?? units[0];
			BindingContext.CurrentUnit = originalUnit;
			BindingContext.CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;
			DeclareFunction(instDecl, instDecl.Name);
		}

		foreach (var decl in BindingContext.MonomorphizedExtensionDecls)
		{
			var emitName = BindingContext.MonomorphizedExtensionNames[decl];
			if (decl is FunctionDeclarationSyntax function)
			{
				DeclareFunction(function, emitName);
			}
			else if (decl is ConstructorDeclarationSyntax constructor)
			{
				DeclareFunction(constructor.ToFunctionDeclaration(), emitName);
				if (constructor.HasConstructorInitializer)
				{
					_constructorInitializers[emitName] = constructor;
				}
			}
		}
	}

	/// <summary>
	/// Maps a resolved constructor overload back to the matching source declaration by comparing
	/// explicit parameter types and ignoring an implicit pointer receiver when present.
	/// </summary>
	public ConstructorDeclarationSyntax? FindConstructorDeclaration(IReadOnlyList<ConstructorDeclarationSyntax> constructors, FunctionSymbol candidate)
	{
		var candidateParameters = candidate.Parameters[0].Type is PointerTypeSymbol
			? [.. candidate.Parameters.Skip(1).Select(p => p.Type.Name)]
			: candidate.Parameters.Select(p => p.Type.Name).ToList();

		foreach (var constructor in constructors)
		{
			if (constructor.Parameters.Count != candidateParameters.Count)
			{
				continue;
			}

			var matches = true;
			for (var i = 0; i < candidateParameters.Count; i++)
			{
				var resolved = BindingContext.ResolveType(constructor.Parameters[i].Type)?.Name;
				if ((resolved ?? constructor.Parameters[i].Type) != candidateParameters[i])
				{
					matches = false;
					break;
				}
			}

			if (matches)
			{
				return constructor;
			}
		}

		return null;
	}

	/// <summary>
	/// Declares one legacy extern signature and records its semantic parameter and return types for
	/// subsequent call lowering.
	/// </summary>
	private void DeclareExternFunction(ExternDeclarationSyntax declaration)
	{
		if (codegen.Globals.ContainsKey(declaration.Name))
		{
			if (!codegen.FunctionParameterTypes.ContainsKey(declaration.Name))
			{
				codegen.FunctionParameterTypes[declaration.Name] = [.. declaration.Parameters.Select(parameter => BindingContext.ResolveType(parameter.Type)!)];
			}

			if (!codegen.FunctionReturnTypes.ContainsKey(declaration.Name))
			{
				codegen.FunctionReturnTypes[declaration.Name] = BindingContext.ResolveType(declaration.ReturnType)!;
			}

			return;
		}

		var returnTypeSymbol = BindingContext.ResolveType(declaration.ReturnType)!;
		var returnType = codegen.Types.Lower(returnTypeSymbol);
		codegen.FunctionReturnTypes[declaration.Name] = returnTypeSymbol;

		var parameterSymbols = declaration.Parameters
			.Select(parameter => BindingContext.ResolveType(parameter.Type)!)
			.ToList();
		var parameterTypes = parameterSymbols.Select(codegen.Types.Lower).ToArray();
		codegen.FunctionParameterTypes[declaration.Name] = parameterSymbols;

		var functionType = declaration.IsVariadic
			? LLVMTypeRef.CreateFunction(returnType, parameterTypes, IsVarArg: true)
			: LLVMTypeRef.CreateFunction(returnType, parameterTypes);
		var function = codegen.Module.AddFunction(declaration.Name, functionType);
		codegen.Globals[declaration.Name] = function;
		codegen.FunctionTypes[declaration.Name] = functionType;
	}

	/// <summary>
	/// Declares one extern-block function under its native import name while retaining the resolved
	/// Cvolo symbol name as the module registry key used by call-site lookup.
	/// </summary>
	private void DeclareExternBlockFunction(ExternBlockFunctionSyntax declaration, FunctionSymbol symbol)
	{
		if (codegen.Globals.ContainsKey(symbol.Name))
			return;

		var returnTypeSymbol = BindingContext.ResolveType(declaration.ReturnType)!;
		var returnType = codegen.Types.Lower(returnTypeSymbol);
		codegen.FunctionReturnTypes[symbol.Name] = returnTypeSymbol;

		var parameterSymbols = declaration.Parameters
			.Select(parameter => BindingContext.ResolveType(parameter.Type)!)
			.ToList();
		var parameterTypes = parameterSymbols.Select(codegen.Types.Lower).ToArray();
		codegen.FunctionParameterTypes[symbol.Name] = parameterSymbols;

		var functionType = declaration.IsVariadic
			? LLVMTypeRef.CreateFunction(returnType, parameterTypes, IsVarArg: true)
			: LLVMTypeRef.CreateFunction(returnType, parameterTypes);
		var nativeName = symbol.ImportName ?? declaration.Name;
		var function = codegen.Module.AddFunction(nativeName, functionType);
		function.FunctionCallConv = symbol.CallingConvention == "system"
			? (uint)LLVMCallConv.LLVMX86StdcallCallConv
			: (uint)LLVMCallConv.LLVMCCallConv;
		codegen.Globals[symbol.Name] = function;
		codegen.FunctionTypes[symbol.Name] = functionType;
	}

	/// <summary>
	/// Declares one Cvolo function signature, attaches existing linkage/attribute policy, and records
	/// semantic signature metadata required by later function and call emission.
	/// </summary>
	private void DeclareFunction(FunctionDeclarationSyntax declaration, string emitName)
	{
		if (codegen.Globals.ContainsKey(emitName))
			return;

		var returnTypeSymbol = BindingContext.ResolveType(declaration.ReturnType)!;
		var declaredSymbol = emitName == "main" ? null : BindingContext.Globals.Lookup(emitName);
		var isExported = declaredSymbol is FunctionSymbol { IsExported: true };
		var returnType = isExported ? lowerFfiType(returnTypeSymbol) : codegen.Types.Lower(returnTypeSymbol);
		codegen.FunctionReturnTypes[emitName] = returnTypeSymbol;

		List<TypeSymbol> parameterSymbols;
		List<LLVMTypeRef> parameterTypes = [];
		if (isExported && declaredSymbol is FunctionSymbol exportSymbol)
		{
			parameterSymbols = exportSymbol.Parameters.Select(parameter => parameter.Type).ToList();
			parameterTypes.AddRange(parameterSymbols.Select(lowerFfiType));
		}
		else if (BindingContext.Globals.Lookup(emitName) is FunctionSymbol functionSymbol)
		{
			parameterSymbols = functionSymbol.Parameters.Select(parameter => parameter.Type).ToList();
			parameterTypes.AddRange(parameterSymbols.Select(codegen.Types.Lower));
		}
		else
		{
			parameterSymbols = [.. declaration.Parameters.Select(parameter => BindingContext.ResolveType(parameter.Type)!)];
			parameterTypes.AddRange(parameterSymbols.Select(codegen.Types.Lower));
		}

		codegen.FunctionParameterTypes[emitName] = parameterSymbols;
		var functionType = LLVMTypeRef.CreateFunction(returnType, [.. parameterTypes]);
		var llvmFunction = codegen.Module.AddFunction(emitName, functionType);

		if (emitName != "main" && !declaration.HasBody)
		{
			llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (emitName != "main" && definedGlobalNames is not null && declaration.Visibility == Visibility.Public)
		{
			llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (emitName != "main" && declaredSymbol is FunctionSymbol { IsNeverInline: true })
		{
			llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (emitName != "main")
		{
			llvmFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
		}

		if (declaredSymbol is FunctionSymbol symbol)
		{
			for (var i = 0; i < symbol.Parameters.Count; i++)
			{
				if ((symbol.IsNoAlias || symbol.Parameters[i].IsNoAlias)
					&& symbol.Parameters[i].Type is PointerTypeSymbol or RawPointerTypeSymbol)
				{
					AddStringAttribute(llvmFunction, (LLVMAttributeIndex)(i + 1), "noalias");
				}
			}

			if (symbol.IsInline)
			{
				AddStringAttribute(llvmFunction, (LLVMAttributeIndex)(-1), "alwaysinline");
			}
			else if (symbol.IsNeverInline)
			{
				AddStringAttribute(llvmFunction, (LLVMAttributeIndex)(-1), "noinline");
			}

			if (symbol.IsExported)
			{
				AddStringAttribute(llvmFunction, (LLVMAttributeIndex)(-1), "sspstrong");
			}
		}

		codegen.Globals[emitName] = llvmFunction;
		codegen.FunctionTypes[emitName] = functionType;
	}

	/// <summary>
	/// Declares functions inside one expose-extern block and creates each exported host-visible alias
	/// exactly once when the bound function symbol is marked for export.
	/// </summary>
	private void DeclareExportBlock(ExposeExternBlockSyntax exportBlock, string? currentNamespace)
	{
		foreach (var exportFunction in exportBlock.Functions)
		{
			var templateName = BindingContext.GetMangledName(exportFunction.Name, currentNamespace);
			if (BindingContext.InterfaceFunctionTemplates.ContainsKey(templateName)
				|| BindingContext.ProtocolFunctionTemplates.ContainsKey(templateName)
				|| !exportFunction.HasBody
				|| exportFunction.Attributes.Any(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute"))
			{
				continue;
			}

			var mangledName = exportFunction.Name is "main" or "Main"
				? "main"
				: BindingContext.GetMangledName(exportFunction.Name, currentNamespace);
			var parameterTypes = exportFunction.Parameters.Select(p => BindingContext.ResolveType(p.Type)!).ToList();
			var overloadedName = BindingContext.GetOverloadedMangledName(mangledName, parameterTypes);
			DeclareFunction(exportFunction, overloadedName);

			if (BindingContext.Globals.Lookup(overloadedName) is FunctionSymbol symbol
				&& symbol.IsExported
				&& _exportedSymbols.Add(symbol.ExposeName ?? exportFunction.Name)
				&& codegen.Globals.TryGetValue(overloadedName, out var target)
				&& codegen.FunctionTypes.TryGetValue(overloadedName, out var functionType))
			{
				CreateExportAlias(symbol.ExposeName ?? exportFunction.Name, target, functionType);
			}
		}
	}

	/// <summary>
	/// Declares concrete extension methods, destructors, and constructors while skipping protocol
	/// default implementations that are materialized only after substitution onto conforming types.
	/// </summary>
	private void DeclareExtension(ExtensionDeclarationSyntax extension, string? currentNamespace)
	{
		if (BindingContext.ResolveType(extension.ExtendedTypeName) is ProtocolTypeSymbol)
			return;

		foreach (var method in extension.Methods.Concat(extension.Destructors.Select(static d => d.ToFunctionDeclaration())))
		{
			var baseName = BindingContext.GetMangledName($"{extension.ExtendedTypeName}.{method.Name}", currentNamespace);
			if (!BindingContext.OverloadedFunctions.TryGetValue(baseName, out var candidates))
			{
				continue;
			}

			foreach (var candidate in candidates)
			{
				DeclareFunction(method, candidate.Name);
			}
		}

		foreach (var constructor in extension.Constructors)
		{
			var baseName = BindingContext.GetMangledName(extension.ExtendedTypeName, currentNamespace);
			if (!BindingContext.OverloadedFunctions.TryGetValue(baseName, out var candidates))
			{
				continue;
			}

			foreach (var candidate in candidates)
			{
				var matchingDeclaration = FindConstructorDeclaration(extension.Constructors, candidate) ?? constructor;
				DeclareFunction(matchingDeclaration.ToFunctionDeclaration(), candidate.Name);
				if (matchingDeclaration.HasConstructorInitializer)
					_constructorInitializers[candidate.Name] = matchingDeclaration;
			}
		}
	}

	/// <summary>
	/// Attaches one LLVM string/enum-compatible attribute at the requested function or parameter
	/// index using the same string-attribute representation as the previous inline implementation.
	/// </summary>
	private void AddStringAttribute(LLVMValueRef llvmFunction, LLVMAttributeIndex index, string name)
	{
		var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
		var emptyBytes = System.Text.Encoding.UTF8.GetBytes("\0");
		unsafe
		{
			fixed (byte* namePtr = nameBytes)
			fixed (byte* valuePtr = emptyBytes)
			{
				var attribute = LLVMSharp.Interop.LLVM.CreateStringAttribute(
					codegen.LLVMContext,
					(sbyte*)namePtr,
					(uint)name.Length,
					(sbyte*)valuePtr,
					0);
				llvmFunction.AddAttributeAtIndex(index, attribute);
			}
		}
	}

	/// <summary>
	/// Creates the host-visible alias for an exposed function while preserving the platform-specific
	/// DLL export/protected-visibility behavior.
	/// </summary>
	private void CreateExportAlias(string exportName, LLVMValueRef target, LLVMTypeRef functionType)
	{
		var alias = codegen.Module.AddAlias2(functionType, 0, target, exportName);
		if (OperatingSystem.IsWindows())
		{
			alias.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		}
		else
		{
			alias.Visibility = LLVMVisibility.LLVMProtectedVisibility;
		}
	}
}
