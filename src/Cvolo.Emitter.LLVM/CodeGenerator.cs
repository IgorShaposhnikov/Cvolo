using System.Runtime.InteropServices;
using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

public sealed class CodeGenerator : IEmitter, IDisposable
{
	private readonly LLVMModuleRef _module;
	private readonly LLVMBuilderRef _builder;
	private readonly LLVMContextRef _context; // Holds the native LLVM Context
	private readonly ILLVMOptimizer? _optimizer;

	// Metadata Cache Dictionaries
	private readonly Dictionary<string, LLVMValueRef> _globals = [];
	private readonly Dictionary<string, LLVMValueRef> _locals = [];
	private readonly Dictionary<string, LLVMTypeRef> _functionTypes = [];
	private readonly Dictionary<string, LLVMTypeRef> _llvmStructTypes = [];
	private readonly Dictionary<string, TypeSymbol> _variableTypes = [];
	private readonly HashSet<string> _heapAllocatedVars = [];
	private readonly HashSet<string> _movedVars = [];
	private readonly Dictionary<string, StructDeclarationSyntax> _astStructs = [];
	private readonly Dictionary<string, ExternDeclarationSyntax> _astExterns = [];
	private readonly Dictionary<string, List<TypeSymbol>> _functionParameterTypes = [];
	private readonly Dictionary<string, TypeSymbol> _functionReturnTypes = [];
	private readonly Dictionary<string, LLVMValueRef> _globalVariables = [];
	private readonly Dictionary<string, TypeSymbol> _globalVariableTypes = [];
	private BindingContext? _bindingContext;
	private CompilationContext? _compilationContext; // Renamed to avoid LLVM _context conflict
	private CompilationUnitSyntax? _currentUnit;
	private readonly HashSet<string> _disposedVars = [];
	private int _unsafeDepth;
	private (LLVMTypeRef Type, LLVMValueRef Func)? _llvmTrap;
	public CodeGenerator(string moduleName, ILLVMOptimizer? optimizer = null)
	{
		_context = LLVMContextRef.Global;
		_module = _context.CreateModuleWithName(moduleName);
		_builder = _context.CreateBuilder();
		_optimizer = optimizer;
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

		// memset(void* dest, int value, size_t count) -> void* — used for `{}` zero-init arrays
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
			if (_globalVariables.ContainsKey(globalNode.Name))
				continue;

			var llvmType = GetLLVMType(globalSymbol.Type);
			var globalRef = _module.AddGlobal(llvmType, globalNode.Name);
			globalRef.IsGlobalConstant = !globalSymbol.IsMutable;
			globalRef.Linkage = LLVMLinkage.LLVMInternalLinkage;
			globalRef.Initializer = BuildGlobalInitializer(globalSymbol.Type, globalNode.Initializer, llvmType);
			_globalVariables[globalNode.Name] = globalRef;
			_globalVariableTypes[globalNode.Name] = globalSymbol.Type;
		}

		// Pass C: Declare Extern functions and custom user-defined function signatures
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			bindingContext.CurrentUnit = unit;
			bindingContext.CurrentNamespace = ns;
			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				switch (member)
				{
					case ExternDeclarationSyntax ext:
						_astExterns[ext.Name] = ext;
						DeclareExternFunction(ext);
						break;
					case FunctionDeclarationSyntax func when func.GenericParameters.Count == 0 && !func.Name.Contains('<'):
						// Keep 'main' / 'Main' global and unmangled
						var mangledName = (func.Name == "main" || func.Name == "Main")
							? "main"
							: bindingContext.GetMangledName(func.Name, ns);

						// --- FIX: Mangle function registration based on parameter types ---
						var paramTypes = func.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
						var overloadedMangledName = bindingContext.GetOverloadedMangledName(mangledName, paramTypes);
						DeclareFunction(func, overloadedMangledName);
						break;
					case ExtensionDeclarationSyntax extDecl:
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
									DeclareFunction(ctorDecl.ToFunctionDeclaration(), candidate.Name);
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
			}
		}

		// Pass E: Generate bodies of Regular and Monomorphized functions
		var emittedFunctionNames = new HashSet<string>();

		foreach (var unit in units)
		{
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

					foreach (var ctorDecl in extDecl.Constructors)
					{
						var ctorBaseMangledName = bindingContext.GetMangledName(extDecl.ExtendedTypeName, ns);
						if (bindingContext.OverloadedFunctions.TryGetValue(ctorBaseMangledName, out var ctorCandidates))
						{
							foreach (var candidate in ctorCandidates)
							{
								if (emittedFunctionNames.Add(candidate.Name))
								{
									EmitFunctionBody(ctorDecl.ToFunctionDeclaration(), candidate.Name);
								}
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

		_optimizer?.Optimize(_module);

		return _module.PrintToString();
	}

	private void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		// Deduplicate: If this extern function has already been declared, return early
		if (_globals.ContainsKey(ext.Name))
			return;

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

	private void DeclareFunction(FunctionDeclarationSyntax func, string emitName)
	{
		// Deduplicate: If this function has already been declared, return early
		if (_globals.ContainsKey(emitName))
			return;

		var returnTypeSymbol = _bindingContext!.ResolveType(func.ReturnType)!;
		var returnType = GetLLVMType(returnTypeSymbol);
		_functionReturnTypes[emitName] = returnTypeSymbol;

		var paramTypes = new List<LLVMTypeRef>();
		var paramSymbols = new List<TypeSymbol>();
		if (_bindingContext.Globals.Lookup(emitName) is FunctionSymbol sym)
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

		if (emitName != "main")
		{
			llvmFunc.Linkage = LLVMLinkage.LLVMInternalLinkage;
		}

		// Attach noalias attributes if [NoAlias] is present on the function or individual parameters
		if (_bindingContext!.Globals.Lookup(emitName) is FunctionSymbol funcSym)
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
		}

		_globals[emitName] = llvmFunc;
		_functionTypes[emitName] = funcType;
	}

	private void EmitFunctionBody(FunctionDeclarationSyntax func, string mangledName)
	{
		if (!_globals.TryGetValue(mangledName, out var llvmFunc))
			return;

		var entry = llvmFunc.AppendBasicBlock("entry");
		_builder.PositionAtEnd(entry);

		_locals.Clear();
		_variableTypes.Clear();
		_heapAllocatedVars.Clear();
		_movedVars.Clear();
		_disposedVars.Clear();

		var funcSymbol = _bindingContext!.Globals.Lookup(mangledName) as FunctionSymbol;
		_unsafeDepth = funcSymbol is not null && (funcSymbol.SafetyTier == SafetyTier.Unsafe || funcSymbol.IsUnsafeBody) ? 1 : 0;

		// Seed data-segment globals into the local symbol table: a GlobalVariable IS a pointer,
		// so loads/stores/field GEPs work through the ordinary machinery (locals shadow on redeclare).
		foreach (var (globalName, globalRef) in _globalVariables)
		{
			if (!_locals.ContainsKey(globalName))
				_locals[globalName] = globalRef;
		}

		foreach (var (globalName, globalType) in _globalVariableTypes)
		{
			if (!_variableTypes.ContainsKey(globalName))
				_variableTypes[globalName] = globalType;
		}

		if (_bindingContext!.Globals.Lookup(mangledName) is FunctionSymbol sym)
		{
			for (var i = 0; i < sym.Parameters.Count; i++)
			{
				var param = llvmFunc.GetParam((uint)i);
				var paramName = sym.Parameters[i].Name;
				param.Name = paramName;

				var typeSymbol = sym.Parameters[i].Type;
				var llvmType = GetLLVMType(typeSymbol);

				var alloca = _builder.BuildAlloca(llvmType, paramName);
				_builder.BuildStore(param, alloca);

				_locals[paramName] = alloca;
				_variableTypes[paramName] = typeSymbol;
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

				_locals[paramName] = alloca;
				_variableTypes[paramName] = typeSymbol;
			}
		}

		EmitBlock(func.Body);

		if (func.ReturnType == "void" && !EndsWithReturn(func.Body))
		{
			EmitCleanup([.. _locals.Keys]);
			_builder.BuildRetVoid();
		}

		_unsafeDepth = 0;
	}

	private void EmitBlock(BlockStatementSyntax block)
	{
		var blockVars = new List<string>();

		foreach (var stmt in block.Statements)
		{
			if (stmt is VariableDeclarationSyntax v)
			{
				blockVars.Add(v.Name);
			}

			EmitStatement(stmt);
		}

		if (!EndsWithReturn(block))
		{
			EmitCleanup(blockVars);
		}
	}

	private void EmitStatement(SyntaxNode stmt)
	{
		switch (stmt)
		{
			case ReturnStatementSyntax ret:
				EmitReturnStatement(ret);
				break;
			case ExpressionStatementSyntax exprStmt:
				EmitExpression(exprStmt.Expression);
				break;
			case VariableDeclarationSyntax varDecl:
				EmitVariableDeclaration(varDecl);
				break;
			case BlockStatementSyntax block:
				EmitBlock(block);
				break;
			case IfStatementSyntax ifStmt:
				EmitIfStatement(ifStmt);
				break;
			case SwitchStatementSyntax sw:
				EmitSwitchStatement(sw);
				break;
			case WhileStatementSyntax whileStmt:
				EmitWhileStatement(whileStmt);
				break;
			case ForStatementSyntax forStmt:
				EmitForStatement(forStmt);
				break;
			case UnsafeBlockStatementSyntax unsafeBlock:
				_unsafeDepth++;
				EmitBlock(unsafeBlock.Body);
				_unsafeDepth--;
				break;
		}
	}

	private void EmitReturnStatement(ReturnStatementSyntax ret)
	{
		if (ret.Expression is not null)
		{
			// 1. Handle Null Returns on Options (Lowers null to Option.None)
			var expectedType = _functionReturnTypes.TryGetValue(_builder.InsertBlock.Parent.Name, out var et) ? et : TypeSymbol.Int;

			if (ret.Expression is NullLiteralExpressionSyntax && expectedType is UnionTypeSymbol optionUnion && optionUnion.IsOption)
			{
				var unionLayout = GetLLVMType(optionUnion);
				var tempAlloc = _builder.BuildAlloca(unionLayout, "ret_null_tmp");

				// Null-Pointer Optimization: store flat nullptr (None == zero) instead of a tag.
				if (optionUnion.IsNpoEligible)
				{
					_builder.BuildStore(LLVMValueRef.CreateConstPointerNull(unionLayout), tempAlloc);
				}
				else
				{
					var fieldIndex = GetFieldIndex(optionUnion, "None");

					var tagPtr = _builder.BuildGEP2(unionLayout, tempAlloc, new LLVMValueRef[] {
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "union_tag_ptr");
					_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);
				}

				var loadedNone = _builder.BuildLoad2(unionLayout, tempAlloc, "loaded_none");

				EmitCleanup([.. _locals.Keys]);
				_builder.BuildRet(loadedNone);
				return;
			}

			// 2. Handle Standard Returns
			var value = EmitExpression(ret.Expression);
			var type = GetExprType(ret.Expression);

			// Materialize memory-resident return values (structs/unions living in allocas/heap
			// slots) BEFORE scope cleanup frees them - the loaded register is what survives.
			LLVMValueRef? materialized = null;
			if ((type is StructTypeSymbol || type is UnionTypeSymbol) && value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			{
				var layout = GetLLVMType(type);
				materialized = _builder.BuildLoad2(layout, value, "struct_ret_val");
			}

			EmitCleanup([.. _locals.Keys]);

			if (materialized is not null)
			{
				_builder.BuildRet(materialized.Value);
			}
			else
			{
				_builder.BuildRet(value);
			}
		}
		else
		{
			EmitCleanup([.. _locals.Keys]);
			_builder.BuildRetVoid();
		}
	}

	private LLVMValueRef EmitExpression(ExpressionSyntax expr)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax intLit:
			{
				// Out-of-int-range literals are 64-bit; in-range literals stay int.
				var litTy = intLit.Value is > int.MaxValue or < int.MinValue ? TypeSymbol.Long : TypeSymbol.Int;
				return LLVMValueRef.CreateConstInt(GetLLVMType(litTy), unchecked((ulong)intLit.Value));
			}
			case DoubleLiteralExpressionSyntax dblLit:
				return LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, dblLit.Value);
			case BooleanLiteralExpressionSyntax boolLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolLit.Value ? 1UL : 0UL);
			case StringLiteralExpressionSyntax strLit:
				return EmitStringLiteral(strLit.Value);
			case CharacterLiteralExpressionSyntax charLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, charLit.Value);
			case NullLiteralExpressionSyntax:
				// Defensive: the binder rejects 'null' in safe code before emission.
				return LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
			case IdentifierExpressionSyntax id:
				return Load(id.Name);
case MemberAccessExpressionSyntax m:
			{
				// Enum scoped-variant access: EnumName.Variant is a compile-time constant.
				if (TryResolveEnumVariantReceiver(m) is { } enumConstType
					&& enumConstType.FindVariant(m.MemberName) is { } enumConstVariant)
				{
					return LLVMValueRef.CreateConstInt(GetLLVMType(enumConstType), unchecked((ulong)enumConstVariant.Value));
				}

				var (ptr, type) = GetFieldPointer(m);
				return _builder.BuildLoad2(GetLLVMType(type), ptr, "member_val");
			}
			case IndexExpressionSyntax idx:
				{
					var (ptr, type) = GetFieldPointer(idx);
					return _builder.BuildLoad2(GetLLVMType(type), ptr, "index_val");
				}
			case BorrowExpressionSyntax b:
				return EmitBorrowExpression(b);
			case HeapAllocationExpressionSyntax h:
				return EmitHeapAllocation(h);
			case HeapArrayAllocationExpressionSyntax hArr:
				return EmitHeapArrayAllocation(hArr);
			case StructInitializationExpressionSyntax s:
				return EmitStructInitialization(s);
			case ArrayInitializationExpressionSyntax a:
				return EmitArrayInitialization(a);
			case ArrayReplicationExpressionSyntax arrRepl:
				return EmitArrayReplication(arrRepl);
			case ParenthesizedStructInitializerExpressionSyntax parenStruct:
				return EmitParenthesizedStructInitialization(parenStruct);
			case CallExpressionSyntax call:
				return EmitCallExpression(call);
			case BinaryExpressionSyntax bin:
				return EmitBinaryExpression(bin);
			case UnaryExpressionSyntax unary:
				return EmitUnaryExpression(unary);
			case TernaryExpressionSyntax t:
				return EmitTernaryExpression(t);
			default:
				throw new InvalidOperationException($"Unknown expression type: {expr.GetType()}");
		}
	}

	private LLVMValueRef EmitStringLiteral(string value)
	{
		// Safely wraps string allocation natively via robust built-in BuildGlobalStringPtr API
		return _builder.BuildGlobalStringPtr(value, "str");
	}

    private bool IsConstructorCall(CallExpressionSyntax call, TypeSymbol targetType)
    {
        // Strip generics from the target type name to match the call's function name (e.g. Point<int> -> Point)
        var targetTypeName = targetType.Name;
        if (targetTypeName.Contains('<'))
        {
            targetTypeName = targetTypeName.Substring(0, targetTypeName.IndexOf('<'));
        }

        // Get short names to ignore namespace differences
        var shortTargetTypeName = targetTypeName.Contains('.')
            ? targetTypeName.Substring(targetTypeName.LastIndexOf('.') + 1)
            : targetTypeName;

        var shortCallName = call.FunctionName.Contains('.')
            ? call.FunctionName.Substring(call.FunctionName.LastIndexOf('.') + 1)
            : call.FunctionName;

        if (!string.Equals(shortCallName, shortTargetTypeName, StringComparison.Ordinal))
            return false;

        // Look up either the concrete instantiated constructor or the base template constructor
        if (!_bindingContext!.Constructors.TryGetValue(targetType.Name, out var ctors) &&
            !_bindingContext.Constructors.TryGetValue(targetTypeName, out ctors))
        {
            return false;
        }

        return _bindingContext.ResolvedCalls.TryGetValue(call, out var resolved) &&
               resolved.Parameters.Count > 0 &&
               resolved.Parameters[0].Name == "this";
    }

    private LLVMValueRef EmitCallExpression(CallExpressionSyntax call, LLVMValueRef? implicitThisPtr = null)
	{
		var paramOffset = implicitThisPtr is not null ? 1 : 0;
		return EmitCallExpressionCore(call, implicitThisPtr, paramOffset);
	}

	private LLVMValueRef EmitCallExpression(CallExpressionSyntax call)
	{
		return EmitCallExpressionCore(call, null, 0);
	}

	private LLVMValueRef EmitCallExpressionCore(CallExpressionSyntax call, LLVMValueRef? implicitThisPtr, int paramOffset)
	{
		if (call.FunctionName == "sizeof")
		{
			var targetTypeName = call.TypeArguments[0];
			var targetType = _bindingContext!.ResolveType(targetTypeName)!;
			var size = GetByteSize(targetType);
			return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)size);
		}

		// (§3.C) HasFlag is synthesized on [Flags] enums and lowered inline to (p & f) == f.
		if (_bindingContext!.ResolvedCalls.TryGetValue(call, out var hasFlagFunc)
			&& hasFlagFunc.Name.StartsWith("$HasFlag$", StringComparison.Ordinal))
		{
			var receiverName = call.FunctionName[..call.FunctionName.IndexOf('.')];
			LLVMValueRef receiverValue;
			if (_locals.TryGetValue(receiverName, out var hasFlagReceiverPtr))
			{
				receiverValue = Load(receiverName);
			}
			else
			{
				receiverValue = EmitExpression(call.Arguments[0]);
			}

			var flagValue = EmitExpression(call.Arguments[0]);
			var andVal = _builder.BuildAnd(receiverValue, flagValue, "hasflag_and");
			return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, andVal, flagValue, "hasflag_result");
		}

		string emitName;

		// Retrieve the pre-resolved overload from the binder context
		if (_bindingContext!.ResolvedCalls.TryGetValue(call, out var resolvedFunc))
		{
			emitName = resolvedFunc.Name;
		}
		else
		{
			// Fallback for generic configurations and structural fallbacks
			emitName = ResolveFunctionName(call.FunctionName, _currentUnit!);
			if (call.TypeArguments.Count > 0)
			{
				emitName = $"{emitName}<{string.Join(", ", call.TypeArguments)}>";
			}
		}

		var callee = _globals[emitName];
		var funcType = _functionTypes[emitName];

		var args = new List<LLVMValueRef>();		if (call.FunctionName.Contains('.') && _bindingContext.ResolvedCalls.TryGetValue(call, out var extFunc))
		{
			var receiverName = call.FunctionName.Split('.')[0];
			if (_locals.TryGetValue(receiverName, out var receiverPtr))
			{
				// If the receiver is a ref/refvar (pointer) variable, the extension's 'this'
				// parameter expects the referred-to struct address, so load the stored pointer.
				if (_variableTypes.TryGetValue(receiverName, out var recvType) && recvType is PointerTypeSymbol)
				{
					args.Add(_builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), receiverPtr, "receiver_loaded_ptr"));
				}
				else
				{
					args.Add(receiverPtr);
				}
			}
		}
		else if (implicitThisPtr is not null)
		{
			// Constructor call: first parameter is the destination storage
			args.Add(implicitThisPtr.Value);
		}

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var argExpr = call.Arguments[i];
			LLVMValueRef val;
			var valTy = GetExprType(argExpr);
			var paramTy = GetParamType(emitName, i + paramOffset);

			var targetSlice = paramTy is SliceTypeSymbol sl
							? sl
							: (paramTy is PointerTypeSymbol pPtr && pPtr.ReferencedType is SliceTypeSymbol sRef ? sRef : null);

			var isArgArray = valTy is ArrayTypeSymbol || (valTy is PointerTypeSymbol aPtr && aPtr.ReferencedType is ArrayTypeSymbol);

			if (targetSlice is not null && isArgArray)
			{
				LLVMValueRef arrayPtr;
				if (argExpr is IdentifierExpressionSyntax id)
				{
					arrayPtr = _locals[id.Name];
				}
				else
				{
					var (ptr, _) = GetFieldPointer(argExpr);
					arrayPtr = ptr;
				}

				val = CoerceArrayToSlice(arrayPtr, valTy, targetSlice);
			}
			else
			{
				val = EmitExpression(argExpr);
			}

			// Ownership transfer: passing a ResourceMove-style union by value transfers the
			// resource to the callee. Exclude the source from caller cleanup so the callee's
			// tag-checked destructor (and not a second drop here) releases it — no double free.
			if (argExpr is IdentifierExpressionSyntax moveArg
				&& !_disposedVars.Contains(moveArg.Name)
				&& _variableTypes.TryGetValue(moveArg.Name, out var srcTy)
				&& srcTy is UnionTypeSymbol srcUnion
				&& UnionNeedsTagCheckedCleanup(srcUnion)
				&& paramTy is UnionTypeSymbol paramUnion && paramUnion.Name == srcUnion.Name)
			{
				_movedVars.Add(moveArg.Name);
			}

			// Variadic promotion rules (promote Boolean and Char to i32)
			var isVariadic = _astExterns.TryGetValue(emitName, out var ext) && ext.IsVariadic;
			if (isVariadic && i + paramOffset >= _functionParameterTypes[emitName].Count)
			{
				if (valTy.Equals(TypeSymbol.Bool))
				{
					val = _builder.BuildZExt(val, LLVMTypeRef.Int32, "prom_bool");
				}
				else if (valTy.Equals(TypeSymbol.Char))
				{
					val = _builder.BuildZExt(val, LLVMTypeRef.Int32, "prom_char");
				}
			}

			if (paramTy.Equals(TypeSymbol.String) && valTy is ArrayTypeSymbol)
			{
				LLVMValueRef arrayPtr;
				if (argExpr is IdentifierExpressionSyntax id)
				{
					arrayPtr = _locals[id.Name];
				}
				else
				{
					var (ptr, _) = GetFieldPointer(argExpr);
					arrayPtr = ptr;
				}

				val = _builder.BuildBitCast(arrayPtr, GetLLVMType(TypeSymbol.String), "array_to_string_cast");
			}
			else if (paramTy.Equals(TypeSymbol.String) && valTy is SliceTypeSymbol)
			{
				LLVMValueRef slicePtr;
				if (argExpr is IdentifierExpressionSyntax id)
				{
					slicePtr = _locals[id.Name];
				}
				else
				{
					var (ptr, _) = GetFieldPointer(argExpr);
					slicePtr = ptr;
				}

				var sliceLayout = GetLLVMType(valTy);
				var ptrField = _builder.BuildGEP2(sliceLayout, slicePtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "slice_ptr_field");
				val = _builder.BuildLoad2(GetLLVMType(TypeSymbol.String), ptrField, "slice_to_string_cast");
			}

			args.Add(val);
		}

		var retTypeSymbol = _functionReturnTypes.TryGetValue(emitName, out var ret) ? ret : TypeSymbol.Int;
		var instName = retTypeSymbol.Equals(TypeSymbol.Void) ? "" : "call_val";

		return _builder.BuildCall2(funcType, callee, args.ToArray(), instName);
	}

	private LLVMValueRef EmitBinaryExpression(BinaryExpressionSyntax bin)
	{
		// Intercept assignment operators first to prevent mathematical promotion conflicts
		if (bin.Operator == "=")
		{
			return EmitAssignStore(bin);
		}

		var left = EmitExpression(bin.Left);
		var right = EmitExpression(bin.Right);
		var lTy = GetExprType(bin.Left);
		var rTy = GetExprType(bin.Right);

		var isDouble = lTy.Equals(TypeSymbol.Double) || rTy.Equals(TypeSymbol.Double);

		if (isDouble)
		{
			if (lTy.Equals(TypeSymbol.Int))
			{
				left = _builder.BuildSIToFP(left, LLVMTypeRef.Double, "sitofp_left");
			}

			if (rTy.Equals(TypeSymbol.Int))
			{
				right = _builder.BuildSIToFP(right, LLVMTypeRef.Double, "sitofp_right");
			}

			if (TypeSymbol.IsIntegerType(lTy) && !lTy.Equals(TypeSymbol.Int))
			{
				left = _builder.BuildSIToFP(left, LLVMTypeRef.Double, "sitofp_left");
			}

			if (TypeSymbol.IsIntegerType(rTy) && !rTy.Equals(TypeSymbol.Int))
			{
				right = _builder.BuildSIToFP(right, LLVMTypeRef.Double, "sitofp_right");
			}

			return bin.Operator switch
			{
				"+" => _builder.BuildFAdd(left, right),
				"-" => _builder.BuildFSub(left, right),
				"*" => _builder.BuildFMul(left, right),
				"/" => _builder.BuildFDiv(left, right),
				"%" => _builder.BuildFRem(left, right),
				"==" => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
				"!=" => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, left, right),
				"<" => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
				">" => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
				"<=" => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
				">=" => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
				_ => throw new InvalidOperationException($"Unknown double operator '{bin.Operator}'"),
			};
		}

		// Integer width promotion: when mixing integer widths, zext/sext the narrower
		// operand up to the wider operand's width before the operation.
		if (TypeSymbol.IsIntegerType(lTy) && TypeSymbol.IsIntegerType(rTy))
		{
			var lWidth = TypeSymbol.IntegerBitWidth(lTy);
			var rWidth = TypeSymbol.IntegerBitWidth(rTy);

			if (lWidth < rWidth)
			{
				left = TypeSymbol.IsSignedIntegerType(lTy)
					? _builder.BuildSExt(left, GetLLVMType(rTy), "promote_left_sext")
					: _builder.BuildZExt(left, GetLLVMType(rTy), "promote_left_zext");
			}
			else if (rWidth < lWidth)
			{
				right = TypeSymbol.IsSignedIntegerType(rTy)
					? _builder.BuildSExt(right, GetLLVMType(lTy), "promote_right_sext")
					: _builder.BuildZExt(right, GetLLVMType(lTy), "promote_right_zext");
			}

			// Signed division/modulo must be relaxed for unsigned operands
			if (bin.Operator is "/" or "%" && (!TypeSymbol.IsSignedIntegerType(lTy) || !TypeSymbol.IsSignedIntegerType(rTy)))
			{
				return bin.Operator switch
				{
					"/" => _builder.BuildUDiv(left, right),
					_ => _builder.BuildURem(left, right),
				};
			}
		}

		return bin.Operator switch
		{
			"+" => _builder.BuildAdd(left, right),
			"-" => _builder.BuildSub(left, right),
			"*" => _builder.BuildMul(left, right),
			"/" => _builder.BuildSDiv(left, right),
			"%" => _builder.BuildSRem(left, right),
			"==" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right),
			"!=" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right),
			"<" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right),
			">" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right),
			"<=" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right),
			">=" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right),
			"&" => _builder.BuildAnd(left, right),
			"|" => _builder.BuildOr(left, right),
			"^" => _builder.BuildXor(left, right),
			"&&" => _builder.BuildAnd(left, right),
			"||" => _builder.BuildOr(left, right),
			"<<" => _builder.BuildShl(left, right),
			">>" => _builder.BuildAShr(left, right),
			">>>" => _builder.BuildLShr(left, right),
			_ => throw new InvalidOperationException($"Unknown binary operator '{bin.Operator}'"),
		};
	}

	private LLVMValueRef CoerceIntegerWidth(LLVMValueRef value, TypeSymbol fromType, TypeSymbol toType)
	{
		if (!TypeSymbol.IsIntegerType(fromType) || !TypeSymbol.IsIntegerType(toType))
			return value;

		var fromWidth = TypeSymbol.IntegerBitWidth(fromType) is var fw ? fw : 0;
		var toWidth = TypeSymbol.IntegerBitWidth(toType) is var tw ? tw : 0;

		if (fromWidth == toWidth)
			return value;

		var llvmTarget = GetLLVMType(toType);

		if (fromWidth > toWidth)
			return _builder.BuildTrunc(value, llvmTarget, "store_trunc");

		return TypeSymbol.IsSignedIntegerType(fromType)
			? _builder.BuildSExt(value, llvmTarget, "store_sext")
			: _builder.BuildZExt(value, llvmTarget, "store_zext");
	}

	private LLVMValueRef EmitAssignStore(BinaryExpressionSyntax bin)
	{
		var right = EmitExpression(bin.Right);
		var rTy = GetExprType(bin.Right);
		var llvmTy = GetLLVMType(rTy);

		// Dereference aggregate pointers before storing them into value targets
		if (right.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && (rTy is StructTypeSymbol || rTy is ArrayTypeSymbol))
		{
			right = _builder.BuildLoad2(llvmTy, right, "loaded_assign_struct");
		}

		if (bin.Left is IdentifierExpressionSyntax id)
		{
			if (_locals.TryGetValue(id.Name, out var ptr))
			{
				var type = _variableTypes[id.Name];

				if (bin.Right is NullLiteralExpressionSyntax && type is UnionTypeSymbol optionUnion && optionUnion.IsOption)
				{
					var unionLayout = GetLLVMType(optionUnion);

					// Null-Pointer Optimization: store flat nullptr (None == zero) instead of a tag.
					if (optionUnion.IsNpoEligible)
					{
						_builder.BuildStore(LLVMValueRef.CreateConstPointerNull(unionLayout), ptr);
					}
					else
					{
						var fieldIndex = GetFieldIndex(optionUnion, "None");

						var tagPtr = _builder.BuildGEP2(unionLayout, ptr, new LLVMValueRef[] {
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
						}, "union_tag_ptr");
						_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);
					}
					return right;
				}
				else if (type is PointerTypeSymbol)
				{
					if (bin.Right is BorrowExpressionSyntax)
					{
						// §3G: refvar reassignment (`a = ref expr`) stores the new pointer into the alloca
						_builder.BuildStore(right, ptr);
					}
					else
					{
						// Write-through (`a = b` or `a = value`): load current pointer, store value through it
						var actualPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptr, "target_ptr");
						_builder.BuildStore(right, actualPtr);
					}
				}
				else
				{
					// Drop-on-reassignment: overwriting a ResourceMove-style union local with a
					// *fresh* value (struct initializer) must release the previously-active resource
					// first. Guarded to the fresh-construction case so copy-assignment between two
					// live option variables cannot free a resource the source still owns.
					if (type is UnionTypeSymbol reassignedUnion
						&& UnionNeedsTagCheckedCleanup(reassignedUnion)
						&& bin.Right is StructInitializationExpressionSyntax
						&& !_movedVars.Contains(id.Name)
						&& !_disposedVars.Contains(id.Name))
					{
						EmitUnionTagCheckedCleanup(id.Name, ptr, reassignedUnion);
					}

					// A fresh union construction (struct initializer) must be materialized in place:
					// EmitStructInitialization returns an alloca pointer, not an aggregate value, so
					// a plain BuildStore would store a pointer into the slot and corrupt it.
					if (type is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax reinit)
					{
						EmitStructInitializationInPlace(reinit, ptr);
					}
					else
					{
						var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
							? EmitRefToValue(right, GetExprType(bin.Right))
							: right;
						_builder.BuildStore(coerced, ptr);
					}
				}

				return right;
			}
			else if (_locals.TryGetValue("this", out var thisPtr))
			{
				var thisType = _variableTypes["this"] as PointerTypeSymbol;
				var refType = thisType!.ReferencedType;

				if (refType is StructTypeSymbol structType)
				{
					var field = structType.FindField(id.Name);
					if (field is not null)
					{
						var (fieldPtr, _) = GetFieldPointer(id);
						_builder.BuildStore(right, fieldPtr);
						return right;
					}
				}
				else if (refType is UnionTypeSymbol unionType)
				{
					var field = unionType.FindField(id.Name);
					if (field is not null)
					{
						var (fieldPtr, _) = GetFieldPointer(id);
						if (bin.Right is StructInitializationExpressionSyntax thisFieldReinit)
							EmitStructInitializationInPlace(thisFieldReinit, fieldPtr);
						else
							_builder.BuildStore(right, fieldPtr);
						return right;
					}
				}
			}

			return right;
		}
		else if (bin.Left is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, fieldType) = GetFieldPointer(m);
			if (fieldType is UnionTypeSymbol fieldUnion
				&& UnionNeedsTagCheckedCleanup(fieldUnion)
				&& bin.Right is StructInitializationExpressionSyntax)
			{
				EmitUnionTagCheckedCleanup(m.MemberName, fieldPtr, fieldUnion);
			}
			if (fieldType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax fieldReinit)
				EmitStructInitializationInPlace(fieldReinit, fieldPtr);
			else
			{
				var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
					? EmitRefToValue(right, GetExprType(bin.Right))
					: right;
				_builder.BuildStore(coerced, fieldPtr);
			}
			return right;
		}
		else if (bin.Left is IndexExpressionSyntax idx)
		{
			var (elementPtr, elementType) = GetFieldPointer(idx);
			if (elementType is UnionTypeSymbol elemUnion
				&& UnionNeedsTagCheckedCleanup(elemUnion)
				&& bin.Right is StructInitializationExpressionSyntax)
			{
				EmitUnionTagCheckedCleanup("elem", elementPtr, elemUnion);
			}
			if (elementType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax elemReinit)
				EmitStructInitializationInPlace(elemReinit, elementPtr);
			else
			{
				var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
					? EmitRefToValue(right, GetExprType(bin.Right))
					: right;
				_builder.BuildStore(coerced, elementPtr);
			}
			return right;
		}

		throw new InvalidOperationException("Invalid target assignment");
	}

	private LLVMValueRef EmitUnaryExpression(UnaryExpressionSyntax unary)
	{
		if (unary.Operator.EndsWith("_postfix") || unary.Operator.EndsWith("_prefix"))
		{
			return EmitIncrementDecrement(unary, unary.Operator.EndsWith("_prefix"), unary.Operator.StartsWith("++"));
		}

		var operand = EmitExpression(unary.Operand);

		if (unary.Operator.StartsWith("(") && unary.Operator.EndsWith(")"))
		{
			var targetTypeName = unary.Operator.Substring(1, unary.Operator.Length - 2);
			var targetTypeSymbol = _bindingContext!.ResolveType(targetTypeName)!;
			var targetType = GetLLVMType(targetTypeSymbol);

			var operandType = GetExprType(unary.Operand);
			var operandLlvmType = GetLLVMType(operandType);

			// Safe/unbound zone: (Enum)integer yields Option<Enum> — a checked conversion
			// comparing against every declared variant value (None when no match). The raw
			// enum scalar is only produced by this cast inside unsafe code.
			if (targetTypeSymbol is EnumTypeSymbol safeCastEnum && _unsafeDepth == 0 &&
				operandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(operandType))
			{
				if (_bindingContext.ResolveType($"Option<{safeCastEnum.Name}>") is UnionTypeSymbol optionUnion)
				{
					var (optionPtr, optionTy) = MaterializeEnumCastOption(safeCastEnum, operand, operandType, optionUnion);
					return _builder.BuildLoad2(GetLLVMType(optionTy), optionPtr, "enum_cast_option");
				}
			}

			// If casting between the identical LLVM type, return early
			if (targetType.Handle == operandLlvmType.Handle)
				return operand;

			// Enums are flat scalar integers (§1): reduce to their underlying storage
			// type so the width-adjustment branches below can operate on them directly.
			var effectiveTarget = targetTypeSymbol is EnumTypeSymbol targetEnum ? targetEnum.StorageType : targetTypeSymbol;
			var effectiveOperand = operandType is EnumTypeSymbol operandEnum ? operandEnum.StorageType : operandType;

			var targetIsInt = TypeSymbol.IsIntegerType(effectiveTarget);
			var operandIsInt = TypeSymbol.IsIntegerType(effectiveOperand);

			// Integer to Float (any signed int -> double)
			if (effectiveTarget.Equals(TypeSymbol.Double) && operandIsInt)
			{
				return _builder.BuildSIToFP(operand, targetType, "cast_sitofp");
			}

			// Float to Integer (double -> any int)
			if (effectiveOperand.Equals(TypeSymbol.Double) && targetIsInt)
			{
				return _builder.BuildFPToSI(operand, targetType, "cast_fptosi");
			}

			// Integer <-> Integer width conversion (byte/char/short/int/long and unsigned variants)
			if (operandIsInt && targetIsInt)
			{
				var operandWidth = TypeSymbol.IntegerBitWidth(effectiveOperand);
				var targetWidth = TypeSymbol.IntegerBitWidth(effectiveTarget);

				if (operandWidth > targetWidth)
				{
					// Narrowing: truncate the source to the target width
					return _builder.BuildTrunc(operand, targetType, "cast_trunc");
				}

				if (operandWidth < targetWidth)
				{
					// Widening: sign-extend signed sources, zero-extend unsigned sources
					return TypeSymbol.IsSignedIntegerType(effectiveOperand)
						? _builder.BuildSExt(operand, targetType, "cast_sext")
						: _builder.BuildZExt(operand, targetType, "cast_zext");
				}
			}

			return _builder.BuildBitCast(operand, targetType, "cast_bitcast");
		}

		switch (unary.Operator)
		{
			case "-":
				return _builder.BuildNeg(operand);
			case "!":
				return _builder.BuildNot(operand);
			case "~":
			{
				// (§3.B) On a [Flags] enum, '~' is the masked bitwise complement:
				// (~value) & CombinedAtomicMask, truncated/width-locked to storage width.
				if (GetExprType(unary.Operand) is EnumTypeSymbol { IsFlags: true } flagsEnum)
				{
					var storeTy = GetLLVMType(flagsEnum);
					var notVal = _builder.BuildNot(operand, "flags_not");
					var combinedMask = 0L;
					foreach (var flagVar in flagsEnum.Variants)
					{
						combinedMask |= flagVar.Value;
					}
					return _builder.BuildAnd(notVal,
						LLVMValueRef.CreateConstInt(storeTy, unchecked((ulong)combinedMask)), "flags_masked");
				}
				return _builder.BuildXor(operand, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, unchecked((ulong)-1)));
			}
			case "*":
			{
				var operandType = GetExprType(unary.Operand);
				if (operandType is RawPointerTypeSymbol rawPtr)
				{
					var elemLlvmType = GetLLVMType(rawPtr.ElementType);
					return _builder.BuildLoad2(elemLlvmType, operand, "deref_val");
				}
				return _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), operand, "deref_val");
			}
			case "&":
			{
				if (unary.Operand is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
					return ptr;
				if (unary.Operand is MemberAccessExpressionSyntax memberAccess)
				{
					var (fieldPtr, _) = GetFieldPointer(memberAccess);
					return fieldPtr;
				}
				if (unary.Operand is IndexExpressionSyntax indexExpr)
				{
					var (elementPtr, _) = GetFieldPointer(indexExpr);
					return elementPtr;
				}
				return operand;
			}
			default:
				throw new InvalidOperationException($"Unknown unary operator '{unary.Operator}'");
		}
	}

	private void EmitVariableDeclaration(VariableDeclarationSyntax varDecl)
	{
		TypeSymbol? typeSymbol = null;
		if (varDecl.Type is not null)
		{
			typeSymbol = _bindingContext!.ResolveType(varDecl.Type);
		}

		// Handle References / Borrows
		if (varDecl.Type == "refvar" || varDecl.Type == "ref")
		{
			var val = EmitExpression(varDecl.Initializer!);
			var valTy = GetExprType(varDecl.Initializer!);

			var innerType = valTy is PointerTypeSymbol ptrType ? ptrType.ReferencedType : valTy;
			var isMutable = varDecl.Type == "refvar";
			var pointerType = new PointerTypeSymbol(innerType, isMutable);

			var alloca = _builder.BuildAlloca(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), varDecl.Name);
			_locals[varDecl.Name] = alloca;
			_variableTypes[varDecl.Name] = pointerType;

			_builder.BuildStore(val, alloca);
			return;
		}

		// Handle Heap Allocations
		if (varDecl.Initializer is HeapAllocationExpressionSyntax heapInit)
		{
			var val = EmitExpression(heapInit);
			var valTy = GetExprType(heapInit);

			var alloca = _builder.BuildAlloca(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), varDecl.Name);
			_locals[varDecl.Name] = alloca;
			_variableTypes[varDecl.Name] = valTy;
			_heapAllocatedVars.Add(varDecl.Name);

			_builder.BuildStore(val, alloca);
			return;
		}
		else if (varDecl.Initializer is HeapArrayAllocationExpressionSyntax heapArrInit)
		{
			var val = EmitExpression(heapArrInit);
			var valTy = GetExprType(heapArrInit);

			var alloca = _builder.BuildAlloca(GetLLVMType(valTy), varDecl.Name);
			_locals[varDecl.Name] = alloca;
			_variableTypes[varDecl.Name] = valTy;
			_heapAllocatedVars.Add(varDecl.Name); // Register for RAII cleanup!

			_builder.BuildStore(val, alloca);
			return;
		}

		if (typeSymbol is not null)
		{
			var llvmType = GetLLVMType(typeSymbol);
			var alloca = _builder.BuildAlloca(llvmType, varDecl.Name);
			_locals[varDecl.Name] = alloca;
			_variableTypes[varDecl.Name] = typeSymbol;

			// Panic-safe zero-ing: a ResourceMove-style union local is pre-set to None so that if
			// a panic occurs while its constructor/initializer is still running, the unwinder (and
			// any tag-checked destructor) observes a None slot instead of uninitialized garbage.
			if (typeSymbol is UnionTypeSymbol zeroUnion && UnionNeedsTagCheckedCleanup(zeroUnion))
			{
				var zeroNone = zeroUnion.NoneVariant is not null ? GetFieldIndex(zeroUnion, zeroUnion.NoneVariant.Name) : 0;
				var zeroTagPtr = _builder.BuildGEP2(llvmType, alloca, new LLVMValueRef[] {
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "union_tag_ptr");
				_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)zeroNone), zeroTagPtr);
			}


			if (varDecl.Initializer is not null)
			{
				// Handle Null Initializers on Option Types (Lowers null to Option.None)
				if (varDecl.Initializer is NullLiteralExpressionSyntax && typeSymbol is UnionTypeSymbol optionUnion && optionUnion.IsOption)
				{
					// Null-Pointer Optimization: store flat nullptr (None == zero) instead of a tag.
					if (optionUnion.IsNpoEligible)
					{
						_builder.BuildStore(LLVMValueRef.CreateConstPointerNull(llvmType), alloca);
						return;
					}

					var fieldIndex = GetFieldIndex(optionUnion, "None");

					var tagPtr = _builder.BuildGEP2(llvmType, alloca, new LLVMValueRef[] {
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "union_tag_ptr");
					_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);
					return;
				}

				var valTy = GetExprType(varDecl.Initializer);

				if (typeSymbol is SliceTypeSymbol && valTy is ArrayTypeSymbol)
				{
					var arrayPtr = EmitExpression(varDecl.Initializer);
					var sliceVal = CoerceArrayToSlice(arrayPtr, valTy, (typeSymbol as SliceTypeSymbol)!);
					_builder.BuildStore(sliceVal, alloca);
					return;
				}

				if (varDecl.Initializer is StructInitializationExpressionSyntax structInit)
				{
					EmitStructInitializationInPlace(structInit, alloca);
				}
				else if (varDecl.Initializer is ArrayInitializationExpressionSyntax arrInit)
				{
					EmitArrayInitializationInPlace(arrInit, alloca, (typeSymbol as ArrayTypeSymbol)!);
				}
				else if (varDecl.Initializer is ArrayReplicationExpressionSyntax arrRepl)
				{
					EmitArrayReplicationInPlace(arrRepl, alloca, (typeSymbol as ArrayTypeSymbol)!);
				}
				else if (varDecl.Initializer is CallExpressionSyntax ctorCall && IsConstructorCall(ctorCall, typeSymbol))
				{
					// 'var T v = T(args)': the constructor populates the variable's storage
					// in place via its implicit 'this' parameter; no value store follows.
					EmitCallExpression(ctorCall, alloca);
				}
				else
				{
					var value = EmitExpression(varDecl.Initializer);
					var coerced = typeSymbol is not PointerTypeSymbol
						&& (varDecl.Initializer is MemberAccessExpressionSyntax or IndexExpressionSyntax)
						? EmitRefToValue(value, GetExprType(varDecl.Initializer!))
						: value;
					coerced = CoerceIntegerWidth(coerced, GetExprType(varDecl.Initializer!) ?? typeSymbol, typeSymbol);
					_builder.BuildStore(coerced, alloca);
				}
			}
		}
		else
		{
			// Type Inference
			var val = EmitExpression(varDecl.Initializer!);
			var valTy = GetExprType(varDecl.Initializer!);

			_variableTypes[varDecl.Name] = valTy;

			// Register Forwarding: If the aggregate is already allocated on the stack, forward its address
			if (valTy is StructTypeSymbol || valTy is ArrayTypeSymbol)
			{
				_locals[varDecl.Name] = val;
			}
			else
			{
				var llvmType = GetLLVMType(valTy);
				var alloca = _builder.BuildAlloca(llvmType, varDecl.Name);
				_locals[varDecl.Name] = alloca;
				_builder.BuildStore(val, alloca);
			}
		}
	}

	private void EmitIfStatement(IfStatementSyntax ifStmt)
	{
		var condition = EmitExpression(ifStmt.Condition);

		var currentFunc = _builder.InsertBlock.Parent;
		var thenBlock = currentFunc.AppendBasicBlock("then");
		var elseBlock = currentFunc.AppendBasicBlock("else");
		var mergeBlock = currentFunc.AppendBasicBlock("ifend");

		_builder.BuildCondBr(condition, thenBlock, elseBlock);

		_builder.PositionAtEnd(thenBlock);
		EmitStatement(ifStmt.ThenStatement);
		if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			_builder.BuildBr(mergeBlock);

		_builder.PositionAtEnd(elseBlock);
		if (ifStmt.ElseClause is not null)
			EmitStatement(ifStmt.ElseClause.Body);
		if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			_builder.BuildBr(mergeBlock);

		_builder.PositionAtEnd(mergeBlock);
	}

	private void EmitWhileStatement(WhileStatementSyntax whileStmt)
	{
		var currentFunc = _builder.InsertBlock.Parent;
		var condBlock = currentFunc.AppendBasicBlock("whilecond");
		var bodyBlock = currentFunc.AppendBasicBlock("whilebody");
		var endBlock = currentFunc.AppendBasicBlock("whileend");

		_builder.BuildBr(condBlock);

		_builder.PositionAtEnd(condBlock);
		var condition = EmitExpression(whileStmt.Condition);
		_builder.BuildCondBr(condition, bodyBlock, endBlock);

		_builder.PositionAtEnd(bodyBlock);
		EmitStatement(whileStmt.Body);
		_builder.BuildBr(condBlock);

		_builder.PositionAtEnd(endBlock);
	}

	private void EmitForStatement(ForStatementSyntax forStmt)
	{
		EmitVariableDeclaration(forStmt.Initializer);

		var currentFunc = _builder.InsertBlock.Parent;
		var condBlock = currentFunc.AppendBasicBlock("forcond");
		var bodyBlock = currentFunc.AppendBasicBlock("forbody");
		var incBlock = currentFunc.AppendBasicBlock("forinc");
		var endBlock = currentFunc.AppendBasicBlock("forend");

		_builder.BuildBr(condBlock);

		_builder.PositionAtEnd(condBlock);
		var condition = EmitExpression(forStmt.Condition);
		_builder.BuildCondBr(condition, bodyBlock, endBlock);

		_builder.PositionAtEnd(bodyBlock);
		EmitStatement(forStmt.Body);
		_builder.BuildBr(incBlock);

		_builder.PositionAtEnd(incBlock);
		EmitExpression(forStmt.Increment);
		_builder.BuildBr(condBlock);

		_builder.PositionAtEnd(endBlock);
	}

	// Coerces an expression value that is a `ref T` into the pointed-to value when it
	// is being consumed as a non-reference value (e.g. `var int y = opt.Some` or
	// `v = node.Next.Some`). `opt.Some` on a null-pointer-optimized option yields the
	// inner reference pointer; the value destination expects the pointed-to object, so
	// dereference once. Callers that want the raw reference never pass through this
	// (the refvar-declaration and switch-promotion paths consume the pointer directly).
	private LLVMValueRef EmitRefToValue(LLVMValueRef value, TypeSymbol exprType)
	{
		if (exprType is PointerTypeSymbol ptrType && ptrType.ReferencedType is not null)
		{
			return _builder.BuildLoad2(GetLLVMType(ptrType.ReferencedType), value, "ref_to_value");
		}

		return value;
	}

	private LLVMValueRef Load(string name)
	{
		if (!_locals.TryGetValue(name, out var ptr))
		{
			if (_locals.TryGetValue("this", out var thisPtr))
			{
				if (_variableTypes["this"] is PointerTypeSymbol thisPtrTy && thisPtrTy.ReferencedType is EnumTypeSymbol enumSelf)
				{
					// Unqualified enum variant access inside an extension body:
					// 'Active' lowers to the variant's compile-time constant.
					var variant = enumSelf.FindVariant(name);
					if (variant is not null)
						return LLVMValueRef.CreateConstInt(GetLLVMType(enumSelf), unchecked((ulong)variant.Value));
				}

				var thisType = _variableTypes["this"] as PointerTypeSymbol;
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
					return _builder.BuildLoad2(GetLLVMType(field.Type), fieldPtr, "this_field_val");
				}
			}

			throw new InvalidOperationException($"Undefined variable '{name}'");
		}

		var type = _variableTypes[name];
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

	private LLVMValueRef EmitBorrowExpression(BorrowExpressionSyntax expr)
	{
		if (expr.Expression is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var type = _variableTypes[id.Name];
			var isReference = type is PointerTypeSymbol;
			var isHeap = _heapAllocatedVars.Contains(id.Name) && type is not SliceTypeSymbol;

			if (isReference || isHeap)
			{
				return _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptr, "borrow_ref");
			}

			return ptr;
		}
		else if (expr.Expression is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, _) = GetFieldPointer(m);
			return fieldPtr;
		}
		else if (expr.Expression is IndexExpressionSyntax idx)
		{
			var (elementPtr, _) = GetFieldPointer(idx);
			return elementPtr;
		}

		throw new InvalidOperationException("Can only borrow variables or member fields");
	}

	private LLVMValueRef EmitIncrementDecrement(UnaryExpressionSyntax u, bool isPrefix, bool isIncrement)
	{
		var (ptr, type) = GetFieldPointer(u.Operand);
		var ty = GetLLVMType(type);

		var currentVal = _builder.BuildLoad2(ty, ptr, "incdec_current");
		var step = LLVMValueRef.CreateConstInt(ty, 1);

		var newVal = isIncrement
			? _builder.BuildAdd(currentVal, step, "incdec_new")
			: _builder.BuildSub(currentVal, step, "incdec_new");

		_builder.BuildStore(newVal, ptr);

		return isPrefix ? newVal : currentVal;
	}

	private LLVMValueRef EmitTernaryExpression(TernaryExpressionSyntax expr)
	{
		var condition = EmitExpression(expr.Condition);
		var thenVal = EmitExpression(expr.ThenExpression);
		var elseVal = EmitExpression(expr.ElseExpression);

		var type = GetExprType(expr.ThenExpression);
		var llvmTy = GetLLVMType(type);

		var currentFunc = _builder.InsertBlock.Parent;
		var thenBlock = currentFunc.AppendBasicBlock("ternary_then");
		var elseBlock = currentFunc.AppendBasicBlock("ternary_else");
		var mergeBlock = currentFunc.AppendBasicBlock("ternary_end");

		var resultAlloc = _builder.BuildAlloca(llvmTy, "ternary_result");
		_builder.BuildCondBr(condition, thenBlock, elseBlock);

		_builder.PositionAtEnd(thenBlock);
		_builder.BuildStore(thenVal, resultAlloc);
		_builder.BuildBr(mergeBlock);

		_builder.PositionAtEnd(elseBlock);
		_builder.BuildStore(elseVal, resultAlloc);
		_builder.BuildBr(mergeBlock);

		_builder.PositionAtEnd(mergeBlock);
		var loadedReg = _builder.BuildLoad2(llvmTy, resultAlloc, "ternary_val");

		return loadedReg;
	}

	private TypeSymbol GetExprType(ExpressionSyntax expr)
	{
		if (expr is BorrowExpressionSyntax bb)
		{
			var res = new PointerTypeSymbol(GetExprType(bb.Expression), bb.IsMutable);
			return res;
		}

		return expr switch
		{
			IntegerLiteralExpressionSyntax intLit => intLit.Value is > int.MaxValue or < int.MinValue ? TypeSymbol.Long : TypeSymbol.Int,
			DoubleLiteralExpressionSyntax => TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			IdentifierExpressionSyntax id => GetExprTypeIdentifier(id),
			MemberAccessExpressionSyntax m => GetMemberAccessType(m),
			IndexExpressionSyntax idx => GetIndexExpressionType(idx),
			BorrowExpressionSyntax b => new PointerTypeSymbol(GetExprType(b.Expression), false),
			StructInitializationExpressionSyntax s => _bindingContext!.ResolveType(s.StructTypeName)!,
			UnaryExpressionSyntax u => ResolveUnaryExprType(u),
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
		if (_variableTypes.TryGetValue(id.Name, out var type))
		{
			// Enum 'this' in an extension body reads as the scalar enum value,
			// not the injected receiver pointer.
			if (type is PointerTypeSymbol ptrId && ptrId.ReferencedType is EnumTypeSymbol enumId)
				return enumId;
			return type;
		}

		// Unqualified enum variant access inside an enum extension body.
		if (_variableTypes.TryGetValue("this", out var thisTy)
			&& thisTy is PointerTypeSymbol thisPtr
			&& thisPtr.ReferencedType is EnumTypeSymbol enumSelf
			&& enumSelf.FindVariant(id.Name) is not null)
		{
			return enumSelf;
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
			if (result is EnumTypeSymbol castEnum && _unsafeDepth == 0)
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
		var mangledName = ResolveFunctionName(call.FunctionName, _currentUnit!);
		if (call.TypeArguments.Count > 0)
		{
			mangledName = $"{mangledName}<{string.Join(", ", call.TypeArguments)}>";
		}

		return _functionReturnTypes.TryGetValue(mangledName, out var type) ? type : TypeSymbol.Int;
	}

	private TypeSymbol ResolveBinaryExpressionType(BinaryExpressionSyntax bin)
	{
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
		if (TryResolveEnumVariantReceiver(m) is { } enumType)
			return enumType;

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

	private TypeSymbol GetParamType(string mangledFuncName, int index)
	{
		if (_functionParameterTypes.TryGetValue(mangledFuncName, out var paramTypes) && index < paramTypes.Count)
		{
			return paramTypes[index];
		}

		return TypeSymbol.Int;
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

		var activeUsings = new List<string>(activeUnit.Usings.Select(u => u.NamespaceName));
		if (activeUnit.NamespaceDeclaration is not null)
			activeUsings.AddRange(activeUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

		foreach (var importNs in activeUsings)
		{
			var candidateMangled = $"{importNs}.{name}";
			if (_globals.ContainsKey(candidateMangled) || _bindingContext!.GenericFunctionTemplates.ContainsKey(candidateMangled))
				return candidateMangled;
		}

		return name;
	}

	private void EmitCleanup(IEnumerable<string> variableNames)
	{
		foreach (var name in variableNames)
		{
			if (_movedVars.Contains(name) || _disposedVars.Contains(name))
				continue;

			_disposedVars.Add(name);

			var ptrAlloc = _locals[name];
			var type = _variableTypes[name];

			// 1. Call the type's '~T()' destructor ONLY for owned StructTypeSymbol variables
			if (type is StructTypeSymbol structType)
			{
				var disposeBaseName = $"{structType.Name}.~{structType.Name}";

				if (_bindingContext!.OverloadedFunctions.TryGetValue(disposeBaseName, out var candidates) && candidates.Count > 0)
				{
					var disposeSymbol = candidates[0];
					var callee = _globals[disposeSymbol.Name];
					var funcType = _functionTypes[disposeSymbol.Name];

					var isHeap = _heapAllocatedVars.Contains(name);

					LLVMValueRef thisPtr;
					if (isHeap)
					{
						thisPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrAlloc, "this_ptr");
					}
					else
					{
						thisPtr = ptrAlloc;
					}

					_builder.BuildCall2(funcType, callee, new LLVMValueRef[] { thisPtr }, "");
				}
			}

			// 1b. Tag-checked destructor for ResourceMove unions (Option<T> wrapping a move type):
			//     run the inner ~T() only on the currently-active (Some) payload variant, with a
			//     reset-before-drop volatile None store so a panic during ~T() never double-frees.
			if (type is UnionTypeSymbol unionType)
			{
				EmitUnionTagCheckedCleanup(name, ptrAlloc, unionType);
			}

			// 2. Free heap memory if it was heap-allocated
			if (_heapAllocatedVars.Contains(name))
			{
				LLVMValueRef actualHeapPtr;
				if (type is SliceTypeSymbol sliceType)
				{
					var sliceLayout = GetLLVMType(sliceType);
					var ptrField = _builder.BuildGEP2(sliceLayout, ptrAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "slice_ptr_field");
					actualHeapPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrField, "heap_ptr");
				}
				else
				{
					actualHeapPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrAlloc, "heap_ptr");
				}

				var freeFunc = _globals["free"];
				var freeType = _functionTypes["free"];
				_builder.BuildCall2(freeType, freeFunc, new LLVMValueRef[] { actualHeapPtr }, "");
			}
		}
	}

	/// <summary>
	/// True when a union carries at least one dtor-bearing payload variant (i.e. it can own a
	/// runtime resource that must be released when the active variant is <c>Some</c>). Options
	/// wrapping references (NPO) have no inner destructor and are naturally excluded.
	/// </summary>
	private bool UnionNeedsTagCheckedCleanup(UnionTypeSymbol unionType)
	{
		return unionType.Fields.Any(f => !f.IsVoidVariant
			&& _bindingContext!.OverloadedFunctions.ContainsKey($"{f.Type.Name}.~{f.Type.Name}"));
	}

	/// <summary>
	/// Branching tag-checked destructor for a ResourceMove-style union (e.g. <c>Option&lt;T&gt;</c>
	/// wrapping a move type). Loads the active variant tag and, only for a payload variant whose
	/// <c>~T()</c> is registered, resets the slot to <c>None</c> (reset-before-drop, volatile) and
	/// invokes the inner destructor on the payload. A panic inside <c>~T()</c> therefore reads a
	/// <c>None</c> slot and cannot double-free.
	/// </summary>
	private void EmitUnionTagCheckedCleanup(string name, LLVMValueRef ptrAlloc, UnionTypeSymbol unionType)
	{
		var dropped = unionType.Fields
			.Where(f => !f.IsVoidVariant)
			.Select(f => (Field: f, Index: GetFieldIndex(unionType, f!.Name)))
			.Where(t => _bindingContext!.OverloadedFunctions.ContainsKey($"{t.Field.Type.Name}.~{t.Field.Type.Name}"))
			.ToList();

		if (dropped.Count == 0)
			return;

		var unionLayout = GetLLVMType(unionType);
		var currentFunc = _builder.InsertBlock.Parent;

		// Load the active tag (struct index 0).
		var tagPtr = _builder.BuildGEP2(unionLayout, ptrAlloc, new LLVMValueRef[] {
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
		}, "union_tag_ptr");
		var tagVal = _builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "union_tag_val");

		var noneIndex = unionType.NoneVariant is not null ? GetFieldIndex(unionType, unionType.NoneVariant.Name) : 0;
		var noneTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)noneIndex);

		// Forward if/else-if chain over each dtor-bearing variant, rejoining at `after`.
		var after = currentFunc.AppendBasicBlock($"{name}_cleanup_after");

		for (var i = 0; i < dropped.Count; i++)
		{
			var (field, fieldIndex) = dropped[i];
			var isLast = i == dropped.Count - 1;

			var failBlock = isLast ? after : currentFunc.AppendBasicBlock($"{name}_cleanup_chk_{i + 1}");
			var dropBlock = currentFunc.AppendBasicBlock($"{name}_cleanup_drop_{i}");

			var isMatch = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tagVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), "tag_match");
			_builder.BuildCondBr(isMatch, dropBlock, failBlock);

			// Drop body: reset-before-drop (store None tag) THEN call inner ~T() on payload so a
			// panic during ~T() reads a None slot and cannot double-free. (LLVMSharp exposes no
			// volatile-store primitive, but DSE cannot elide a store feeding the dtor call.)
			_builder.PositionAtEnd(dropBlock);
			_builder.BuildStore(noneTag, tagPtr);
			var payloadPtr = _builder.BuildGEP2(unionLayout, ptrAlloc, new LLVMValueRef[] {
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
			}, $"{name}_payload");
			var castPtr = _builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(field.Type), 0), $"{name}_payload_ptr");
			var disposeBase = $"{field.Type.Name}.~{field.Type.Name}";
			var disposeSymbol = _bindingContext!.OverloadedFunctions[disposeBase].First();
			var disposeCallee = _globals[disposeSymbol.Name];
			var disposeType = _functionTypes[disposeSymbol.Name];
			_builder.BuildCall2(disposeType, disposeCallee, new LLVMValueRef[] { castPtr }, "");
			_builder.BuildBr(after);

			_builder.PositionAtEnd(failBlock);
		}

		_builder.PositionAtEnd(after);
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
						return LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)(long)-negInt.Value));
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
			default:
				return LLVMValueRef.CreateConstNull(llvmType);
		}
	}

	private static bool IsSimpleConstant(ExpressionSyntax expr) =>
		expr is IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or BooleanLiteralExpressionSyntax or CharacterLiteralExpressionSyntax;

	private LLVMTypeRef GetLLVMType(TypeSymbol t)
	{
		if (t is null)
			return LLVMTypeRef.Int32;

		if (t is PointerTypeSymbol)
			return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		if (t is RawPointerTypeSymbol rawPtr)
			return LLVMTypeRef.CreatePointer(GetLLVMType(rawPtr.ElementType), 0);

		if (t is SliceTypeSymbol)
		{
			var opaquePtr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
			return LLVMTypeRef.CreateStruct(new LLVMTypeRef[] { opaquePtr, LLVMTypeRef.Int32 }, false);
		}

		if (t is ArrayTypeSymbol arr)
		{
			return LLVMTypeRef.CreateArray(GetLLVMType(arr.ElementType), (uint)arr.Size);
		}

		if (t is UnionTypeSymbol union && union.IsNpoEligible)
			return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		if (t is EnumTypeSymbol enumType)
			return GetLLVMType(enumType.StorageType);

		if (t is StructTypeSymbol || t is UnionTypeSymbol)
		{
			if (_llvmStructTypes.TryGetValue(t.Name, out var typeRef))
				return typeRef;
		}

		return t.Name switch
		{
			"void" => LLVMTypeRef.Void,
			"int" => LLVMTypeRef.Int32,
			"uint" => LLVMTypeRef.Int32,
			"long" => LLVMTypeRef.Int64,
			"ulong" => LLVMTypeRef.Int64,
			"short" => LLVMTypeRef.Int16,
			"ushort" => LLVMTypeRef.Int16,
			"byte" => LLVMTypeRef.Int8,
			"sbyte" => LLVMTypeRef.Int8,
			"double" => LLVMTypeRef.Double,
			"bool" => LLVMTypeRef.Int1,
			"string" or "ptr" => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
			"char" => LLVMTypeRef.Int8,
			_ => _llvmStructTypes.TryGetValue(t.Name, out var foundType) ? foundType : LLVMTypeRef.Int32
		};
	}

	private (LLVMValueRef ptr, TypeSymbol type) GetFieldPointer(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
		{
			if (!_locals.TryGetValue(id.Name, out var structPtr))
			{
				// If the identifier is a field/variant of 'this' in an extension block, resolve its pointer implicitly!
				if (_locals.TryGetValue("this", out var thisPtr))
				{
					var thisType = _variableTypes["this"] as PointerTypeSymbol;
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
							return (fieldPtr, field.Type);
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
								return (actualThisPtr, field.Type);

							var fieldIndex = GetFieldIndex(unionType, id.Name);
							var structLayoutTy = GetLLVMType(unionType);

							// For unions, access the payload (index 1 of the struct) and cast it to the variant's concrete type
							var payloadPtr = _builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] {
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
							}, "union_payload_ptr");

							var castPtr = _builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(field.Type), 0), "payload_cast_ptr");
							return (castPtr, field.Type);
						}
					}
				}

				throw new InvalidOperationException($"Undefined variable '{id.Name}'");
			}

			var type = _variableTypes[id.Name];
			var isReference = type is PointerTypeSymbol;
			var isHeap = _heapAllocatedVars.Contains(id.Name) && type is not SliceTypeSymbol;

			if (isReference || isHeap)
			{
				var actualPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), structPtr, "loaded_ptr");
				var innerType = type is PointerTypeSymbol ptrType ? ptrType.ReferencedType : type;
				return (actualPtr, innerType);
			}

			return (structPtr, type);
		}
		else if (expr is MemberAccessExpressionSyntax m)
		{
			var (parentPtr, parentType) = GetFieldPointer(m.Expression);

			if (parentType is SliceTypeSymbol sliceType && m.MemberName == "Length")
			{
				var structLayout = GetLLVMType(sliceType);
				var lengthPtr = _builder.BuildGEP2(structLayout, parentPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "len_ptr");
				return (lengthPtr, TypeSymbol.Int);
			}

			// Arrow operator: parentPtr is a pointer to a struct pointer; load it first
			if (m.Operator == "->")
			{
				var rawPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), parentPtr, "arrow_load");
				var structType = (StructTypeSymbol)parentType;
				var fieldIndex = GetFieldIndex(structType, m.MemberName);
				var fieldType = structType.Fields[fieldIndex].Type;

				var structLayoutTy = GetLLVMType(parentType);
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

				var fieldPtr = _builder.BuildGEP2(structLayoutTy, rawPtr, new LLVMValueRef[] { zero, index }, "arrow_field_ptr");
				return (fieldPtr, fieldType);
			}

			if (parentType is UnionTypeSymbol unionType)
			{
				var fieldIndex = GetFieldIndex(unionType, m.MemberName);
				var fieldType = unionType.Fields[fieldIndex].Type;

				// Null-Pointer Optimization: the flat slot IS the ref/refvar value. Reading u.Some
				// yields the flat pointer itself; there is no tag/payload struct to index into.
				if (unionType.IsNpoEligible && !unionType.Fields[fieldIndex].IsVoidVariant)
					return (parentPtr, fieldType);

				var structLayoutTy = GetLLVMType(parentType);
				var payloadPtr = _builder.BuildGEP2(structLayoutTy, parentPtr, new LLVMValueRef[] {
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "union_payload_ptr");

				var castPtr = _builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(fieldType), 0), "payload_cast_ptr");
				return (castPtr, fieldType);
			}

			var dotStructType = (StructTypeSymbol)parentType;
			var dotFieldIndex = GetFieldIndex(dotStructType, m.MemberName);
			var dotFieldType = dotStructType.Fields[dotFieldIndex].Type;

			var dotStructLayoutTy = GetLLVMType(parentType);
			var dotZero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			var dotIndex = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)dotFieldIndex);

			var dotFieldPtr = _builder.BuildGEP2(dotStructLayoutTy, parentPtr, new LLVMValueRef[] { dotZero, dotIndex }, "member_ptr");
			return (dotFieldPtr, dotFieldType);
		}
		else if (expr is IndexExpressionSyntax idx)
		{
			var (parentPtr, parentType) = GetFieldPointer(idx.Left);
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
				return (elementPtr, sliceType.ElementType);
			}
			else if (parentType is ArrayTypeSymbol arrayType)
			{
				var arrayLayout = GetLLVMType(arrayType);
				var limit = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)arrayType.Size);

				InjectBoundsCheck(idx, indexVal, limit);

				var elementPtr = _builder.BuildGEP2(arrayLayout, parentPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), indexVal }, "element_ptr");
				return (elementPtr, arrayType.ElementType);
			}
		}
else if (expr is BorrowExpressionSyntax b)
		{
			return GetFieldPointer(b.Expression); // Unpack the inner expression pointer recursively
		}
		else if (expr is UnaryExpressionSyntax castExpr && castExpr.Operator.StartsWith("(") && castExpr.Operator.EndsWith(")") && _unsafeDepth == 0)
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
					return MaterializeEnumCastOption(castEnum, castOperand, castOperandType, castOption);
				}
			}
		}

		throw new InvalidOperationException($"Unsupported {expr.GetType()} field pointer expression");
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

	private LLVMValueRef EmitStructInitialization(StructInitializationExpressionSyntax expr)
	{
		var typeSymbol = _bindingContext!.ResolveType(expr.StructTypeName) as StructTypeSymbol;
		var structLayout = GetLLVMType(typeSymbol!);
		var tempAlloc = _builder.BuildAlloca(structLayout, "struct_tmp");
		EmitStructInitializationInPlace(expr, tempAlloc);
		return tempAlloc;
	}

	private void EmitStructInitializationInPlace(StructInitializationExpressionSyntax expr, LLVMValueRef destPtr)
	{
		var typeSymbol = _bindingContext!.ResolveType(expr.StructTypeName);

		if (typeSymbol is UnionTypeSymbol unionType)
		{
			var unionLayout = GetLLVMType(unionType);
			var init = expr.Initializers[0];
			var fieldIndex = GetFieldIndex(unionType, init.MemberName);
			var field = unionType.Fields[fieldIndex];

			// Null-Pointer Optimization: the flat slot IS the pointer. Some(x) stores the ref
			// value directly into the slot; None stores nullptr. No tag, no payload struct.
			if (unionType.IsNpoEligible)
			{
				if (field.IsVoidVariant)
				{
					_builder.BuildStore(LLVMValueRef.CreateConstPointerNull(unionLayout), destPtr);
				}
				else
				{
					var value = EmitExpression(init.Expression);
					_builder.BuildStore(value, destPtr);
				}
				return;
			}

			// 1. Store the active variant index into the i8 tag field (Index 0)
			var tagPtr = _builder.BuildGEP2(unionLayout, destPtr, new LLVMValueRef[] {
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "union_tag_ptr");
			_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);

			// 2. Store the variant value into the payload field (Index 1) if not void
			if (!field.IsVoidVariant)
			{
				var payloadPtr = _builder.BuildGEP2(unionLayout, destPtr, new LLVMValueRef[] {
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "union_payload_ptr");

				var castPtr = _builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(field.Type), 0), "payload_cast_ptr");

				// Aggregate variants are materialized in place (EmitStructInitialization returns an
				// alloca pointer, not an aggregate value); store scalars by value.
				if (init.Expression is StructInitializationExpressionSyntax structVariant)
				{
					EmitStructInitializationInPlace(structVariant, castPtr);
				}
				else if (init.Expression is ParenthesizedStructInitializerExpressionSyntax parenVariant)
				{
					EmitParenthesizedStructInitializationInPlace(parenVariant, castPtr);
				}
				else
				{
					var value = EmitExpression(init.Expression);
					_builder.BuildStore(value, castPtr);
				}
			}

			return;
		}

		// Struct fallback: Cast the resolved typeSymbol to StructTypeSymbol
		var structType = (StructTypeSymbol)typeSymbol!;
		var structLayout = GetLLVMType(structType);

		foreach (var init in expr.Initializers)
		{
			var fieldIndex = GetFieldIndex(structType, init.MemberName);
			var targetFieldPtr = _builder.BuildGEP2(structLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex)
			}, "init_field_ptr");

			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nestedParen)
			{
				EmitParenthesizedStructInitializationInPlace(nestedParen, targetFieldPtr);
			}
			else if (init.Expression is StructInitializationExpressionSyntax structInit)
			{
				EmitStructInitializationInPlace(structInit, targetFieldPtr);
			}
			else
			{
				var value = EmitExpression(init.Expression);
				_builder.BuildStore(value, targetFieldPtr);
			}
		}
	}

	private LLVMValueRef EmitHeapAllocation(HeapAllocationExpressionSyntax expr)
	{
		var structInit = (StructInitializationExpressionSyntax)expr.Expression;
		var typeSymbol = _bindingContext!.ResolveType(structInit.StructTypeName) as StructTypeSymbol;

		var structSize = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)(typeSymbol!.Fields.Count * 8));

		var mallocFunc = _globals["malloc"];
		var mallocType = _functionTypes["malloc"];
		var rawPtr = _builder.BuildCall2(mallocType, mallocFunc, new LLVMValueRef[] { structSize }, "heap_alloc");

		EmitStructInitializationInPlace(structInit, rawPtr);
		return rawPtr;
	}

	private LLVMValueRef EmitArrayInitialization(ArrayInitializationExpressionSyntax expr)
	{
		var elementType = GetExprType(expr.Elements[0]);
		var arrayTypeSymbol = new ArrayTypeSymbol(elementType, expr.Elements.Count);
		var arrayLayout = GetLLVMType(arrayTypeSymbol);

		var tempAlloc = _builder.BuildAlloca(arrayLayout, "arr_tmp");
		EmitArrayInitializationInPlace(expr, tempAlloc, arrayTypeSymbol);
		return tempAlloc;
	}

	private void EmitArrayInitializationInPlace(ArrayInitializationExpressionSyntax expr, LLVMValueRef destPtr, ArrayTypeSymbol arrayType)
	{
		// `{}` empty initializer on an explicitly-sized array: zero-initialize per Memory spec §5.
		if (expr.Elements.Count == 0 && arrayType.Size > 0)
		{
			EmitZeroInitArray(destPtr, arrayType);
			return;
		}

		var arrayLayout = GetLLVMType(arrayType);

		for (var i = 0; i < expr.Elements.Count; i++)
		{
			var elementPtr = _builder.BuildGEP2(arrayLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)i)
			}, "arr_el");
			var elementExpr = expr.Elements[i];

			if (elementExpr is StructInitializationExpressionSyntax structInit)
			{
				EmitStructInitializationInPlace(structInit, elementPtr);
			}
			else if (elementExpr is ArrayInitializationExpressionSyntax nestedArr)
			{
				EmitArrayInitializationInPlace(nestedArr, elementPtr, (arrayType.ElementType as ArrayTypeSymbol)!);
			}
			else
			{
				var val = EmitExpression(elementExpr);
				_builder.BuildStore(val, elementPtr);
			}
		}
	}

	private void EmitMemset(LLVMValueRef destPtr, long byteCount)
	{
		if (byteCount <= 0) return;
		var memsetFunc = _globals["memset"];
		var memsetType = _functionTypes["memset"];
		var destCasted = _builder.BuildBitCast(destPtr, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "zero_dest");
		_builder.BuildCall2(memsetType, memsetFunc, new LLVMValueRef[]
		{
			destCasted,
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)byteCount)
		}, "");
	}

	// The real storage size of an LLVM type (including alignment padding), from the module
	// data layout. GetByteSize reflects the semantic size and can under-report padded structs.
	private long GetLLVMStoreSize(LLVMTypeRef type)
	{
		var targetData = LLVMTargetDataRef.FromStringRepresentation(_module.DataLayout);
		return (long)targetData.StoreSizeOfType(type);
	}

	// Whether zeroing every byte of a value yields a valid empty state:
	// primitives/pointers/slices/strings and NPO options (None == flat address 0) are safe;
	// tagged unions (None tag != 0) and types with custom destructors (ResourceMove) are not.
	private bool TypeIsZeroInitSafe(TypeSymbol t)
	{
		if (t is ArrayTypeSymbol arr) return TypeIsZeroInitSafe(arr.ElementType);
		if (t is UnionTypeSymbol us)
		{
			// Null-Pointer-Optimized option: None is the flat zero pointer — memset is safe.
			if (us.IsNpoEligible) return true;
			return false; // tagged union: None requires an explicit tag store, never raw zeroing
		}
		if (t is StructTypeSymbol st)
		{
			if (_bindingContext!.OverloadedFunctions.ContainsKey($"{st.Name}.~{st.Name}")) return false;
			return st.Fields.All(f => TypeIsZeroInitSafe(f.Type));
		}
		return true;
	}

	private void EmitZeroInitArray(LLVMValueRef destPtr, ArrayTypeSymbol arrayType)
	{
		var elementType = arrayType.ElementType;

		// Fast path: Trivial/Large-Copy element (incl. NPO option fields) → single memset.
		// Byte count comes from LLVM's real storage size to account for struct padding.
		if (TypeIsZeroInitSafe(elementType))
		{
			EmitMemset(destPtr, GetLLVMStoreSize(GetLLVMType(arrayType)));
			return;
		}

		// Linear restriction (Memory spec §5): no raw whole-array memset for
		// tagged unions or types with custom destructors. Initialize per element.
		var arrayLayout = GetLLVMType(arrayType);
		for (var i = 0; i < arrayType.Size; i++)
		{
			var elementPtr = _builder.BuildGEP2(arrayLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)i)
			}, "zero_el");

			// A tagged union's empty state is its None tag; store it explicitly.
			if (elementType is UnionTypeSymbol unionEl)
			{
				var unionLayout = GetLLVMType(unionEl);
				var tagPtr = _builder.BuildGEP2(unionLayout, elementPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "zero_tag");
				var noneTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)GetFieldIndex(unionEl, unionEl.NoneVariant.Name));
				_builder.BuildStore(noneTag, tagPtr);
				continue;
			}

			// Struct with a destructor (ResourceMove) or a nested tagged union:
			// explicit per-element zero init (no single pooled memset).
			EmitMemset(elementPtr, GetLLVMStoreSize(GetLLVMType(elementType)));
		}
	}

	private LLVMValueRef CoerceArrayToSlice(LLVMValueRef arrayPtr, TypeSymbol argTy, SliceTypeSymbol sliceTy)
	{
		var arrayTy = argTy is PointerTypeSymbol ptr ? ptr.ReferencedType as ArrayTypeSymbol : argTy as ArrayTypeSymbol;

		var fatStructType = GetLLVMType(sliceTy);
		var sliceAlloc = _builder.BuildAlloca(fatStructType, "slice_tmp");

		var ptrField = _builder.BuildGEP2(fatStructType, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "ptr_field");
		var castPtr = _builder.BuildBitCast(arrayPtr, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
		_builder.BuildStore(castPtr, ptrField);

		var sizeField = _builder.BuildGEP2(fatStructType, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "size_field");
		_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)arrayTy!.Size), sizeField);

		return _builder.BuildLoad2(fatStructType, sliceAlloc, "slice_val");
	}

	private static bool EndsWithReturn(SyntaxNode s) => s switch
	{
		BlockStatementSyntax b => b.Statements.Count > 0 && b.Statements[^1] is ReturnStatementSyntax,
		ReturnStatementSyntax => true,
		_ => false,
	};
	public void Dispose()
	{
		_builder.Dispose();
		_module.Dispose();
	}

	private LLVMValueRef EmitArrayReplication(ArrayReplicationExpressionSyntax expr)
	{
		var valueType = GetExprType(expr.Value);
		var countVal = (expr.Count is IntegerLiteralExpressionSyntax countLit) ? (int)countLit.Value : 0;
		var arrayTypeSymbol = new ArrayTypeSymbol(valueType, countVal);
		var arrayLayout = GetLLVMType(arrayTypeSymbol);

		var tempAlloc = _builder.BuildAlloca(arrayLayout, "arr_repl_tmp");
		EmitArrayReplicationInPlace(expr, tempAlloc, arrayTypeSymbol);
		return tempAlloc;
	}

	private void EmitArrayReplicationInPlace(ArrayReplicationExpressionSyntax expr, LLVMValueRef destPtr, ArrayTypeSymbol arrayType)
	{
		var arrayLayout = GetLLVMType(arrayType);

		var currentFunc = _builder.InsertBlock.Parent;
		var condBlock = currentFunc.AppendBasicBlock("repl_cond");
		var bodyBlock = currentFunc.AppendBasicBlock("repl_body");
		var endBlock = currentFunc.AppendBasicBlock("repl_end");

		// Initialize counter: alloca and store 0
		var counterAlloc = _builder.BuildAlloca(LLVMTypeRef.Int32, "repl_i");
		_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), counterAlloc);
		_builder.BuildBr(condBlock);

		// Condition verification
		_builder.PositionAtEnd(condBlock);
		var iVal = _builder.BuildLoad2(LLVMTypeRef.Int32, counterAlloc, "i_val");
		var limitVal = EmitExpression(expr.Count);
		var cmp = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, iVal, limitVal, "repl_cmp");
		_builder.BuildCondBr(cmp, bodyBlock, endBlock);

		// Loop fill body
		_builder.PositionAtEnd(bodyBlock);
		var elementPtr = _builder.BuildGEP2(arrayLayout, destPtr, new LLVMValueRef[]
		{
		LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), iVal
		}, "repl_el");

		if (expr.Value is ParenthesizedStructInitializerExpressionSyntax nestedParen)
		{
			EmitParenthesizedStructInitializationInPlace(nestedParen, elementPtr);
		}
		else if (expr.Value is StructInitializationExpressionSyntax structInit)
		{
			EmitStructInitializationInPlace(structInit, elementPtr);
		}
		else
		{
			var val = EmitExpression(expr.Value);
			_builder.BuildStore(val, elementPtr);
		}

		// Increment index
		var nextI = _builder.BuildAdd(iVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1), "next_i");
		_builder.BuildStore(nextI, counterAlloc);
		_builder.BuildBr(condBlock);

		_builder.PositionAtEnd(endBlock);
	}

	private LLVMValueRef EmitParenthesizedStructInitialization(ParenthesizedStructInitializerExpressionSyntax expr)
	{
		var typeSymbol = _bindingContext!.ResolveType(expr.ResolvedStructTypeName!) as StructTypeSymbol;
		var structLayout = GetLLVMType(typeSymbol!);
		var tempAlloc = _builder.BuildAlloca(structLayout, "struct_tmp");
		EmitParenthesizedStructInitializationInPlace(expr, tempAlloc);
		return tempAlloc;
	}

	private void EmitParenthesizedStructInitializationInPlace(ParenthesizedStructInitializerExpressionSyntax expr, LLVMValueRef destPtr)
	{
		var typeSymbol = _bindingContext!.ResolveType(expr.ResolvedStructTypeName!) as StructTypeSymbol;
		var structLayout = GetLLVMType(typeSymbol!);

		foreach (var init in expr.Initializers)
		{
			var fieldIndex = GetFieldIndex(typeSymbol!, init.MemberName);
			var targetFieldPtr = _builder.BuildGEP2(structLayout, destPtr, new LLVMValueRef[]
			{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex)
			}, "init_field_ptr");

			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nestedInit)
			{
				EmitParenthesizedStructInitializationInPlace(nestedInit, targetFieldPtr);
			}
			else if (init.Expression is StructInitializationExpressionSyntax structInit)
			{
				EmitStructInitializationInPlace(structInit, targetFieldPtr);
			}
			else
			{
				var value = EmitExpression(init.Expression);
				_builder.BuildStore(value, targetFieldPtr);
			}
		}
	}

	private LLVMValueRef EmitHeapArrayAllocation(HeapArrayAllocationExpressionSyntax expr)
	{
		var elementType = _bindingContext!.ResolveType(expr.ElementTypeName);
		var elementLlvmType = GetLLVMType(elementType!);

		// 1. Evaluate the dynamic count requested by the user
		var countVal = EmitExpression(expr.CountExpression);
		var count64 = _builder.BuildZExt(countVal, LLVMTypeRef.Int64, "count_64");

		// 2. Calculate runtime size (count * sizeof(T))
		// We use a GEP trick to get the exact size of the element type from LLVM safely
		var nullPtr = LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(elementLlvmType, 0));
		var sizePtr = _builder.BuildGEP2(elementLlvmType, nullPtr, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "size_ptr");
		var elementSize64 = _builder.BuildPtrToInt(sizePtr, LLVMTypeRef.Int64, "element_size");

		var totalSize = _builder.BuildMul(count64, elementSize64, "total_alloc_size");

		// 3. Call malloc
		var mallocFunc = _globals["malloc"];
		var mallocType = _functionTypes["malloc"];
		var rawPtr = _builder.BuildCall2(mallocType, mallocFunc, new LLVMValueRef[] { totalSize }, "heap_arr_alloc");

		// 4. Assemble the Slice Fat Pointer { ptr, i32 }
		var sliceType = new SliceTypeSymbol(elementType!);
		var sliceLayout = GetLLVMType(sliceType);
		var sliceAlloc = _builder.BuildAlloca(sliceLayout, "slice_tmp");

		// Store ptr
		var ptrField = _builder.BuildGEP2(sliceLayout, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "ptr_field");
		_builder.BuildStore(rawPtr, ptrField);

		// Store length
		var sizeField = _builder.BuildGEP2(sliceLayout, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "size_field");
		_builder.BuildStore(countVal, sizeField);

		return _builder.BuildLoad2(sliceLayout, sliceAlloc, "slice_val");
	}

	public int GetByteSize(TypeSymbol type)
	{
		if (type is null) return 0;
		if (type.Equals(TypeSymbol.String) || type is PointerTypeSymbol) return 8; // 64-bit pointers
		if (type is SliceTypeSymbol) return 16; // Fat Pointer: { ptr, i32 }
		if (type.Equals(TypeSymbol.Int) || type.Equals(TypeSymbol.UInt)) return 4;
		if (type.Equals(TypeSymbol.Long) || type.Equals(TypeSymbol.ULong) || type.Equals(TypeSymbol.Double)) return 8;
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

	private void EmitSwitchStatement(SwitchStatementSyntax sw)
	{
		var switchTargetType = GetExprType(sw.Expression);
		EnumTypeSymbol? enumTarget = null;
		if (switchTargetType is EnumTypeSymbol et)
		{
			enumTarget = et;
		}
		else if (switchTargetType is PointerTypeSymbol pt && pt.ReferencedType is EnumTypeSymbol pet)
		{
			enumTarget = pet;
		}

		if (enumTarget is not null)
		{
			var value = EmitExpression(sw.Expression);
			EmitEnumSwitch(sw, enumTarget, value);
			return;
		}

		var (targetVal, unionType) = GetFieldPointer(sw.Expression);
		var isRefTarget = GetExprType(sw.Expression) is PointerTypeSymbol;
		var isMutableRef = GetExprType(sw.Expression) is PointerTypeSymbol ptrSymbol && ptrSymbol.IsMutable; // Capture original reference mutability

		if (unionType is PointerTypeSymbol ptr)
		{
			unionType = ptr.ReferencedType;
		}

		var unionLayout = GetLLVMType(unionType);

		var isNpo = unionType is UnionTypeSymbol npoUt && npoUt.IsNpoEligible;

		// 1. Load the discriminator: for NPO unions this is the flat pointer value
		//    itself (None = null); for tagged unions it is the i8 tag at struct index 0.
		LLVMValueRef discriminator;
		if (isNpo)
		{
			discriminator = _builder.BuildLoad2(unionLayout, targetVal, "flat_ptr_val");
		}
		else
		{
			var tagPtr = _builder.BuildGEP2(unionLayout, targetVal, new LLVMValueRef[] {
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "tag_ptr");
			discriminator = _builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "tag_val");
		}

		var currentFunc = _builder.InsertBlock.Parent;
		var endBlock = currentFunc.AppendBasicBlock("sw_end");
		var nextCheckBlock = _builder.InsertBlock;

		for (int i = 0; i < sw.Cases.Count; i++)
		{
			var c = sw.Cases[i];
			_builder.PositionAtEnd(nextCheckBlock);

			if (c.IsDefault || c.VariantName == "_")
			{
				var bodyBlock = currentFunc.AppendBasicBlock("default_body");
				_builder.BuildBr(bodyBlock);

				_builder.PositionAtEnd(bodyBlock);
				EmitSwitchCaseBody(c, targetVal, unionType, "", isDefault: true, isRefTarget, isMutableRef);
				if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
					_builder.BuildBr(endBlock);

				break;
			}
			else
			{
				var unionTypeSym = unionType as UnionTypeSymbol;
				var fieldIndex = GetFieldIndex(unionTypeSym!, c.VariantName);

				var caseBodyBlock = currentFunc.AppendBasicBlock($"case_{c.VariantName}_body");
				nextCheckBlock = currentFunc.AppendBasicBlock($"case_{c.VariantName}_next");

				LLVMValueRef cond;
				if (isNpo)
				{
					// NPO: Some (payload) matches non-null; None (void) matches null.
					var isNone = unionTypeSym!.Fields[fieldIndex].IsVoidVariant;
					var nullConst = LLVMValueRef.CreateConstPointerNull(unionLayout);
					cond = isNone
						? _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, discriminator, nullConst, "npo_is_none")
						: _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, discriminator, nullConst, "npo_is_some");
				}
				else
				{
					cond = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, discriminator, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), "tag_match");
				}
				_builder.BuildCondBr(cond, caseBodyBlock, nextCheckBlock);

				_builder.PositionAtEnd(caseBodyBlock);
				EmitSwitchCaseBody(c, targetVal, unionType, c.VariantName, isDefault: false, isRefTarget, isMutableRef);
				if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
					_builder.BuildBr(endBlock);
			}
		}

		_builder.PositionAtEnd(nextCheckBlock);
		if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			_builder.BuildBr(endBlock);

		_builder.PositionAtEnd(endBlock);
	}

	private void EmitEnumSwitch(SwitchStatementSyntax sw, EnumTypeSymbol enumTarget, LLVMValueRef value)
	{
		var storageTy = GetLLVMType(enumTarget);
		var currentFunc = _builder.InsertBlock.Parent;
		var endBlock = currentFunc.AppendBasicBlock("sw_end");
		var nextCheckBlock = _builder.InsertBlock;
		var hasDefault = false;

		for (int i = 0; i < sw.Cases.Count; i++)
		{
			var c = sw.Cases[i];
			_builder.PositionAtEnd(nextCheckBlock);

			if (c.IsDefault || c.VariantName == "_")
			{
				hasDefault = true;
				var bodyBlock = currentFunc.AppendBasicBlock("default_body");
				_builder.BuildBr(bodyBlock);

				_builder.PositionAtEnd(bodyBlock);
				EmitBlock(new BlockStatementSyntax(c.Span, c.Body));
				if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
					_builder.BuildBr(endBlock);

				break;
			}
			else
			{
				var variant = enumTarget.FindVariant(c.VariantName) ?? enumTarget.Variants[0];

				var caseBodyBlock = currentFunc.AppendBasicBlock($"enum_case_{c.VariantName}_body");
				nextCheckBlock = currentFunc.AppendBasicBlock($"enum_case_{c.VariantName}_next");

				var cond = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value,
					LLVMValueRef.CreateConstInt(storageTy, unchecked((ulong)variant.Value)), "enum_switch_match");
				_builder.BuildCondBr(cond, caseBodyBlock, nextCheckBlock);

				_builder.PositionAtEnd(caseBodyBlock);
				EmitBlock(new BlockStatementSyntax(c.Span, c.Body));
				if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
					_builder.BuildBr(endBlock);
			}
		}

		_builder.PositionAtEnd(nextCheckBlock);
		if (!hasDefault)
		{
			EmitEnumSwitchTrapDefault();
		}
		else if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			_builder.BuildBr(endBlock);
		}

		_builder.PositionAtEnd(endBlock);
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

	private void EmitSwitchCaseBody(SwitchCaseSyntax c, LLVMValueRef targetVal, TypeSymbol unionType, string variantName, bool isDefault, bool isRefTarget, bool isMutableRef)
	{
		var unionTypeSym = unionType as UnionTypeSymbol;

		if (c.VariableName is not null && !isDefault)
		{
			var fieldIndex = GetFieldIndex(unionTypeSym!, variantName);
			var field = unionTypeSym.Fields[fieldIndex];

			var isNpo = unionTypeSym.IsNpoEligible;

			LLVMValueRef payloadPtr;
			LLVMValueRef castPtr;
			if (isNpo)
			{
				// NPO: the payload IS the flat pointer slot itself (no {0,1} GEP, no bitcast).
				payloadPtr = targetVal;
				castPtr = targetVal;
			}
			else
			{
				var unionLayout = GetLLVMType(unionType);
				payloadPtr = _builder.BuildGEP2(unionLayout, targetVal, new LLVMValueRef[] {
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "union_payload_ptr");

				castPtr = _builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(GetLLVMType(field.Type), 0), "payload_cast_ptr");
			}

			// Structurally mirror the target reference mutability. For NPO the payload is
			// already the inner reference, so the promoted variable is that same reference.
			var varType = isNpo ? field.Type : (isRefTarget ? new PointerTypeSymbol(field.Type, isMutable: isMutableRef) : field.Type);

			var alloca = _builder.BuildAlloca(GetLLVMType(varType), c.VariableName);
			_locals[c.VariableName] = alloca;
			_variableTypes[c.VariableName] = varType;

			if (isNpo)
			{
				// NPO: the promoted variable holds the inner reference itself (the flat pointer
				// value), regardless of whether the target was taken by ref or by value.
				var val = _builder.BuildLoad2(GetLLVMType(field.Type), targetVal, "flat_payload_val");
				_builder.BuildStore(val, alloca);
			}
			else if (isRefTarget)
			{
				_builder.BuildStore(castPtr, alloca);
			}
			else
			{
				var val = _builder.BuildLoad2(GetLLVMType(field.Type), castPtr, "payload_val");
				_builder.BuildStore(val, alloca);

				// By-value switch over a ResourceMove-style union moves the payload into the case
				// binding (the sole owner now); reset the source slot to None so its own tag-checked
				// cleanup cannot drop the same resource a second time.
				if (UnionNeedsTagCheckedCleanup(unionTypeSym!))
				{
					var srcLayout = GetLLVMType(unionType);
					var srcTagPtr = _builder.BuildGEP2(srcLayout, targetVal, new LLVMValueRef[] {
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "move_src_tag_ptr");
					var moveNoneIdx = unionTypeSym.NoneVariant is not null ? GetFieldIndex(unionTypeSym, unionTypeSym.NoneVariant.Name) : 0;
					_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)moveNoneIdx), srcTagPtr);
				}
			}
		}

		EmitBlock(new BlockStatementSyntax(c.Span, c.Body));
	}
}
