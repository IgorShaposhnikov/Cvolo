using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Emitter.LLVM.Codegen;
using Cvolo.Emitter.LLVM.Codegen.Emitters;
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
	private readonly ValueLoader _values;
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
		_values = new ValueLoader(_codegen, _aggregates, () => _function);
		_calls = new CallEmitter(
			_codegen,
			_cleanup,
			_aggregates,
			() => _function,
			EmitExpression,
			_expressionTypes,
			_coercion,
			_values,
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
			_values,
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
			_values);
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
			_values);

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


	/// <summary>
	/// Returns the C-ABI-correct LLVM type for a Cvolo type crossing a foreign boundary.
	/// Every Cvolo bool is lowered to an unsigned 8-bit integer (i8) at FFI boundaries,
	/// matching the C ABI 1-byte boolean representation.
	/// </summary>
	private LLVMTypeRef GetFFIType(TypeSymbol t)
	{
		if (t is not null && t.Name == "bool")
			return LLVMTypeRef.Int8;
		return _codegen.Types.Lower(t);
	}





	public void Dispose()
	{
		_builder.Dispose();
		_module.Dispose();
	}


}
