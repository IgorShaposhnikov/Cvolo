using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Emitter.LLVM.Codegen;
using Cvolo.Emitter.LLVM.Codegen.Emitters;
using Cvolo.Emitter.LLVM.Codegen.TypeLowering;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

public sealed class CodeGenerator : IEmitter, IDisposable
{
	private readonly CodegenContext _codegen;
	private readonly CleanupEmitter _cleanup;
	private readonly MemoryEmitter _memory;
	private readonly AggregateEmitter _aggregates;
	private readonly CallEmitter _calls;
	private readonly DelegateEmitter _delegates;
	private readonly ValueCoercion _coercion;
	private readonly StatementEmitter _statements;
	private readonly ExpressionEmitter _expressions;
	private readonly ILLVMOptimizer? _optimizer;
	private readonly IRVerifier? _irVerifier;

	// Keep migration aliases local to CodeGenerator so this commit changes ownership,
	// not hundreds of emission call sites at once.
	private LLVMModuleRef _module => _codegen.Module;
	private LLVMBuilderRef _builder => _codegen.Builder;
	private LLVMContextRef _context => _codegen.LLVMContext;
	private LlvmTypeLowering _types => _codegen.Types;
	private Dictionary<string, LLVMValueRef> _globals => _codegen.Globals;
	private Dictionary<string, LLVMTypeRef> _functionTypes => _codegen.FunctionTypes;
	private Dictionary<string, LLVMTypeRef> _llvmStructTypes => _codegen.LlvmStructTypes;
	private Dictionary<string, List<TypeSymbol>> _functionParameterTypes => _codegen.FunctionParameterTypes;
	private Dictionary<string, TypeSymbol> _functionReturnTypes => _codegen.FunctionReturnTypes;
	private Dictionary<string, LLVMValueRef> _globalVariables => _codegen.GlobalVariables;
	private Dictionary<string, TypeSymbol> _globalVariableTypes => _codegen.GlobalVariableTypes;
	private Dictionary<string, List<string>> _globalShortNames => _codegen.GlobalShortNames;
	private BindingContext? _bindingContext { get => _codegen.BindingContext; set => _codegen.BindingContext = value; }
	private CompilationContext? _compilationContext { get => _codegen.CompilationContext; set => _codegen.CompilationContext = value; }
	private CompilationUnitSyntax? _currentUnit { get => _codegen.CurrentUnit; set => _codegen.CurrentUnit = value; }

	private FunctionCodegenContext _function = new();
	private readonly Dictionary<string, StructDeclarationSyntax> _astStructs = [];
	private readonly Dictionary<string, ExternDeclarationSyntax> _astExterns = [];
	private readonly Dictionary<string, ExternBlockFunctionSyntax> _astExternBlockFunctions = [];
	private readonly bool _enableTbaa;
	private TbaaMetadata? _tbaa;
	private (LLVMTypeRef Type, LLVMValueRef Func)? _llvmTrap;
	private readonly Dictionary<string, LLVMValueRef> _enumValuesGlobals = [];
	private readonly Dictionary<string, ConstructorDeclarationSyntax> _constructorInitializers = [];
	private readonly HashSet<string> _exportedSymbols = [];
	private readonly bool _checkedFfiBounds;
	private readonly IReadOnlySet<string>? _definedGlobalNames;

	static CodeGenerator()
	{
		LLVMSharp.Interop.LLVM.InitializeAllTargetInfos();
		LLVMSharp.Interop.LLVM.InitializeAllTargets();
		LLVMSharp.Interop.LLVM.InitializeAllTargetMCs();
		LLVMSharp.Interop.LLVM.InitializeAllAsmParsers();
		LLVMSharp.Interop.LLVM.InitializeAllAsmPrinters();
	}

	public CodeGenerator(string moduleName, TargetLayout targetLayout, ILLVMOptimizer? optimizer = null, IRVerifier? irVerifier = null, bool enableTbaa = true, bool checkedFfiBounds = false, IReadOnlySet<string>? definedGlobalNames = null)
	{
		var llvmContext = LLVMContextRef.Global;
		var module = llvmContext.CreateModuleWithName(moduleName);
		var builder = llvmContext.CreateBuilder();
		targetLayout.Apply(module, LLVMTargetRef.DefaultTriple);
		_codegen = new CodegenContext(llvmContext, module, builder, targetLayout);
		_cleanup = new CleanupEmitter(_codegen);
		_memory = new MemoryEmitter(_codegen);
		_coercion = new ValueCoercion(_codegen);
		_aggregates = new AggregateEmitter(_codegen, _memory, EmitExpression, GetExprType);
		_calls = new CallEmitter(
			_codegen,
			_cleanup,
			() => _function,
			EmitExpression,
			GetExprType,
			GetFieldPointer,
			_coercion,
			Load,
			GetByteSize,
			_astExterns,
			_astExternBlockFunctions);
		_statements = new StatementEmitter(
			_codegen,
			_cleanup,
			_memory,
			_aggregates,
			_calls,
			_coercion,
			() => _function,
			EmitExpression,
			GetExprType,
			GetFieldPointer,
			IsConstructorCall,
			EmitEnumSwitchTrapDefault);
		_delegates = new DelegateEmitter(
			_codegen,
			() => _function,
			function => _function = function,
			EmitExpression,
			_statements.EmitBlock,
			GetExprType,
			_coercion,
			Load,
			(structType, fieldName) => GetFieldIndex(structType, fieldName),
			ResolveGlobalKey);
		_expressions = new ExpressionEmitter(
			_codegen,
			_cleanup,
			_memory,
			_aggregates,
			_calls,
			_delegates,
			_coercion,
			() => _function,
			GetExprType,
			GetFieldPointer,
			Load,
			ResolveGlobalKey,
			TryExtractQualifiedGlobalKey,
			TryResolveEnumVariantReceiver,
			EmitEnumValuesSlicePointer,
			(unionType, fieldName) => GetFieldIndex(unionType, fieldName),
			ApplyTbaa,
			TypeEscapesHeap,
			SafeBitCast,
			MaterializeEnumCastOption);

		_optimizer = optimizer;
		_irVerifier = irVerifier;
		_enableTbaa = enableTbaa;
		_checkedFfiBounds = checkedFfiBounds;
		_definedGlobalNames = definedGlobalNames;
	}

	public LLVMModuleRef Module => _module;

	public string Emit(IReadOnlyList<CompilationUnitSyntax> units, CompilationContext context, BindingContext bindingContext)
	{
		_bindingContext = bindingContext;
		_compilationContext = context;

		// Inject standard safe memory management system declarations
		var mallocType = LLVMTypeRef.CreateFunction(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), [LLVMTypeRef.Int64]);
		_functionTypes["malloc"] = mallocType;
		_globals["malloc"] = _module.AddFunction("malloc", mallocType);

		var freeType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)]);
		_functionTypes["free"] = freeType;
		_globals["free"] = _module.AddFunction("free", freeType);

		var putsType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0)]);
		_functionTypes["puts"] = putsType;
		_globals["puts"] = _module.AddFunction("puts", putsType);

		var exitType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [LLVMTypeRef.Int32]);
		_functionTypes["exit"] = exitType;
		_globals["exit"] = _module.AddFunction("exit", exitType);

		// Register return-type symbols for the built-in runtime functions so the call
		// emitter knows not to name void returns (void calls must have no instruction name).
		_functionReturnTypes["malloc"] = TypeSymbol.String;
		_functionReturnTypes["free"] = TypeSymbol.Void;
		_functionReturnTypes["puts"] = TypeSymbol.Int;
		_functionReturnTypes["exit"] = TypeSymbol.Void;
		_functionReturnTypes["memset"] = TypeSymbol.String;

		// Register parameter-type symbols so call emission sees correct widths for built-ins
		_functionParameterTypes["malloc"] = [TypeSymbol.ULong];
		_functionParameterTypes["free"] = [TypeSymbol.String];
		_functionParameterTypes["puts"] = [TypeSymbol.String];
		_functionParameterTypes["exit"] = [TypeSymbol.Int];
		_functionParameterTypes["memset"] = [TypeSymbol.String, TypeSymbol.Int, TypeSymbol.ULong];

		// memset(void* dest, int value, size_t count) -> void* â€” used for `{}` zero-init arrays
		var memsetType = LLVMTypeRef.CreateFunction(
			LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
			[LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), LLVMTypeRef.Int32, LLVMTypeRef.Int64]);
		_functionTypes["memset"] = memsetType;
		_globals["memset"] = _module.AddFunction("memset", memsetType);

		// Pass A: Declare all Nominal and Instantiated Structs as Opaque Shells
		foreach (var structType in bindingContext.StructTypes.Values)
		{
			if (!_llvmStructTypes.ContainsKey(structType.Name))
			{
				_llvmStructTypes[structType.Name] = _context.CreateNamedStruct(structType.Name);
			}
		}

		foreach (var unionType in bindingContext.UnionTypes.Values)
		{
			if (!_llvmStructTypes.ContainsKey(unionType.Name))
			{
				_llvmStructTypes[unionType.Name] = _context.CreateNamedStruct(unionType.Name);
			}
		}

		// Pass B: Define Struct Bodies recursively
		foreach (var structType in bindingContext.StructTypes.Values)
		{
			var llvmStruct = _llvmStructTypes[structType.Name];
			var fieldTypes = structType.Fields.Select(f => GetLLVMType(f.Type)).ToArray();
			llvmStruct.StructSetBody(fieldTypes, false);
		}

		foreach (var unionType in bindingContext.UnionTypes.Values)
		{
			// Null-Pointer Optimization: an Option whose payload is a ref/refvar compiles to a
			// single flat 8-byte pointer (Some = non-zero address, None = 0) with zero size/tag
			// overhead. A flat pointer needs no named struct body.
			if (unionType.IsNpoEligible)
				continue;

			var llvmUnion = _llvmStructTypes[unionType.Name];
			var maxPayloadSize = unionType.Fields.Where(f => !f.IsVoidVariant).Select(f => GetByteSize(f.Type)).DefaultIfEmpty(0).Max();
			llvmUnion.StructSetBody([LLVMTypeRef.Int8, LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)maxPayloadSize)], false);
		}

		// Pass B2: Emit data-segment globals ('global T name = <const>;')
		foreach (var (globalNode, globalSymbol) in bindingContext.GlobalVariables)
		{
			var qualifiedName = globalSymbol.QualifiedGlobalName;
			if (_globalVariables.ContainsKey(qualifiedName))
				continue;

			var llvmType = GetLLVMType(globalSymbol.Type);
			var globalRef = _module.AddGlobal(llvmType, qualifiedName);
			var importedPackageGlobal = globalSymbol.DeclaringUnit is not null
				&& bindingContext.ExternalPackageUnits.Contains(globalSymbol.DeclaringUnit);
			var defineHere = !importedPackageGlobal
				&& (_definedGlobalNames is null || _definedGlobalNames.Contains(qualifiedName));

			globalRef.IsGlobalConstant = !globalSymbol.IsMutable;
			if (defineHere)
			{
				// PackCompilation supplies _definedGlobalNames when producing Sector 3.
				// Public package globals are part of the Cvolo package surface, so they
				// must remain externally visible to the consumer module. Ordinary builds
				// keep the historical internal linkage for their own data segment.
				globalRef.Linkage = _definedGlobalNames is not null && globalSymbol.Visibility == Visibility.Public
					? LLVMLinkage.LLVMExternalLinkage
					: LLVMLinkage.LLVMInternalLinkage;
				globalRef.Initializer = BuildGlobalInitializer(globalSymbol.Type, globalNode.Initializer, llvmType);
			}
			else
			{
				// Two kinds of globals are declarations only:
				//  * stdlib globals while producing Sector 3 (the final consumer owns them), and
				//  * globals imported from a package API in a consumer compilation (Sector 3 owns them).
				// Neither may get a local zero initializer here, otherwise a package global such
				// as Foo.Bias silently shadows the real definition from the linked package.
				globalRef.Linkage = LLVMLinkage.LLVMExternalLinkage;
			}
			_globalVariables[qualifiedName] = globalRef;
			_globalVariableTypes[qualifiedName] = globalSymbol.Type;

			if (!_globalShortNames.TryGetValue(globalSymbol.Name, out var shortCandidates))
				_globalShortNames[globalSymbol.Name] = shortCandidates = [];
			shortCandidates.Add(qualifiedName);
		}

		// Pass C: Declare Extern functions and custom user-defined function signatures.
		// External package units may be omitted from the emission unit list (package builds emit
		// only their own definitions), but their bodyless API declarations are still required.
		var declarationUnits = units.Concat(bindingContext.ExternalPackageUnits.Where(unit => !units.Contains(unit)));
		foreach (var unit in declarationUnits)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			bindingContext.CurrentUnit = unit;
			bindingContext.CurrentNamespace = ns;
			var isExternalPackageUnit = bindingContext.ExternalPackageUnits.Contains(unit);
			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				switch (member)
				{
					case ExternDeclarationSyntax ext:
						_astExterns[ext.Name] = ext;
						DeclareExternFunction(ext);
						break;
					case ExternBlockSyntax extBlock:
						foreach (var fn in extBlock.Functions)
						{
							var sourceName = bindingContext.GetMangledName(fn.Name, ns);
							var funcSym = _bindingContext!.Globals.Lookup(sourceName) as FunctionSymbol;
							if (funcSym is null)
								continue;
							_astExternBlockFunctions[sourceName] = fn;
							DeclareExternBlockFunction(fn, funcSym);
						}
						break;
					case FunctionDeclarationSyntax func when func.GenericParameters.Count == 0 && !func.Name.Contains('<'):
						// Skip abstract interface/protocol templates and bodyless intrinsic functions
						var ifaceTemplateName = bindingContext.GetMangledName(func.Name, ns);
						if (bindingContext.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName) ||
							bindingContext.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName) ||
							(!func.HasBody && !isExternalPackageUnit) ||
							func.Attributes.Any(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute"))
						{
							continue;
						}

						var mangledName = (func.Name == "main" || func.Name == "Main")
							? "main"
							: bindingContext.GetMangledName(func.Name, ns);

						var paramTypes = func.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
						var overloadedMangledName = bindingContext.GetOverloadedMangledName(mangledName, paramTypes);
						DeclareFunction(func, overloadedMangledName);
						break;
					case ExposeExternBlockSyntax exportBlock:
						foreach (var exportFunc in exportBlock.Functions)
						{
							// Skip abstract interface/protocol templates and bodyless intrinsic functions
							var exportIfaceTemplateName = bindingContext.GetMangledName(exportFunc.Name, ns);
							if (bindingContext.InterfaceFunctionTemplates.ContainsKey(exportIfaceTemplateName) ||
								bindingContext.ProtocolFunctionTemplates.ContainsKey(exportIfaceTemplateName) ||
								!exportFunc.HasBody ||
								exportFunc.Attributes.Any(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute"))
							{
								continue;
							}

							var exportedMangledName = (exportFunc.Name == "main" || exportFunc.Name == "Main")
								? "main"
								: bindingContext.GetMangledName(exportFunc.Name, ns);

							var exportedParamTypes = exportFunc.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
							var exportedOverloadedName = bindingContext.GetOverloadedMangledName(exportedMangledName, exportedParamTypes);
							DeclareFunction(exportFunc, exportedOverloadedName);

							// Synthesize the exported alias if the binder tagged this function for export.
							if (bindingContext.Globals.Lookup(exportedOverloadedName) is FunctionSymbol exportFuncSym
								&& exportFuncSym.IsExported
								&& _exportedSymbols.Add(exportFuncSym.ExposeName ?? exportFunc.Name)
								&& _globals.TryGetValue(exportedOverloadedName, out var exportedTarget)
								&& _functionTypes.TryGetValue(exportedOverloadedName, out var exportedFuncType))
							{
								CreateExportAlias(exportFuncSym.ExposeName ?? exportFunc.Name, exportedTarget, exportedFuncType);
							}
						}
						break;
					case ExtensionDeclarationSyntax extDecl:
						// Skip protocol extension defaults in Pass C (they are materialized onto concrete conformers)
						if (bindingContext.ResolveType(extDecl.ExtendedTypeName) is ProtocolTypeSymbol)
							continue;

						foreach (var method in extDecl.Methods
							.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
						{
							var baseMangledName = bindingContext.GetMangledName($"{extDecl.ExtendedTypeName}.{method.Name}", ns);
							if (bindingContext.OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
							{
								foreach (var candidate in candidates)
								{
									DeclareFunction(method, candidate.Name);
								}
							}
						}

						foreach (var ctorDecl in extDecl.Constructors)
						{
							var ctorBaseMangledName = bindingContext.GetMangledName(extDecl.ExtendedTypeName, ns);
							if (bindingContext.OverloadedFunctions.TryGetValue(ctorBaseMangledName, out var ctorCandidates))
							{
								foreach (var candidate in ctorCandidates)
								{
									var matchingDecl = FindCtorDeclaration(extDecl.Constructors, candidate) ?? ctorDecl;
									DeclareFunction(matchingDecl.ToFunctionDeclaration(), candidate.Name);

									if (matchingDecl.HasConstructorInitializer)
										_constructorInitializers[candidate.Name] = matchingDecl;
								}
							}
						}

						break;
				}
			}
		}

		// Pass D: Declare Monomorphized and Explicit Generic Specializations
		foreach (var instDecl in bindingContext.MonomorphizedFunctionDecls)
		{
			var baseMangledName = instDecl.Name.Split('<')[0];
			var originalUnit = (bindingContext.SymbolUnits.TryGetValue(baseMangledName, out var u) ? u : null) ?? units[0];
			bindingContext.CurrentUnit = originalUnit;
			bindingContext.CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;

			DeclareFunction(instDecl, instDecl.Name);
		}

		// Pass D2: Declare Monomorphized Extension Methods and Constructors
		foreach (var decl in bindingContext.MonomorphizedExtensionDecls)
		{
			var emitName = bindingContext.MonomorphizedExtensionNames[decl];
			if (decl is FunctionDeclarationSyntax func)
			{
				DeclareFunction(func, emitName);
			}
			else if (decl is ConstructorDeclarationSyntax ctor)
			{
				DeclareFunction(ctor.ToFunctionDeclaration(), emitName);

				if (ctor.HasConstructorInitializer)
					_constructorInitializers[emitName] = ctor;
			}
		}

		// Pass E: Generate bodies of Regular and Monomorphized functions
		var emittedFunctionNames = new HashSet<string>();

		foreach (var unit in units)
		{
			// Package API units are declarations only. Their implementations live in
			// Sector 3 bitcode and are linked after this module is emitted.
			if (bindingContext.ExternalPackageUnits.Contains(unit))
				continue;

			var ns = unit.NamespaceDeclaration?.Name;
			bindingContext.CurrentUnit = unit;
			bindingContext.CurrentNamespace = ns;
			_currentUnit = unit;

			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0 && !func.Name.Contains('<'))
				{
					// Interface-parameterized functions are implicit templates (no value representation);
					// their monomorphized instances are emitted separately in Pass E.
					var ifaceTemplateName = bindingContext.GetMangledName(func.Name, ns);
					if (bindingContext.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName))
						continue;

					// Protocol-parameterized functions are implicit templates too (no value representation);
					// their monomorphized instances are emitted separately in Pass E.
					if (bindingContext.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName))
						continue;

					// Keep 'main' / 'Main' global and unmangled
					var mangledName = (func.Name == "main" || func.Name == "Main")
						? "main"
						: bindingContext.GetMangledName(func.Name, ns);

					// --- FIX: Generate function bodies using their overloaded mangled names ---
					var paramTypes = func.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
					var overloadedMangledName = bindingContext.GetOverloadedMangledName(mangledName, paramTypes);

					if (emittedFunctionNames.Add(overloadedMangledName))
					{
						EmitFunctionBody(func, overloadedMangledName);
					}
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						if (exportFunc.GenericParameters.Count > 0 || exportFunc.Name.Contains('<'))
							continue;

						var exportIfaceTemplateName = bindingContext.GetMangledName(exportFunc.Name, ns);
						if (bindingContext.InterfaceFunctionTemplates.ContainsKey(exportIfaceTemplateName)
							|| bindingContext.ProtocolFunctionTemplates.ContainsKey(exportIfaceTemplateName))
							continue;

						var exportedMangledName = (exportFunc.Name == "main" || exportFunc.Name == "Main")
							? "main"
							: bindingContext.GetMangledName(exportFunc.Name, ns);

						var exportedParamTypes = exportFunc.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
						var exportedOverloadedName = bindingContext.GetOverloadedMangledName(exportedMangledName, exportedParamTypes);

						if (emittedFunctionNames.Add(exportedOverloadedName))
						{
							EmitFunctionBody(exportFunc, exportedOverloadedName);
						}
					}
				}
				else if (member is ExtensionDeclarationSyntax extDecl)
				{
					// Default implementations written on a protocol definition are
					// never emitted standalone (their receiver is abstract); a
					// substituted copy is materialized onto each conforming type.
					if (bindingContext.ResolveType(extDecl.ExtendedTypeName) is ProtocolTypeSymbol)
						continue;

					foreach (var method in extDecl.Methods
						.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
					{
						var baseMangledName = bindingContext.GetMangledName($"{extDecl.ExtendedTypeName}.{method.Name}", ns);
						if (bindingContext.OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
						{
							foreach (var candidate in candidates)
							{
								if (emittedFunctionNames.Add(candidate.Name))
								{
									EmitFunctionBody(method, candidate.Name);
								}
							}
						}
					}

					var ctorBaseMangledName = bindingContext.GetMangledName(extDecl.ExtendedTypeName, ns);
					if (bindingContext.OverloadedFunctions.TryGetValue(ctorBaseMangledName, out var ctorCandidates))
					{
						foreach (var candidate in ctorCandidates)
						{
							if (emittedFunctionNames.Add(candidate.Name))
							{
								var ctorDecl = FindCtorDeclaration(extDecl.Constructors, candidate);
								if (ctorDecl is not null)
									EmitFunctionBody(ctorDecl.ToFunctionDeclaration(), candidate.Name);
							}
						}
					}
				}
			}
		}

		foreach (var instDecl in bindingContext.MonomorphizedFunctionDecls)
		{
			var canonicalName = bindingContext.NormalizeGenericName(instDecl.Name);
			if (emittedFunctionNames.Add(canonicalName))
			{
				var baseMangledName = instDecl.Name.Split('<')[0];
				var originalUnit = (bindingContext.SymbolUnits.TryGetValue(baseMangledName, out var u) ? u : null) ?? units[0];
				bindingContext.CurrentUnit = originalUnit;
				bindingContext.CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;
				_currentUnit = originalUnit;

				EmitFunctionBody(instDecl, instDecl.Name);
			}
		}

		// Emit monomorphized extension methods and constructors
		foreach (var decl in bindingContext.MonomorphizedExtensionDecls)
		{
			var emitName = bindingContext.MonomorphizedExtensionNames[decl];
			if (emittedFunctionNames.Add(emitName))
			{
				var originalUnit = (bindingContext.SymbolUnits.TryGetValue(emitName, out var u) ? u : null) ?? units[0];
				bindingContext.CurrentUnit = originalUnit;
				bindingContext.CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;
				_currentUnit = originalUnit;

				if (decl is FunctionDeclarationSyntax func)
				{
					EmitFunctionBody(func, emitName);
				}
				else if (decl is ConstructorDeclarationSyntax ctor)
				{
					EmitFunctionBody(ctor.ToFunctionDeclaration(), emitName);
				}
			}
		}

		// 1. Create a deep clone of the unoptimized module state for diagnostic fallback
		var noOptimizedModule = _module.Clone();

		// 2. Run the optimization pipeline
		_optimizer?.Optimize(_module);

		// 3. Verify the resulting module, providing the unoptimized fallback
		_irVerifier?.VerifyModule(_module, noOptimizedModule);

		// 4. Dispose of the unoptimized clone if verification passes to prevent native leaks
		noOptimizedModule.Dispose();

		return _module.PrintToString();
	}

	private void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		// Deduplicate: If this extern function has already been declared, return early
		if (_globals.ContainsKey(ext.Name))
		{
			if (!_functionParameterTypes.ContainsKey(ext.Name))
			{
				var pSymbols = new List<TypeSymbol>();
				foreach (var param in ext.Parameters)
				{
					var paramTypeSymbol = _bindingContext!.ResolveType(param.Type)!;
					pSymbols.Add(paramTypeSymbol);
				}
				_functionParameterTypes[ext.Name] = pSymbols;
			}

			if (!_functionReturnTypes.ContainsKey(ext.Name))
			{
				_functionReturnTypes[ext.Name] = _bindingContext!.ResolveType(ext.ReturnType)!;
			}

			return;
		}

		var returnTypeSymbol = _bindingContext!.ResolveType(ext.ReturnType)!;
		var returnType = GetLLVMType(returnTypeSymbol);
		_functionReturnTypes[ext.Name] = returnTypeSymbol;

		var paramTypes = new List<LLVMTypeRef>();
		var paramSymbols = new List<TypeSymbol>();
		foreach (var param in ext.Parameters)
		{
			var paramTypeSymbol = _bindingContext.ResolveType(param.Type)!;
			paramTypes.Add(GetLLVMType(paramTypeSymbol));
			paramSymbols.Add(paramTypeSymbol);
		}

		_functionParameterTypes[ext.Name] = paramSymbols;

		var funcType = ext.IsVariadic
			? LLVMTypeRef.CreateFunction(returnType, [.. paramTypes], IsVarArg: true)
			: LLVMTypeRef.CreateFunction(returnType, [.. paramTypes]);

		var func = _module.AddFunction(ext.Name, funcType);
		_globals[ext.Name] = func;
		_functionTypes[ext.Name] = funcType;
	}

	private void DeclareExternBlockFunction(ExternBlockFunctionSyntax fn, FunctionSymbol symbol)
	{
		// Deduplicate: If this extern block function has already been declared, return early.
		if (_globals.ContainsKey(symbol.Name))
			return;

		var returnTypeSymbol = _bindingContext!.ResolveType(fn.ReturnType)!;
		var returnType = GetLLVMType(returnTypeSymbol);
		_functionReturnTypes[symbol.Name] = returnTypeSymbol;

		var paramTypes = new List<LLVMTypeRef>();
		var paramSymbols = new List<TypeSymbol>();
		foreach (var param in fn.Parameters)
		{
			var paramTypeSymbol = _bindingContext.ResolveType(param.Type)!;
			paramTypes.Add(GetLLVMType(paramTypeSymbol));
			paramSymbols.Add(paramTypeSymbol);
		}

		_functionParameterTypes[symbol.Name] = paramSymbols;

		var funcType = fn.IsVariadic
			? LLVMTypeRef.CreateFunction(returnType, [.. paramTypes], IsVarArg: true)
			: LLVMTypeRef.CreateFunction(returnType, [.. paramTypes]);

		// Extern block functions are declared under their native symbol name ([ImportName] ?? source
		// name); the _globals cache stays keyed by the Cvolo-level name so call-site resolution
		// (_globals[resolvedFunc.Name]) keeps working unchanged. "C" -> cdecl (ccc), "system" -> stdcall.
		var nativeName = symbol.ImportName ?? fn.Name;
		var func = _module.AddFunction(nativeName, funcType);
		func.FunctionCallConv = symbol.CallingConvention == "system"
			? (uint)LLVMCallConv.LLVMX86StdcallCallConv
			: (uint)LLVMCallConv.LLVMCCallConv;
		_globals[symbol.Name] = func;
		_functionTypes[symbol.Name] = funcType;
	}

	private void DeclareFunction(FunctionDeclarationSyntax func, string emitName)
	{
		// Deduplicate: If this function has already been declared, return early
		if (_globals.ContainsKey(emitName))
			return;

		var returnTypeSymbol = _bindingContext!.ResolveType(func.ReturnType)!;
		var declaredSymbol = emitName == "main" ? null : _bindingContext!.Globals.Lookup(emitName);
		var isExported = declaredSymbol is FunctionSymbol { IsExported: true };

		// FFI boundary: bool â†’ i8 (unsigned 1-byte) instead of i1 to match C ABI.
		var returnType = isExported ? GetFFIType(returnTypeSymbol) : GetLLVMType(returnTypeSymbol);
		_functionReturnTypes[emitName] = returnTypeSymbol;

		var paramTypes = new List<LLVMTypeRef>();
		var paramSymbols = new List<TypeSymbol>();
		if (isExported && declaredSymbol is FunctionSymbol exportSym)
		{
			foreach (var p in exportSym.Parameters)
			{
				paramTypes.Add(GetFFIType(p.Type));
				paramSymbols.Add(p.Type);
			}
		}
		else if (_bindingContext.Globals.Lookup(emitName) is FunctionSymbol sym)
		{
			foreach (var p in sym.Parameters)
			{
				paramTypes.Add(GetLLVMType(p.Type));
				paramSymbols.Add(p.Type);
			}
		}
		else // Fallback for standard declarations
		{
			foreach (var param in func.Parameters)
			{
				var paramTypeSymbol = _bindingContext.ResolveType(param.Type)!;
				paramTypes.Add(GetLLVMType(paramTypeSymbol));
				paramSymbols.Add(paramTypeSymbol);
			}
		}

		_functionParameterTypes[emitName] = paramSymbols;

		var funcType = LLVMTypeRef.CreateFunction(returnType, [.. paramTypes]);

		var llvmFunc = _module.AddFunction(emitName, funcType);

		if (emitName != "main" && !func.HasBody)
		{
			// Bodyless Cvolo declarations imported from a package are resolved by the
			// package's Sector 3 bitcode at link time. LLVM requires declarations
			// without a body to have external (or weak) linkage.
			llvmFunc.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (emitName != "main" && _definedGlobalNames is not null && func.Visibility == Visibility.Public)
		{
			// Sector 3 is linked as a separate LLVM module. Public Cvolo package
			// definitions therefore need external linkage so consumer declarations
			// (created from Sector 1 metadata) can resolve to these bodies. Internal
			// package helpers stay internal and cannot collide with other packages.
			llvmFunc.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (emitName != "main" && declaredSymbol is FunctionSymbol { IsNeverInline: true })
		{
			// External linkage keeps the [NeverInline] function itself from being
			// inlined or stripped by LLVM's optimizers; the symbol export is harmless.
			llvmFunc.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (emitName != "main")
		{
			llvmFunc.Linkage = LLVMLinkage.LLVMInternalLinkage;
		}

		// Attach noalias attributes if [NoAlias] is present on the function or individual parameters
		if (declaredSymbol is FunctionSymbol funcSym)
		{
			for (var i = 0; i < funcSym.Parameters.Count; i++)
			{
				if (funcSym.IsNoAlias || funcSym.Parameters[i].IsNoAlias)
				{
					if (funcSym.Parameters[i].Type is PointerTypeSymbol or RawPointerTypeSymbol)
					{
						var nameBytes = System.Text.Encoding.UTF8.GetBytes("noalias\0");
						var emptyBytes = System.Text.Encoding.UTF8.GetBytes("\0");
						unsafe
						{
							fixed (byte* namePtr = nameBytes)
							fixed (byte* valPtr = emptyBytes)
							{
								var noAliasAttr = LLVMSharp.Interop.LLVM.CreateStringAttribute(_context, (sbyte*)namePtr, 7, (sbyte*)valPtr, 0);
								llvmFunc.AddAttributeAtIndex((LLVMAttributeIndex)(i + 1), noAliasAttr);
							}
						}
					}
				}
			}

			// [Inline] / [NeverInline] -> LLVM alwaysinline / noinline function attributes
			// (applied before the body is emitted, matching the noalias pattern above).
			if (funcSym.IsInline)
				AddFunctionStringAttribute(llvmFunc, "alwaysinline");
			else if (funcSym.IsNeverInline)
				AddFunctionStringAttribute(llvmFunc, "noinline");

			// Functions inside expose extern "C" blocks are hardened with a strong stack
			// canary (sspstrong) since they intercept uncontrolled external threads.
			if (funcSym.IsExported)
				AddFunctionStringAttribute(llvmFunc, "sspstrong");
		}

		_globals[emitName] = llvmFunc;
		_functionTypes[emitName] = funcType;
	}

	/// <summary>
	/// Attaches a function-level string attribute (e.g. "alwaysinline" / "noinline") to the given LLVM function.
	/// Well-known names are canonicalized to their enum attribute kind by LLVM.
	/// </summary>
	private void AddFunctionStringAttribute(LLVMValueRef llvmFunc, string name)
	{
		var nameBytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
		var emptyBytes = System.Text.Encoding.UTF8.GetBytes("\0");
		unsafe
		{
			fixed (byte* namePtr = nameBytes)
			fixed (byte* valPtr = emptyBytes)
			{
				var attr = LLVMSharp.Interop.LLVM.CreateStringAttribute(_context, (sbyte*)namePtr, (uint)name.Length, (sbyte*)valPtr, 0);
				llvmFunc.AddAttributeAtIndex((LLVMAttributeIndex)(-1), attr);
			}
		}
	}

	/// <summary>Maps a registered constructor candidate back to its corresponding source
	/// declaration (matching by parameter count/types, ignoring the implicit 'this').</summary>
	private ConstructorDeclarationSyntax? FindCtorDeclaration(
		IReadOnlyList<ConstructorDeclarationSyntax> ctors, FunctionSymbol candidate)
	{
		var candParams = candidate.Parameters[0].Type is PointerTypeSymbol
			? candidate.Parameters.Skip(1).Select(p => p.Type.Name).ToList()
			: candidate.Parameters.Select(p => p.Type.Name).ToList();
		foreach (var ctorDecl in ctors)
		{
			if (ctorDecl.Parameters.Count != candParams.Count)
				continue;

			var match = true;
			for (var i = 0; i < candParams.Count; i++)
			{
				// Compare the source parameter's resolved type name against the candidate.
				var resolved = _bindingContext?.ResolveType(ctorDecl.Parameters[i].Type)?.Name;
				if ((resolved ?? ctorDecl.Parameters[i].Type) != candParams[i])
				{
					match = false;
					break;
				}
			}

			if (match)
				return ctorDecl;
		}

		return null;
	}

	private void EmitFunctionBody(FunctionDeclarationSyntax func, string mangledName)
	{
		// Bodyless intrinsic functions don't generate function bodies (calls are lowered directly to LLVM instructions)
		if (!func.HasBody)
			return;

		if (!_globals.TryGetValue(mangledName, out var llvmFunc))
			return;

		var entry = llvmFunc.AppendBasicBlock("entry");
		_builder.PositionAtEnd(entry);

		_function = new FunctionCodegenContext();

		var funcSymbol = _bindingContext!.Globals.Lookup(mangledName) as FunctionSymbol
			?? (_bindingContext.MonomorphizedFunctions.TryGetValue(mangledName, out var monoSymbol) ? monoSymbol : null);
		_function.UnsafeDepth = funcSymbol is not null && (funcSymbol.SafetyTier == SafetyTier.Unsafe || funcSymbol.IsUnsafeBody) ? 1 : 0;

		// An 'unbound' factory that returns a heap-escaping graph handle transfers ownership of
		// its heap allocations to the caller ('heap-relative provenance'), so inner-block scopes
		// must NOT free them (that would sever the self-referential graph mid-construction).
		_function.OwnershipTransferFunction = funcSymbol is not null
			&& funcSymbol.SafetyTier == SafetyTier.Unbound
			&& _functionReturnTypes.TryGetValue(mangledName, out var retType)
			&& TypeEscapesHeap(retType);

		// Seed data-segment globals into the local symbol table: a GlobalVariable IS a pointer,
		// so loads/stores/field GEPs work through the ordinary machinery (locals shadow on redeclare).
		foreach (var (globalName, globalRef) in _globalVariables)
		{
			if (!_function.Locals.ContainsKey(globalName))
				_function.Locals[globalName] = globalRef;
		}

		foreach (var (globalName, globalType) in _globalVariableTypes)
		{
			if (!_function.VariableTypes.ContainsKey(globalName))
				_function.VariableTypes[globalName] = globalType;
		}

		// Also seed the bare short name for unqualified references, resolved against the
		// current namespace context (mirrors the binder's ambiguity rules; ambiguous
		// references never reach codegen because the binder reports CVL1077 first).
		foreach (var (shortName, _) in _globalShortNames)
		{
			if (_function.Locals.ContainsKey(shortName))
				continue;

			var resolvedKey = ResolveGlobalKey(shortName);
			if (resolvedKey is null)
				continue;

			_function.Locals[shortName] = _globalVariables[resolvedKey];
			_function.VariableTypes[shortName] = _globalVariableTypes[resolvedKey];
		}

		if (_bindingContext!.Globals.Lookup(mangledName) is FunctionSymbol sym)
		{
			var isExported = sym.IsExported;

			// --checked-ffi-bounds: insert explicit null-check prologues for pointer params
			if (isExported && _checkedFfiBounds && sym.Parameters.Count > 0)
			{
				var bodyBlock = llvmFunc.AppendBasicBlock("ffi.body");

				// Build a combined i1 "is_null" flag by AND-ing all pointer-param null checks.
				LLVMValueRef? anyNull = null;
				for (var i = 0; i < sym.Parameters.Count; i++)
				{
					if (sym.Parameters[i].Type is PointerTypeSymbol or RawPointerTypeSymbol)
					{
						var param = llvmFunc.GetParam((uint)i);
						var isNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, param, LLVMValueRef.CreateConstPointerNull(param.TypeOf), $"null.{sym.Parameters[i].Name}");
						anyNull = anyNull is null ? isNull : _builder.BuildOr(anyNull.Value, isNull, "any_null");
					}
				}

				if (anyNull is not null)
				{
					var trapBlock = llvmFunc.AppendBasicBlock("ffi.trap");
					_builder.BuildCondBr(anyNull.Value, trapBlock, bodyBlock);

					_builder.PositionAtEnd(trapBlock);
					if (_llvmTrap is null)
					{
						var trapFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, []);
						_llvmTrap = (trapFnType, _module.AddFunction("llvm.trap", trapFnType));
					}
					_builder.BuildCall2(_llvmTrap.Value.Type, _llvmTrap.Value.Func, new LLVMValueRef[] { }, "");
					_builder.BuildUnreachable();
				}
				else
				{
					_builder.BuildBr(bodyBlock);
				}

				_builder.PositionAtEnd(bodyBlock);
			}

			for (var i = 0; i < sym.Parameters.Count; i++)
			{
				var param = llvmFunc.GetParam((uint)i);
				var paramName = sym.Parameters[i].Name;
				param.Name = paramName;

				var typeSymbol = sym.Parameters[i].Type;
				var llvmType = isExported ? GetFFIType(typeSymbol) : GetLLVMType(typeSymbol);

				var alloca = _builder.BuildAlloca(llvmType, paramName);

				// FFI bool lowering: the parameter arrives as i8 (1-byte C ABI bool);
				// truncate it back to i1 for the internal boolean logic.
				if (isExported && typeSymbol is not null && typeSymbol.Name == "bool")
				{
					param = _builder.BuildTrunc(param, LLVMTypeRef.Int1, "bool.trunc");
				}

				_builder.BuildStore(param, alloca);

				_function.Locals[paramName] = alloca;
				_function.VariableTypes[paramName] = typeSymbol;
			}
		}
		else // Fallback
		{
			for (var i = 0; i < func.Parameters.Count; i++)
			{
				var param = llvmFunc.GetParam((uint)i);
				var paramName = func.Parameters[i].Name;
				param.Name = paramName;

				var typeSymbol = _bindingContext!.ResolveType(func.Parameters[i].Type)!;
				var llvmType = GetLLVMType(typeSymbol);

				var alloca = _builder.BuildAlloca(llvmType, paramName);
				_builder.BuildStore(param, alloca);

				_function.Locals[paramName] = alloca;
				_function.VariableTypes[paramName] = typeSymbol;
			}
		}

		// Constructor chaining: a delegating constructor (`T(args) : this(...)`) invokes
		// the target constructor on the same destination storage before its own body runs.
		if (_constructorInitializers.TryGetValue(mangledName, out var chainedCtor)
			&& _function.Locals.TryGetValue("this", out var thisStorage)
			&& _bindingContext!.ConstructorDelegationTargets.TryGetValue(mangledName, out var chainTarget))
		{
			// 'this' holds the alloca of the destination-storage pointer; load the pointer value.
			var thisPtr = _builder.BuildLoad2(
				LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
				thisStorage,
				"chained_this");

			var chainCall = new CallExpressionSyntax(
				chainedCtor.ConstructorInitializerSpan ?? chainedCtor.Span,
				chainedCtor.StructName,
				[],
				chainedCtor.ConstructorArguments!);

			_bindingContext.ResolvedCalls[chainCall] = chainTarget;
			_calls.Emit(chainCall, thisPtr);
		}

		_statements.EmitBlock(func.Body);

		if (func.ReturnType == "void" && !StatementEmitter.EndsWithReturn(func.Body))
		{
			_cleanup.EmitScopeCleanup(_function, [.. _function.Locals.Keys], skipHeapFree: _function.OwnershipTransferFunction);
			_builder.BuildRetVoid();
		}

		_function.UnsafeDepth = 0;
	}

	/// <summary>
	/// True when the current function is an <c>unbound</c> factory whose return type is a
	/// heap-escaping graph handle (a type transitively carrying <c>ref</c>/<c>refvar</c>
	/// reference fields). In that case the local heap allocations that form the self-referential
	/// graph must NOT be freed on the return path, because ownership transfers to the caller
	/// ('heap-relative provenance'): the references it returns point back into those blocks.
	/// </summary>
	private bool ComputeOwnershipTransfer(TypeSymbol returnType)
	{
		if (!TypeEscapesHeap(returnType))
			return false;

		var funcName = _builder.InsertBlock.Parent.Name;
		return _bindingContext?.Globals.Lookup(funcName) is FunctionSymbol fs && fs.SafetyTier == SafetyTier.Unbound;
	}

	/// <summary>
	/// True if the given type (or any type it transitively contains: struct fields, union
	/// variant payloads, array/slice element types) carries a reference field
	/// (<see cref="PointerTypeSymbol"/> / <see cref="RawPointerTypeSymbol"/>). Such a type is a
	/// graph handle that can point back into a function's heap-allocated data.
	/// </summary>
	private static bool TypeEscapesHeap(TypeSymbol type)
	{
		switch (type)
		{
			case PointerTypeSymbol:
			case RawPointerTypeSymbol:
				return true;
			case StructTypeSymbol structType:
				return structType.Fields.Any(f => TypeEscapesHeap(f.Type));
			case UnionTypeSymbol unionType:
				return unionType.Fields.Any(f => TypeEscapesHeap(f.Type));
			case ArrayTypeSymbol arrayType:
				return TypeEscapesHeap(arrayType.ElementType);
			case SliceTypeSymbol sliceType:
				return TypeEscapesHeap(sliceType.ElementType);
			default:
				return false;
		}
	}




	/// <summary>
	/// Routes expression lowering through the extracted expression emitter.
	/// </summary>
	private LLVMValueRef EmitExpression(ExpressionSyntax expr) => _expressions.Emit(expr);

	/// <summary>
	/// Emits a global string literal through the expression emitter for runtime diagnostics.
	/// </summary>
	private LLVMValueRef EmitStringLiteral(string value) => _expressions.EmitStringLiteral(value);

	/// <summary>
	/// Returns the semantic type of an inline-assembly expression using expression-emitter rules.
	/// </summary>
	private TypeSymbol GetAsmExprType(AsmExpressionSyntax asm) => _expressions.GetAsmExpressionType(asm);

	/// <summary>
	/// Tests whether a call is the constructor form for the requested target type.
	/// </summary>
	private bool IsConstructorCall(CallExpressionSyntax call, TypeSymbol targetType)
		=> _expressions.IsConstructorCall(call, targetType);

	private void CreateExportAlias(string exportName, LLVMValueRef target, LLVMTypeRef funcType)
	{
		// An exported entry forward-declares nothing new: it is a weak-free alias over the
		// internal (mangled) function, exposed to the host binary's dynamic linker.
		var alias = _module.AddAlias2(funcType, 0, target, exportName);
		if (OperatingSystem.IsWindows())
			alias.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		else
			alias.Visibility = LLVMVisibility.LLVMProtectedVisibility;
	}














	private void EmitEnumSwitchTrapDefault()
	{
		if (_llvmTrap is null)
		{
			var trapFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, []);
			_llvmTrap = (trapFnType, _module.AddFunction("llvm.trap", trapFnType));
		}

		_builder.BuildCall2(_llvmTrap.Value.Type, _llvmTrap.Value.Func, new LLVMValueRef[] { }, "");
		_builder.BuildUnreachable();
	}

	private string? ResolveGlobalKey(string shortName)
	{
		if (!_globalShortNames.TryGetValue(shortName, out var candidates))
			return null;

		if (candidates.Count == 1)
			return candidates[0];

		var currentNs = _bindingContext?.CurrentNamespace;
		if (!string.IsNullOrEmpty(currentNs))
		{
			var own = $"{currentNs}.{shortName}";
			if (candidates.Contains(own))
				return own;
		}

		foreach (var ns in _bindingContext?.GetActiveUsings(_bindingContext.CurrentUnit) ?? [])
		{
			var viaKey = $"{ns}.{shortName}";
			if (candidates.Contains(viaKey))
				return viaKey;
		}

		return null;
	}

	private static string? TryExtractQualifiedGlobalKey(ExpressionSyntax expr)
	{
		if (expr is not MemberAccessExpressionSyntax outer)
			return null;

		var segments = new List<string> { outer.MemberName };
		var current = outer.Expression;
		while (current is MemberAccessExpressionSyntax nested)
		{
			if (nested.Expression is not IdentifierExpressionSyntax && nested.Expression is not MemberAccessExpressionSyntax)
				return null;

			segments.Add(nested.MemberName);
			current = nested.Expression;
		}

		if (current is not IdentifierExpressionSyntax leaf)
			return null;

		segments.Add(leaf.Name);
		segments.Reverse();
		return string.Join(".", segments);
	}

	private LLVMValueRef Load(string name)
	{
		if (!_function.Locals.TryGetValue(name, out var ptr))
		{
			if (_function.Locals.TryGetValue("this", out var thisPtr))
			{
				if (_function.VariableTypes["this"] is PointerTypeSymbol thisPtrTy && thisPtrTy.ReferencedType is EnumTypeSymbol enumSelf)
				{
					// Unqualified enum variant access inside an extension body:
					// 'Active' lowers to the variant's compile-time constant.
					var variant = enumSelf.FindVariant(name);
					if (variant is not null)
						return LLVMValueRef.CreateConstInt(GetLLVMType(enumSelf), unchecked((ulong)variant.Value));
				}

				var thisType = _function.VariableTypes["this"] as PointerTypeSymbol;
				var structType = thisType!.ReferencedType as StructTypeSymbol;
				var field = structType?.FindField(name);
				if (field is not null)
				{
					var actualThisPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");

					var fieldIndex = GetFieldIndex(structType, name);
					var structLayoutTy = GetLLVMType(structType);
					var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
					var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

					var fieldPtr = _builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] { zero, index }, "this_field_ptr");
					var thisFieldLoad = _builder.BuildLoad2(GetLLVMType(field.Type), fieldPtr, "this_field_val");
					ApplyTbaa(GetTbaaTag(structType, fieldIndex), thisFieldLoad);
					return thisFieldLoad;
				}
			}

			throw new InvalidOperationException($"Undefined variable '{name}'");
		}

		var type = _function.VariableTypes[name];

		// A heap-allocated owning handle read as a whole denotes the value stored in its
		// heap block (the slot itself only holds the block pointer).
		if (_function.HeapAllocatedVars.Contains(name) && type is StructTypeSymbol heapStruct)
		{
			var innerTy = GetLLVMType(heapStruct);
			var blockPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptr, "heap_block_ptr");
			return _builder.BuildLoad2(innerTy, blockPtr, "heap_load_val");
		}

		var ty = GetLLVMType(type);

		var reg = _builder.BuildLoad2(ty, ptr, "load_val");

		if (type is PointerTypeSymbol ptrType)
		{
			var resolvedType = ptrType.ReferencedType;
			if (resolvedType == TypeSymbol.Int || resolvedType == TypeSymbol.Double || resolvedType == TypeSymbol.Bool || resolvedType == TypeSymbol.Char
				|| resolvedType is EnumTypeSymbol)
			{
				var innerTy = GetLLVMType(resolvedType);
				return _builder.BuildLoad2(innerTy, reg, "deref_val");
			}
		}

		return reg;
	}




	private TypeSymbol GetExprType(ExpressionSyntax expr)
	{
		if (expr is BorrowExpressionSyntax bb)
		{
			var res = new PointerTypeSymbol(GetExprType(bb.Expression), bb.IsMutable);
			return res;
		}

		if (expr is LambdaExpressionSyntax lamTy && _bindingContext!.ResolvedLambdas.TryGetValue(lamTy, out var lamTyInfo))
			return lamTyInfo.Delegate;
		if (_bindingContext!.ResolvedFunctionConversions.TryGetValue(expr, out var convFnTy))
			return _delegates.BuildGroupDelegateType(convFnTy, expr is MemberAccessExpressionSyntax);

		return expr switch
		{
			IntegerLiteralExpressionSyntax intLit => intLit.LiteralType switch
			{
				"uint" => TypeSymbol.UInt,
				"long" => TypeSymbol.Long,
				"ulong" => TypeSymbol.ULong,
				_ => intLit.Value <= (ulong)int.MaxValue ? TypeSymbol.Int : TypeSymbol.Long,
			},
			DoubleLiteralExpressionSyntax dblLit => dblLit.IsFloat ? TypeSymbol.Float : TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			IdentifierExpressionSyntax id => GetExprTypeIdentifier(id),
			MemberAccessExpressionSyntax m => GetMemberAccessType(m),
			IndexExpressionSyntax idx => GetIndexExpressionType(idx),
			BorrowExpressionSyntax b => new PointerTypeSymbol(GetExprType(b.Expression), false),
			StructInitializationExpressionSyntax s => _bindingContext!.ResolveType(s.StructTypeName)!,
			UnaryExpressionSyntax u => ResolveUnaryExprType(u),
			AsmExpressionSyntax asm => GetAsmExprType(asm),
			NameofExpressionSyntax => TypeSymbol.String,
			TypeofExpressionSyntax t => _bindingContext!.ResolveType("System.Type") ?? TypeSymbol.String,
			TernaryExpressionSyntax t => GetExprType(t.ThenExpression),
			CallExpressionSyntax call => ResolveCallReturnType(call),
			BinaryExpressionSyntax bin => ResolveBinaryExpressionType(bin),
			HeapAllocationExpressionSyntax h => GetExprType(h.Expression),
			HeapArrayAllocationExpressionSyntax ha => new SliceTypeSymbol(_bindingContext!.ResolveType(ha.ElementTypeName)!),
			ArrayInitializationExpressionSyntax a => new ArrayTypeSymbol(a.Elements.Count > 0 ? GetExprType(a.Elements[0]) : TypeSymbol.Int, a.Elements.Count),
			_ => TypeSymbol.Int
		};
	}

	private TypeSymbol GetExprTypeIdentifier(IdentifierExpressionSyntax id)
	{
		if (_function.VariableTypes.TryGetValue(id.Name, out var type))
		{
			// Enum 'this' in an extension body reads as the scalar enum value,
			// not the injected receiver pointer.
			if (type is PointerTypeSymbol ptrId && ptrId.ReferencedType is EnumTypeSymbol enumId)
				return enumId;
			return type;
		}

		// Unqualified enum variant access inside an enum extension body.
		if (_function.VariableTypes.TryGetValue("this", out var thisTy)
			&& thisTy is PointerTypeSymbol thisPtr
			&& thisPtr.ReferencedType is EnumTypeSymbol enumSelf
			&& enumSelf.FindVariant(id.Name) is not null)
		{
			return enumSelf;
		}

		// Unqualified struct field access inside a receiver/extension body
		// (e.g. `ptr` meaning `this.ptr`). Mirror the Load() field lookup so
		// type inference (pointer arithmetic etc.) sees the real field type.
		if (_function.VariableTypes.TryGetValue("this", out var thisFieldTy)
			&& thisFieldTy is PointerTypeSymbol thisFieldPtr
			&& thisFieldPtr.ReferencedType is StructTypeSymbol thisFieldStruct
			&& thisFieldStruct.FindField(id.Name) is { } field)
		{
			return field.Type;
		}

		return TypeSymbol.Int;
	}

	private TypeSymbol ResolveUnaryExprType(UnaryExpressionSyntax u)
	{
		if (u.Operator == "*")
		{
			var innerType = GetExprType(u.Operand);
			if (innerType is RawPointerTypeSymbol rawPtr)
				return rawPtr.ElementType;
			return TypeSymbol.Int;
		}
		if (u.Operator == "&")
			return new RawPointerTypeSymbol(GetExprType(u.Operand));
		if (u.Operator.StartsWith("(") && u.Operator.EndsWith(")"))
		{
			var typeName = u.Operator.Substring(1, u.Operator.Length - 2);
			var result = _bindingContext!.ResolveType(typeName)!;
			if (result is EnumTypeSymbol castEnum && _function.UnsafeDepth == 0)
			{
				// Mirrors ValidationPass.GetUnaryExpressionType: safe-zone enum casts
				// from integers produce Option<Enum>; unsafe code gets the raw enum.
				var operandType = GetExprType(u.Operand);
				if (operandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(operandType))
					return _bindingContext.ResolveType($"Option<{castEnum.Name}>") ?? result;
			}

			return result;
		}

		return GetExprType(u.Operand);
	}

	private TypeSymbol ResolveCallReturnType(CallExpressionSyntax call)
	{
		if (_bindingContext!.ResolvedDelegateCalls.TryGetValue(call, out var delegCallTy))
		{
			return delegCallTy.ReturnType;
		}

		if (_bindingContext!.ResolvedCalls.TryGetValue(call, out var resolvedFunc))
		{
			return resolvedFunc.ReturnType;
		}

		var mangledName = ResolveFunctionName(call.FunctionName, _currentUnit!);
		if (call.TypeArguments.Count > 0)
		{
			mangledName = $"{mangledName}<{string.Join(", ", call.TypeArguments)}>";
		}

		return _functionReturnTypes.TryGetValue(mangledName, out var type) ? type : TypeSymbol.Int;
	}

	private TypeSymbol ResolveBinaryExpressionType(BinaryExpressionSyntax bin)
	{
		if (bin.Operator == "+" && ExpressionEmitter.IsConstantStringTree(bin.Left) && ExpressionEmitter.IsConstantStringTree(bin.Right))
		{
			return TypeSymbol.String;
		}

		if (bin.Operator == "==" || bin.Operator == "!=" || bin.Operator == "<" ||
			bin.Operator == ">" || bin.Operator == "<=" || bin.Operator == ">=")
		{
			return TypeSymbol.Bool;
		}

		var lTy = GetExprType(bin.Left);
		var rTy = GetExprType(bin.Right);
		if (lTy.Equals(TypeSymbol.Double) || rTy.Equals(TypeSymbol.Double))
		{
			return TypeSymbol.Double;
		}

		// Integer width promotion: mixed widths yield the wider operand's type
		if (TypeSymbol.IsIntegerType(lTy) && TypeSymbol.IsIntegerType(rTy))
		{
			var lWidth = TypeSymbol.IntegerBitWidth(lTy);
			var rWidth = TypeSymbol.IntegerBitWidth(rTy);
			if (rWidth > lWidth) return rTy;
		}

		return lTy;
	}

	private TypeSymbol GetMemberAccessType(MemberAccessExpressionSyntax m)
	{
		// Enum scoped-variant access: the receiver is an enum type name, not a value.
		// Also exposes the metaprogramming surface: Values (slice), Min/Max/Count (int).
		if (TryResolveEnumVariantReceiver(m) is { } enumMetaType)
		{
			if (enumMetaType.FindVariant(m.MemberName) is not null)
				return enumMetaType;
			if (m.MemberName == "Values")
				return new SliceTypeSymbol(enumMetaType);
			if (m.MemberName is "Min" or "Max" or "Count")
				return TypeSymbol.Int;
			return enumMetaType;
		}

		var parentType = GetExprType(m.Expression);
		if (parentType is PointerTypeSymbol ptr)
		{
			parentType = ptr.ReferencedType;
		}

		if (parentType is SliceTypeSymbol && m.MemberName == "Length")
			return TypeSymbol.Int;

		if (parentType is StructTypeSymbol structType)
		{
			var field = structType.FindField(m.MemberName);
			if (field is not null)
				return field.Type;
		}

		if (parentType is UnionTypeSymbol unionType)
		{
			var field = unionType.FindField(m.MemberName);
			if (field is not null)
				return field.Type;
		}

		return TypeSymbol.Int;
	}

	/// <summary>
	/// Resolves an enum type name used as a scoped-variant-access receiver
	/// (e.g. the 'Status' in 'Status.Active', possibly namespaced). Returns null
	/// when the receiver is a value expression rather than an enum type name.
	/// </summary>
	private EnumTypeSymbol? TryResolveEnumVariantReceiver(MemberAccessExpressionSyntax m)
	{
		var dotted = GetDottedName(m.Expression);
		if (dotted is null)
			return null;

		return _bindingContext!.ResolveType(dotted) as EnumTypeSymbol;
	}

	private static string? GetDottedName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax m && GetDottedName(m.Expression) is { } baseName)
			return $"{baseName}.{m.MemberName}";
		return null;
	}

	private TypeSymbol GetIndexExpressionType(IndexExpressionSyntax idx)
	{
		var parentType = GetExprType(idx.Left);
		return parentType switch
		{
			ArrayTypeSymbol arrayType => arrayType.ElementType,
			SliceTypeSymbol sliceType => sliceType.ElementType,
			_ => TypeSymbol.Int,
		};
	}


	private string ResolveFunctionName(string name, CompilationUnitSyntax activeUnit)
	{
		if (name == "main" || name == "Main")
			return "main";

		if (_globals.ContainsKey(name) || _bindingContext!.GenericFunctionTemplates.ContainsKey(name))
			return name;

		var ns = activeUnit.NamespaceDeclaration?.Name;
		var localMangled = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
		if (_globals.ContainsKey(localMangled) || _bindingContext!.GenericFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		// Use expanded usings from BindingContext
		var activeUsings = _bindingContext!.GetActiveUsings(activeUnit);

		foreach (var importNs in activeUsings)
		{
			var candidateMangled = $"{importNs}.{name}";
			if (_globals.ContainsKey(candidateMangled) || _bindingContext!.GenericFunctionTemplates.ContainsKey(candidateMangled))
				return candidateMangled;
		}

		return name;
	}

	private void InjectBoundsCheck(IndexExpressionSyntax idx, LLVMValueRef indexVal, LLVMValueRef limitVal)
	{
		var currentFunc = _builder.InsertBlock.Parent;
		var safeBlock = currentFunc.AppendBasicBlock("bounds_safe");
		var panicBlock = currentFunc.AppendBasicBlock("bounds_panic");

		var cmp = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, indexVal, limitVal, "is_in_bounds");
		_builder.BuildCondBr(cmp, safeBlock, panicBlock);

		_builder.PositionAtEnd(panicBlock);
		EmitPanicRoutine(idx);

		_builder.PositionAtEnd(safeBlock);
	}

	private void EmitPanicRoutine(IndexExpressionSyntax idx)
	{
		var errorLines = _compilationContext!.FormatDiagnostic("Runtime Error", "Index was outside the bounds of the array.", idx.Span, true);
		var putsFunc = _globals["puts"];
		var putsType = _functionTypes["puts"];
		var exitFunc = _globals["exit"];
		var exitType = _functionTypes["exit"];

		foreach (var line in errorLines)
		{
			var strConstant = EmitStringLiteral(line);
			_builder.BuildCall2(putsType, putsFunc, new LLVMValueRef[] { strConstant }, "puts_call");
		}

		// Passed "" instead of "exit_call" to ensure no void register is assigned
		_builder.BuildCall2(exitType, exitFunc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "");
		_builder.BuildUnreachable();
	}

	private LLVMValueRef BuildGlobalInitializer(TypeSymbol typeSymbol, ExpressionSyntax? initializer, LLVMTypeRef llvmType)
	{
		if (initializer is null)
			return LLVMValueRef.CreateConstNull(llvmType);

		switch (initializer)
		{
			case IntegerLiteralExpressionSyntax intLit:
				return LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)intLit.Value));
			case DoubleLiteralExpressionSyntax dblLit:
				return LLVMValueRef.CreateConstReal(llvmType, dblLit.Value);
			case BooleanLiteralExpressionSyntax boolLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolLit.Value ? 1UL : 0UL);
			case CharacterLiteralExpressionSyntax charLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, charLit.Value);
			case UnaryExpressionSyntax { Operator: "-" } unary:
				switch (unary.Operand)
				{
					case IntegerLiteralExpressionSyntax negInt:
						return LLVMValueRef.CreateConstInt(llvmType, 0UL - negInt.Value);
					case DoubleLiteralExpressionSyntax negDbl:
						return LLVMValueRef.CreateConstReal(llvmType, -negDbl.Value);
					default:
						return LLVMValueRef.CreateConstNull(llvmType);
				}
			case StructInitializationExpressionSyntax structInit when typeSymbol is StructTypeSymbol initStruct
				&& _llvmStructTypes.TryGetValue(initStruct.Name, out var namedStruct):
				{
					var fieldValues = new List<LLVMValueRef>();
					foreach (var field in initStruct.Fields)
					{
						var memberInit = structInit.Initializers.FirstOrDefault(m => m.MemberName == field.Name);
						if (memberInit is not null && IsSimpleConstant(memberInit.Expression))
							fieldValues.Add(BuildGlobalInitializer(field.Type, memberInit.Expression, GetLLVMType(field.Type)));
						else
							fieldValues.Add(LLVMValueRef.CreateConstNull(GetLLVMType(field.Type)));
					}

					return LLVMValueRef.CreateConstNamedStruct(namedStruct, [.. fieldValues]);
				}
			case BinaryExpressionSyntax bin when bin.Operator is "+" or "-" or "*" or "/":
				if (TryEvaluateConstBinary(bin, out var isDoubleResult, out var dblResult, out var intResult))
				{
					var isFloatType = llvmType.Kind is LLVMTypeKind.LLVMDoubleTypeKind or LLVMTypeKind.LLVMFloatTypeKind;
					return isFloatType
						? LLVMValueRef.CreateConstReal(llvmType, isDoubleResult ? dblResult : intResult)
						: LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)intResult));
				}

				return LLVMValueRef.CreateConstNull(llvmType);
			default:
				return LLVMValueRef.CreateConstNull(llvmType);
		}
	}

	private static bool IsSimpleConstant(ExpressionSyntax expr) =>
		expr is IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or BooleanLiteralExpressionSyntax or CharacterLiteralExpressionSyntax;

	/// <summary>
	/// Recursively evaluates a global constant initializer expression tree down to a single
	/// value. Supports integer/double literals, unary minus and binary +, -, *, / (IEEE semantics).
	/// Returns false on unrecognised nodes or integer division by zero.
	/// </summary>
	private static bool TryEvaluateConstBinary(ExpressionSyntax expr, out bool isDouble, out double dbl, out long integer)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax intLit:
				isDouble = false;
				dbl = intLit.Value;
				integer = unchecked((long)intLit.Value);
				return true;
			case DoubleLiteralExpressionSyntax dblLit:
				isDouble = true;
				dbl = dblLit.Value;
				integer = 0;
				return true;
			case BooleanLiteralExpressionSyntax boolLit:
				isDouble = false;
				dbl = boolLit.Value ? 1.0 : 0.0;
				integer = boolLit.Value ? 1 : 0;
				return true;
			case CharacterLiteralExpressionSyntax charLit:
				isDouble = false;
				dbl = charLit.Value;
				integer = charLit.Value;
				return true;
			case UnaryExpressionSyntax { Operator: "-" } unary:
				if (!TryEvaluateConstBinary(unary.Operand, out isDouble, out dbl, out integer))
					return false;
				dbl = -dbl;
				integer = -integer;
				return true;
			case BinaryExpressionSyntax bin:
				if (!TryEvaluateConstBinary(bin.Left, out var lIsDouble, out var lDbl, out var lInt) ||
					!TryEvaluateConstBinary(bin.Right, out var rIsDouble, out var rDbl, out var rInt))
				{
					isDouble = false;
					dbl = 0;
					integer = 0;
					return false;
				}

				isDouble = lIsDouble || rIsDouble;
				if (isDouble)
				{
					var left = lIsDouble ? lDbl : lInt;
					var right = rIsDouble ? rDbl : rInt;
					dbl = bin.Operator switch
					{
						"+" => left + right,
						"-" => left - right,
						"*" => left * right,
						"/" => left / right, // IEEE: 0.0/0.0=NaN (no division-by-zero trap)
						_ => 0,
					};
					integer = 0;
				}
				else
				{
					switch (bin.Operator)
					{
						case "+": integer = lInt + rInt; break;
						case "-": integer = lInt - rInt; break;
						case "*": integer = lInt * rInt; break;
						case "/":
							if (rInt == 0) { isDouble = false; dbl = 0; integer = 0; return false; }
							integer = lInt / rInt;
							break;
						case "%":
							if (rInt == 0) { isDouble = false; dbl = 0; integer = 0; return false; }
							integer = lInt % rInt;
							break;
						default: isDouble = false; dbl = 0; integer = 0; return false;
					}
					dbl = integer;
				}
				return true;
			default:
				isDouble = false;
				dbl = 0;
				integer = 0;
				return false;
		}
	}

	private LLVMTypeRef GetLLVMType(TypeSymbol t)
		=> _types.Lower(t);

	/// <summary>
	/// Returns the C-ABI-correct LLVM type for a Cvolo type crossing a foreign boundary.
	/// Every Cvolo bool is lowered to an unsigned 8-bit integer (i8) at FFI boundaries,
	/// matching the C ABI 1-byte boolean representation.
	/// </summary>
	private LLVMTypeRef GetFFIType(TypeSymbol t)
	{
		if (t is not null && t.Name == "bool")
			return LLVMTypeRef.Int8;
		return GetLLVMType(t);
	}

	private (LLVMValueRef ptr, TypeSymbol type, bool valueProvenance, LLVMValueRef? tbaa) GetFieldPointer(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
		{
			if (!_function.Locals.TryGetValue(id.Name, out var structPtr))
			{
				// If the identifier is a field/variant of 'this' in an extension block, resolve its pointer implicitly!
				if (_function.Locals.TryGetValue("this", out var thisPtr))
				{
					var thisType = _function.VariableTypes["this"] as PointerTypeSymbol;
					var refType = thisType!.ReferencedType;

					if (refType is StructTypeSymbol structType)
					{
						var field = structType.FindField(id.Name);
						if (field is not null)
						{
							var actualThisPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");

							var fieldIndex = GetFieldIndex(structType, id.Name);
							var structLayoutTy = GetLLVMType(structType);
							var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
							var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

							var fieldPtr = _builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] { zero, index }, "this_field_ptr");
							return (fieldPtr, field.Type, true, GetTbaaTag(structType, fieldIndex));
						}
					}
					else if (refType is UnionTypeSymbol unionType)
					{
						var field = unionType.FindField(id.Name);
						if (field is not null)
						{
							var actualThisPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");

							// Null-Pointer Optimization: the flat slot IS the value (a ref/refvar pointer).
							// Some = value stores directly; there is no tag/payload struct to index into.
							if (unionType.IsNpoEligible && !field.IsVoidVariant)
								return (actualThisPtr, field.Type, false, null);

							var fieldIndex = GetFieldIndex(unionType, id.Name);
							var structLayoutTy = GetLLVMType(unionType);

							// For unions, access the payload (index 1 of the struct) and cast it to the variant's concrete type
							var payloadPtr = _builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] {
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
							}, "union_payload_ptr");

							var castPtr = _builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(field.Type), 0), "payload_cast_ptr");
							return (castPtr, field.Type, false, null);
						}
					}
				}

				throw new InvalidOperationException($"Undefined variable '{id.Name}'");
			}

			var type = _function.VariableTypes[id.Name];
			var isReference = type is PointerTypeSymbol;
			var isHeap = _function.HeapAllocatedVars.Contains(id.Name) && type is not SliceTypeSymbol;

			if (isReference || isHeap)
			{
				var actualPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), structPtr, "loaded_ptr");
				var innerType = type is PointerTypeSymbol ptrType ? ptrType.ReferencedType : type;
				// ref/refvar/heap borrows all address a typed struct directly; members are
				// tagged the same way on every access so LLVM can use !tbaa to disambiguate
				// between distinct struct types. Soundness is preserved by GetFieldTag, which
				// still refuses structs carrying reference-layer fields, plus the unsafe gate.
				return (actualPtr, innerType, true, null);
			}

			return (structPtr, type, true, null);
		}
		else if (expr is MemberAccessExpressionSyntax m)
		{
			// Namespace-qualified global receiver: 'NS.Point.X' addresses the global slot
			// directly (the GlobalVariable IS a pointer), so chained member reads/writes work.
			if (TryExtractQualifiedGlobalKey(m) is { } globalBaseKey
				&& _globalVariables.TryGetValue(globalBaseKey, out var globalBasePtr))
			{
				return (globalBasePtr, _globalVariableTypes[globalBaseKey], true, null);
			}

			// Enum metaprogramming: EnumName.Values is a slice backed by a .rodata global.
			// The receiver is a type name (not a value), so it must be handled before the
			// parent lookup below.
			if (TryResolveEnumVariantReceiver(m) is { } enumValuesType && m.MemberName == "Values")
			{
				var (enumPtr, enumType) = EmitEnumValuesSlicePointer(enumValuesType);
				return (enumPtr, enumType, false, null);
			}

			var (parentPtr, parentType, valueProvenance, _) = GetFieldPointer(m.Expression);

			if (parentType is SliceTypeSymbol sliceType && m.MemberName == "Length")
			{
				var structLayout = GetLLVMType(sliceType);
				var lengthPtr = _builder.BuildGEP2(structLayout, parentPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "len_ptr");
				return (lengthPtr, TypeSymbol.Int, false, null);
			}

			// Dot access through a reference field (auto-deref): parentPtr addresses a
			// pointer slot holding the referenced struct; load it, then GEP into the
			// referenced struct's field so both reads and writes work.
			if (parentType is PointerTypeSymbol refPtrType)
			{
				var referred = refPtrType.ReferencedType;
				var refStruct = referred as StructTypeSymbol ?? _bindingContext?.ResolveType(referred.Name) as StructTypeSymbol;
				if (refStruct is not null)
				{
					var rawPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), parentPtr, "reffield_load");
					var refFieldIndex = GetFieldIndex(refStruct, m.MemberName);
					var refFieldType = refStruct.Fields[refFieldIndex].Type;

					var refStructLayoutTy = GetLLVMType(refStruct);
					var refZero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
					var refIndex = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)refFieldIndex);

					var refFieldPtr = _builder.BuildGEP2(refStructLayoutTy, rawPtr, new LLVMValueRef[] { refZero, refIndex }, "reffield_member_ptr");
					return (refFieldPtr, refFieldType, false, GetTbaaTag(refStruct, refFieldIndex));
				}

				parentType = referred;
			}

			// Arrow operator: parentPtr is a pointer to a struct pointer; load it first
			if (m.Operator == "->")
			{
				var rawPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), parentPtr, "arrow_load");
				var structType = (parentType as StructTypeSymbol)
					?? (parentType is RawPointerTypeSymbol rpt ? rpt.ElementType as StructTypeSymbol : null)
					?? (parentType is PointerTypeSymbol pt ? pt.ReferencedType as StructTypeSymbol : null)
					?? _bindingContext?.ResolveType(parentType.Name) as StructTypeSymbol;

				if (structType is null)
				{
					throw new InvalidOperationException($"Cannot resolve struct type for arrow operator on '{parentType.Name}'");
				}

				var fieldIndex = GetFieldIndex(structType, m.MemberName);
				var fieldType = structType.Fields[fieldIndex].Type;

				var structLayoutTy = GetLLVMType(structType);
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

				var fieldPtr = _builder.BuildGEP2(structLayoutTy, rawPtr, new LLVMValueRef[] { zero, index }, "arrow_field_ptr");
				return (fieldPtr, fieldType, false, GetTbaaTag(structType, fieldIndex));
			}

			// Ensure parentType is resolved to concrete StructTypeSymbol or UnionTypeSymbol
			if (parentType is not (StructTypeSymbol or UnionTypeSymbol) && _bindingContext?.ResolveType(parentType.Name) is TypeSymbol resolvedParent)
			{
				parentType = resolvedParent;
			}

			if (parentType is UnionTypeSymbol unionType)
			{
				var fieldIndex = GetFieldIndex(unionType, m.MemberName);
				var fieldType = unionType.Fields[fieldIndex].Type;

				// Null-Pointer Optimization: the flat slot IS the ref/refvar value. Reading u.Some
				// yields the flat pointer itself; there is no tag/payload struct to index into.
				if (unionType.IsNpoEligible && !unionType.Fields[fieldIndex].IsVoidVariant)
					return (parentPtr, fieldType, false, null);

				var structLayoutTy = GetLLVMType(parentType);
				var payloadPtr = _builder.BuildGEP2(structLayoutTy, parentPtr, new LLVMValueRef[] {
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
			}, "union_payload_ptr");

				var castPtr = SafeBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(fieldType), 0), "payload_cast_ptr");
				return (castPtr, fieldType, false, null);
			}

			var dotStructType = (parentType as StructTypeSymbol)
				?? _bindingContext?.ResolveType(parentType.Name) as StructTypeSymbol;

			if (dotStructType is null)
			{
				throw new InvalidOperationException($"Type '{parentType.Name}' is not a struct; cannot access member '{m.MemberName}'");
			}

			var dotFieldIndex = GetFieldIndex(dotStructType, m.MemberName);
			var dotFieldType = dotStructType.Fields[dotFieldIndex].Type;

			var dotStructLayoutTy = GetLLVMType(dotStructType);
			var dotZero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			var dotIndex = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)dotFieldIndex);

			var dotFieldPtr = _builder.BuildGEP2(dotStructLayoutTy, parentPtr, new LLVMValueRef[] { dotZero, dotIndex }, "member_ptr");
			return (dotFieldPtr, dotFieldType, valueProvenance, valueProvenance ? GetTbaaTag(dotStructType, dotFieldIndex) : null);
		}
		else if (expr is IndexExpressionSyntax idx)
		{
			var (parentPtr, parentType, _, _) = GetFieldPointer(idx.Left);
			var indexVal = EmitExpression(idx.Index);

			if (parentType is SliceTypeSymbol sliceType)
			{
				var sliceLayout = GetLLVMType(sliceType);

				// Get pointer to slice buffer (Index 0 of fat pointer)
				var arrPtrField = _builder.BuildGEP2(sliceLayout, parentPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "arr_field");
				var arrayPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), arrPtrField, "arr_ptr");

				// Get slice length (Index 1 of fat pointer)
				var lenPtrField = _builder.BuildGEP2(sliceLayout, parentPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "len_field");
				var lengthReg = _builder.BuildLoad2(LLVMTypeRef.Int32, lenPtrField, "len_val");

				InjectBoundsCheck(idx, indexVal, lengthReg);

				var elementLlvmTy = GetLLVMType(sliceType.ElementType);
				var elementPtr = _builder.BuildGEP2(elementLlvmTy, arrayPtr, new LLVMValueRef[] { indexVal }, "element_ptr");
				return (elementPtr, sliceType.ElementType, false, null);
			}
			else if (parentType is ArrayTypeSymbol arrayType)
			{
				var arrayLayout = GetLLVMType(arrayType);
				var limit = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)arrayType.Size);

				InjectBoundsCheck(idx, indexVal, limit);

				var elementPtr = _builder.BuildGEP2(arrayLayout, parentPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), indexVal }, "element_ptr");
				return (elementPtr, arrayType.ElementType, false, null);
			}
		}
		else if (expr is BorrowExpressionSyntax b)
		{
			return GetFieldPointer(b.Expression); // Unpack the inner expression pointer recursively
		}
		else if (expr is UnaryExpressionSyntax castExpr && castExpr.Operator.StartsWith('(') && castExpr.Operator.EndsWith(')') && _function.UnsafeDepth == 0)
		{
			// A safe-zone (Enum)integer cast evaluates to Option<Enum>; as a field-pointer
			// (used by switch over the cast), materialize the tagged-union temporary.
			var castTypeName = castExpr.Operator.Substring(1, castExpr.Operator.Length - 2);
			if (_bindingContext!.ResolveType(castTypeName) is EnumTypeSymbol castEnum)
			{
				var castOperandType = GetExprType(castExpr.Operand);
				if (castOperandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(castOperandType) &&
					_bindingContext.ResolveType($"Option<{castEnum.Name}>") is UnionTypeSymbol castOption)
				{
					var castOperand = EmitExpression(castExpr.Operand);
					var (castPtr, castType) = MaterializeEnumCastOption(castEnum, castOperand, castOperandType, castOption);
					return (castPtr, castType, false, null);
				}
			}
		}
		else if (expr is CallExpressionSyntax call)
		{
			var retType = GetExprType(call);
			var callVal = _calls.Emit(call);

			// 1. If call returns a reference/pointer (e.g. 'ref Point'), return the pointer directly
			if (retType is PointerTypeSymbol ptrType)
			{
				var inner = ptrType.ReferencedType;
				if (inner is not (StructTypeSymbol or UnionTypeSymbol) && _bindingContext?.ResolveType(inner.Name) is TypeSymbol resolvedInner)
					inner = resolvedInner;
				return (callVal, inner, false, null);
			}

			if (retType is RawPointerTypeSymbol rawPtrType)
			{
				var inner = rawPtrType.ElementType;
				if (inner is not (StructTypeSymbol or UnionTypeSymbol) && _bindingContext?.ResolveType(inner.Name) is TypeSymbol resolvedInner)
					inner = resolvedInner;
				return (callVal, inner, false, null);
			}

			// 2. If call returns a struct or union by value, spill to a stack temporary to allow field GEP
			var structType = retType as StructTypeSymbol ?? _bindingContext?.ResolveType(retType.Name) as StructTypeSymbol;
			if (structType is not null)
			{
				var structLayout = GetLLVMType(structType);
				var tempAlloc = _builder.BuildAlloca(structLayout, "call_struct_tmp");
				_builder.BuildStore(callVal, tempAlloc);
				return (tempAlloc, structType, false, null);
			}

			var unionType = retType as UnionTypeSymbol ?? _bindingContext?.ResolveType(retType.Name) as UnionTypeSymbol;
			if (unionType is not null)
			{
				var unionLayout = GetLLVMType(unionType);
				var tempAlloc = _builder.BuildAlloca(unionLayout, "call_union_tmp");
				_builder.BuildStore(callVal, tempAlloc);
				return (tempAlloc, unionType, false, null);
			}

			return (callVal, retType, false, null);
		}

		throw new InvalidOperationException($"Unsupported {expr.GetType()} field pointer expression");
	}

	private TbaaMetadata Tbaa => _tbaa ??= new TbaaMetadata(_context, _module, s => GetLLVMType(s));

	private LLVMValueRef? GetTbaaTag(StructTypeSymbol structType, int fieldIndex)
	{
		if (!_enableTbaa || _function.UnsafeDepth != 0 || !TbaaMetadata.IsScalar(structType.Fields[fieldIndex].Type))
			return null;

		return Tbaa.GetFieldTag(structType, fieldIndex);
	}

	private void ApplyTbaa(LLVMValueRef? tag, LLVMValueRef instruction)
	{
		if (tag is not null)
			instruction.SetMetadata(Tbaa.TbaaKindId, tag.Value);
	}

	private int GetFieldIndex(StructTypeSymbol type, string name)
	{
		for (var i = 0; i < type.Fields.Count; i++)
		{
			if (type.Fields[i].Name == name)
			{
				return i;
			}
		}

		throw new KeyNotFoundException($"Field {name} not found in struct {type.Name}");
	}

	private int GetFieldIndex(UnionTypeSymbol type, string name)
	{
		for (var i = 0; i < type.Fields.Count; i++)
		{
			if (type.Fields[i].Name == name)
			{
				return i;
			}
		}

		throw new KeyNotFoundException($"Variant {name} not found in union {type.Name}");
	}

	/// <summary>
	/// (Â§5.B) Materializes EnumName.Values as a read-only slice backed by a single
	/// .rodata global. Returns the address of a stack temp holding the slice.
	/// </summary>
	private (LLVMValueRef ptr, TypeSymbol type) EmitEnumValuesSlicePointer(EnumTypeSymbol enumType)
	{
		var sliceType = new SliceTypeSymbol(enumType);
		var sliceLayout = GetLLVMType(sliceType);
		var elementTy = GetLLVMType(enumType.StorageType);
		var count = enumType.IsFlags
			? enumType.Variants.Count(v => v.Value > 0 && IsPowerOfTwo(v.Value))
			: enumType.Variants.Count;
		var global = GetOrCreateEnumValuesGlobal(enumType, elementTy, count);

		var tmp = _builder.BuildAlloca(sliceLayout, "values_slice");
		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1);
		var arrPtr = _builder.BuildGEP2(sliceLayout, tmp, new LLVMValueRef[] { zero, zero }, "values_arr_ptr");
		_builder.BuildStore(_builder.BuildBitCast(global, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "values_arr_cast"), arrPtr);
		var lenPtr = _builder.BuildGEP2(sliceLayout, tmp, new LLVMValueRef[] { zero, one }, "values_len_ptr");
		_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)count), lenPtr);
		return (tmp, sliceType);
	}

	private LLVMValueRef GetOrCreateEnumValuesGlobal(EnumTypeSymbol enumType, LLVMTypeRef elementTy, int count)
	{
		if (_enumValuesGlobals.TryGetValue(enumType.Name, out var existing))
		{
			return existing;
		}

		var constElems = enumType.IsFlags
			? enumType.Variants.Where(v => v.Value > 0 && IsPowerOfTwo(v.Value))
				.Select(v => LLVMValueRef.CreateConstInt(elementTy, unchecked((ulong)v.Value))).ToArray()
			: enumType.Variants
				.Select(v => LLVMValueRef.CreateConstInt(elementTy, unchecked((ulong)v.Value))).ToArray();
		var global = _module.AddGlobal(LLVMTypeRef.CreateArray(elementTy, (uint)count), $"enum_values_{enumType.Name}");
		global.Initializer = LLVMValueRef.CreateConstArray(elementTy, constElems);
		global.IsGlobalConstant = true;
		_enumValuesGlobals[enumType.Name] = global;
		return global;
	}

	private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;

	/// <summary>
	/// Builds a tagged-union Option&lt;Enum&gt; temporary for a safe-zone (Enum)integer cast:
	/// the operand is normalized to the enum's storage width, then compared against every
	/// declared variant value; a match stores the Some tag + payload, otherwise the None tag.
	/// </summary>
	private (LLVMValueRef ptr, UnionTypeSymbol unionType) MaterializeEnumCastOption(
		EnumTypeSymbol enumType, LLVMValueRef operand, TypeSymbol operandType, UnionTypeSymbol optionUnion)
	{
		var storageTy = GetLLVMType(enumType.StorageType);

		var normalized = operand;
		var operandWidth = TypeSymbol.IntegerBitWidth(operandType);
		var storageWidth = TypeSymbol.IntegerBitWidth(enumType.StorageType);
		if (operandWidth > storageWidth)
			normalized = _builder.BuildTrunc(normalized, storageTy, "ecast_trunc");
		else if (operandWidth < storageWidth)
			normalized = TypeSymbol.IsSignedIntegerType(operandType)
				? _builder.BuildSExt(normalized, storageTy, "ecast_sext")
				: _builder.BuildZExt(normalized, storageTy, "ecast_zext");

		var unionLayout = GetLLVMType(optionUnion);
		var tmp = _builder.BuildAlloca(unionLayout, "ecast_tmp");

		var someIndex = GetFieldIndex(optionUnion, "Some");
		var noneIndex = GetFieldIndex(optionUnion, "None");
		var someTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)someIndex);
		var noneTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)noneIndex);

		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1);
		var tagPtr = _builder.BuildGEP2(unionLayout, tmp, new LLVMValueRef[] { zero, zero }, "ecast_tag");
		var payloadPtr = _builder.BuildGEP2(unionLayout, tmp, new LLVMValueRef[] { zero, one }, "ecast_payload");

		var currentFunc = _builder.InsertBlock.Parent;
		var join = currentFunc.AppendBasicBlock("ecast_join");
		var nextCheck = _builder.InsertBlock;

		foreach (var variant in enumType.Variants)
		{
			var matchBlock = currentFunc.AppendBasicBlock($"ecast_{variant.Name}");
			var afterBlock = currentFunc.AppendBasicBlock($"ecast_{variant.Name}_next");

			_builder.PositionAtEnd(nextCheck);
			var variantConst = LLVMValueRef.CreateConstInt(storageTy, unchecked((ulong)variant.Value));
			var matches = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, normalized, variantConst, "ecast_cmp");
			_builder.BuildCondBr(matches, matchBlock, afterBlock);

			_builder.PositionAtEnd(matchBlock);
			_builder.BuildStore(someTag, tagPtr);
			_builder.BuildStore(normalized, payloadPtr);
			_builder.BuildBr(join);

			nextCheck = afterBlock;
		}

		_builder.PositionAtEnd(nextCheck);
		_builder.BuildStore(noneTag, tagPtr);
		_builder.BuildBr(join);

		_builder.PositionAtEnd(join);
		return (tmp, optionUnion);
	}



	public void Dispose()
	{
		_builder.Dispose();
		_module.Dispose();
	}


	public int GetByteSize(TypeSymbol type)
	{
		if (type is null) return 0;
		if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol or RawPointerTypeSymbol) return 8; // 64-bit pointers
		if (type is SliceTypeSymbol) return 16; // Fat Pointer: { ptr, i32 }
		if (type is DelegateTypeSymbol) return 16; // Two-word safe delegate: { invoke thunk, context }
		if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt) || type.Equals(TypeSymbol.Float)) return 4;
		if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.NInt) || type.Equals(TypeSymbol.NUInt) || type.Equals(TypeSymbol.Double)) return 8;
		if (type.Equals(TypeSymbol.Short) || type.Equals(TypeSymbol.UShort)) return 2;
		if (type.Equals(TypeSymbol.SByte) || type.Equals(TypeSymbol.Byte) || type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char)) return 1;
		if (type is ArrayTypeSymbol arr) return GetByteSize(arr.ElementType) * arr.Size;
		if (type is StructTypeSymbol structType)
		{
			var size = 0;
			foreach (var field in structType.Fields)
			{
				size += GetByteSize(field.Type);
			}

			return size;
		}

		if (type is UnionTypeSymbol unionType)
		{
			// Null-Pointer Optimization: a flat Option<ref T> / <refvar T> is a single 8-byte pointer.
			if (unionType.IsNpoEligible)
				return 8;

			// Tagged union: 1-byte tag + (largest non-void variant) payload.
			var maxPayload = unionType.Fields.Where(f => !f.IsVoidVariant).Select(f => GetByteSize(f.Type)).DefaultIfEmpty(0).Max();
			return 1 + maxPayload;
		}

		if (type is EnumTypeSymbol enumType)
			return GetByteSize(enumType.StorageType);

		return 4; // Fallback
	}

					/// <summary>

	private LLVMValueRef SafeBitCast(LLVMValueRef value, LLVMTypeRef targetType, string name = "")
	{
		if (value.TypeOf.Handle == targetType.Handle)
			return value;

		if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && targetType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			return value;

		return _builder.BuildBitCast(value, targetType, name);
	}

}
