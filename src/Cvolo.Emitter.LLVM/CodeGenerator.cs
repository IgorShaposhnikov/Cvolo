using Cvolo.Analysis;
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
	private readonly DeclarationEmitter _declarations;
	private readonly GlobalEmitter _globalEmitter;
	private readonly CleanupEmitter _cleanup;
	private readonly MemoryEmitter _memory;
	private readonly AggregateEmitter _aggregates;
	private readonly CallEmitter _calls;
	private readonly DelegateEmitter _delegates;
	private readonly ValueCoercion _coercion;
	private readonly ExpressionTypeResolver _expressionTypes;
	private readonly StatementEmitter _statements;
	private readonly FunctionEmitter _functions;
	private readonly ExpressionEmitter _expressions;
	private readonly ILLVMOptimizer? _optimizer;
	private readonly IRVerifier? _irVerifier;

	// Keep migration aliases local to CodeGenerator so this commit changes ownership,
	// not hundreds of emission call sites at once.
	private LLVMModuleRef _module => _codegen.Module;
	private LLVMBuilderRef _builder => _codegen.Builder;
	private LlvmTypeLowering _types => _codegen.Types;
	private Dictionary<string, List<string>> _globalShortNames => _codegen.GlobalShortNames;
	private BindingContext? _bindingContext { get => _codegen.BindingContext; set => _codegen.BindingContext = value; }
	private CompilationContext? _compilationContext { get => _codegen.CompilationContext; set => _codegen.CompilationContext = value; }
	private CompilationUnitSyntax? _currentUnit { get => _codegen.CurrentUnit; set => _codegen.CurrentUnit = value; }

	private FunctionCodegenContext _function = new();
	private (LLVMTypeRef Type, LLVMValueRef Func)? _llvmTrap;

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
		_declarations = new DeclarationEmitter(_codegen, GetFFIType, definedGlobalNames);
		_globalEmitter = new GlobalEmitter(_codegen, definedGlobalNames);
		_cleanup = new CleanupEmitter(_codegen);
		_memory = new MemoryEmitter(_codegen);
		_coercion = new ValueCoercion(_codegen);
		_expressionTypes = new ExpressionTypeResolver(_codegen, () => _function);
		_aggregates = new AggregateEmitter(
			_codegen,
			_memory,
			() => _function,
			EmitExpression,
			EmitStringLiteral,
			_expressionTypes,
			EmitCallForAggregateAddress,
			enableTbaa);
		_calls = new CallEmitter(
			_codegen,
			_cleanup,
			_aggregates,
			() => _function,
			EmitExpression,
			_expressionTypes,
			_coercion,
			Load,
			_declarations.ExternDeclarations,
			_declarations.ExternBlockFunctions);
		_statements = new StatementEmitter(
			_codegen,
			_cleanup,
			_memory,
			_aggregates,
			_calls,
			_coercion,
			() => _function,
			EmitExpression,
			_expressionTypes,
			EmitEnumSwitchTrapDefault);
		_functions = new FunctionEmitter(
			_codegen,
			_cleanup,
			() => _function,
			function => _function = function,
			GetFFIType,
			ResolveGlobalKey,
			TypeEscapesHeap,
			(call, thisPointer) => _calls.Emit(call, thisPointer),
			_statements.EmitBlock,
			EmitEnumSwitchTrapDefault,
			_declarations.ConstructorInitializers,
			checkedFfiBounds);
		_delegates = new DelegateEmitter(
			_codegen,
			() => _function,
			function => _function = function,
			EmitExpression,
			_statements.EmitBlock,
			_expressionTypes,
			_coercion,
			Load,
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
			_expressionTypes,
			Load,
			ResolveGlobalKey,
			TypeEscapesHeap,
			SafeBitCast);

		_optimizer = optimizer;
		_irVerifier = irVerifier;
	}

	public LLVMModuleRef Module => _module;

	public string Emit(IReadOnlyList<CompilationUnitSyntax> units, CompilationContext context, BindingContext bindingContext)
	{
		_bindingContext = bindingContext;
		_compilationContext = context;

		// Preserve the existing declaration order while delegating module-level work to
		// dedicated emitters. Function definitions are still orchestrated below.
		_declarations.DeclareRuntimeSupport();
		_declarations.DeclareAggregateTypes();
		_globalEmitter.EmitGlobals();
		_declarations.DeclareSourceSignatures(units);
		_declarations.DeclareGenericSpecializations(units);

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
						_functions.EmitBody(func, overloadedMangledName);
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
							_functions.EmitBody(exportFunc, exportedOverloadedName);
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
									_functions.EmitBody(method, candidate.Name);
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
								var ctorDecl = _declarations.FindConstructorDeclaration(extDecl.Constructors, candidate);
								if (ctorDecl is not null)
									_functions.EmitBody(ctorDecl.ToFunctionDeclaration(), candidate.Name);
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

				_functions.EmitBody(instDecl, instDecl.Name);
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
					_functions.EmitBody(func, emitName);
				}
				else if (decl is ConstructorDeclarationSyntax ctor)
				{
					_functions.EmitBody(ctor.ToFunctionDeclaration(), emitName);
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
	/// Emits calls requested by aggregate address materialization after the call emitter has been wired.
	/// </summary>
	private LLVMValueRef EmitCallForAggregateAddress(CallExpressionSyntax call) => _calls.Emit(call);

	/// <summary>
	/// Emits a global string literal through the expression emitter for runtime diagnostics.
	/// </summary>
	private LLVMValueRef EmitStringLiteral(string value) => _expressions.EmitStringLiteral(value);



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

					var fieldIndex = _codegen.AggregateLayout.GetFieldIndex(structType, name);
					var structLayoutTy = GetLLVMType(structType);
					var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
					var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

					var fieldPtr = _builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] { zero, index }, "this_field_ptr");
					var thisFieldLoad = _builder.BuildLoad2(GetLLVMType(field.Type), fieldPtr, "this_field_val");
					_aggregates.ApplyTbaa(_aggregates.GetTbaaTag(structType, fieldIndex), thisFieldLoad);
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





	public void Dispose()
	{
		_builder.Dispose();
		_module.Dispose();
	}


	private LLVMValueRef SafeBitCast(LLVMValueRef value, LLVMTypeRef targetType, string name = "")
	{
		if (value.TypeOf.Handle == targetType.Handle)
			return value;

		if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && targetType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			return value;

		return _builder.BuildBitCast(value, targetType, name);
	}

}
