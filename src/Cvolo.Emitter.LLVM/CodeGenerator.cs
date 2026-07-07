using System.Runtime.InteropServices;
using Cvolo.Analysis;
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
	private BindingContext? _bindingContext;
	private CompilationContext? _compilationContext; // Renamed to avoid LLVM _context conflict
	private CompilationUnitSyntax? _currentUnit;

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

		// Pass A: Declare all Nominal and Instantiated Structs as Opaque Shells
		foreach (var structType in bindingContext.StructTypes.Values)
		{
			if (!_llvmStructTypes.ContainsKey(structType.Name))
			{
				_llvmStructTypes[structType.Name] = _context.CreateNamedStruct(structType.Name);
			}
		}

		// Pass B: Define Struct Bodies recursively
		foreach (var structType in bindingContext.StructTypes.Values)
		{
			var llvmStruct = _llvmStructTypes[structType.Name];
			var fieldTypes = structType.Fields.Select(f => GetLLVMType(f.Type)).ToArray();
			llvmStruct.StructSetBody(fieldTypes, false);
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
						DeclareFunction(func, mangledName);
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
					// Keep 'main' / 'Main' global and unmangled
					var mangledName = (func.Name == "main" || func.Name == "Main")
						? "main"
						: bindingContext.GetMangledName(func.Name, ns);
					if (emittedFunctionNames.Add(mangledName))
					{
						EmitFunctionBody(func, mangledName);
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
		foreach (var param in func.Parameters)
		{
			var paramTypeSymbol = _bindingContext.ResolveType(param.Type)!;
			paramTypes.Add(GetLLVMType(paramTypeSymbol));
			paramSymbols.Add(paramTypeSymbol);
		}

		_functionParameterTypes[emitName] = paramSymbols;

		var funcType = LLVMTypeRef.CreateFunction(returnType, [.. paramTypes]);

		var llvmFunc = _module.AddFunction(emitName, funcType);

        if (emitName != "main")
        {
            llvmFunc.Linkage = LLVMLinkage.LLVMInternalLinkage;
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

		EmitBlock(func.Body);

		if (func.ReturnType == "void" && !EndsWithReturn(func.Body))
		{
			EmitCleanup([.. _locals.Keys]);
			_builder.BuildRetVoid();
		}
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
			case WhileStatementSyntax whileStmt:
				EmitWhileStatement(whileStmt);
				break;
			case ForStatementSyntax forStmt:
				EmitForStatement(forStmt);
				break;
		}
	}

	private void EmitReturnStatement(ReturnStatementSyntax ret)
	{
		EmitCleanup([.. _locals.Keys]);

		if (ret.Expression is not null)
		{
			var value = EmitExpression(ret.Expression);
			var type = GetExprType(ret.Expression);

			if (type is StructTypeSymbol structType)
			{
				// If the evaluated value is a memory pointer, load the struct before returning
				if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
				{
					var valReg = _builder.BuildLoad2(GetLLVMType(structType), value, "struct_ret_val");
					_builder.BuildRet(valReg);
				}
				else
				{
					_builder.BuildRet(value);
				}
			}
			else
			{
				_builder.BuildRet(value);
			}
		}
		else
		{
			_builder.BuildRetVoid();
		}
	}

	private LLVMValueRef EmitExpression(ExpressionSyntax expr)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax intLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)intLit.Value);
			case DoubleLiteralExpressionSyntax dblLit:
				return LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, dblLit.Value);
			case BooleanLiteralExpressionSyntax boolLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolLit.Value ? 1UL : 0UL);
			case StringLiteralExpressionSyntax strLit:
				return EmitStringLiteral(strLit.Value);
			case CharacterLiteralExpressionSyntax charLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, charLit.Value);
			case IdentifierExpressionSyntax id:
				return Load(id.Name);
			case MemberAccessExpressionSyntax m:
				{
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
			case StructInitializationExpressionSyntax s:
				return EmitStructInitialization(s);
			case ArrayInitializationExpressionSyntax a:
				return EmitArrayInitialization(a);
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

	private LLVMValueRef EmitCallExpression(CallExpressionSyntax call)
	{
		var mangledName = ResolveFunctionName(call.FunctionName, _currentUnit!);
		if (call.TypeArguments.Count > 0)
		{
			mangledName = $"{mangledName}<{string.Join(", ", call.TypeArguments)}>";
		}

		var callee = _globals[mangledName];
		var funcType = _functionTypes[mangledName];

		var args = new List<LLVMValueRef>();
		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var argExpr = call.Arguments[i];
			LLVMValueRef val;
			var valTy = GetExprType(argExpr);
			var paramTy = GetParamType(mangledName, i);

			if (paramTy is SliceTypeSymbol && valTy is ArrayTypeSymbol)
			{
				// Retrieve the pointer address of the array, not its loaded value!
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

				val = CoerceArrayToSlice(arrayPtr, (valTy as ArrayTypeSymbol)!, (paramTy as SliceTypeSymbol)!);
			}
			else
			{
				val = EmitExpression(argExpr);
			}

			// Promotions for variadic inputs (e.g. Bool and Char promoted to i32)
			var isVariadic = _astExterns.TryGetValue(mangledName, out var ext) && ext.IsVariadic;
			if (isVariadic && i >= _functionParameterTypes[mangledName].Count)
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

			args.Add(val);
		}

		var retTypeSymbol = _functionReturnTypes.TryGetValue(mangledName, out var ret) ? ret : TypeSymbol.Int;

		// Void functions must not have an instruction name assigned in LLVM IR
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

		if (bin.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var type = _variableTypes[id.Name];
			if (type is PointerTypeSymbol)
			{
				var actualPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptr, "target_ptr");
				_builder.BuildStore(right, actualPtr);
			}
			else
			{
				_builder.BuildStore(right, ptr);
			}

			return right;
		}
		else if (bin.Left is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, _) = GetFieldPointer(m);
			_builder.BuildStore(right, fieldPtr);
			return right;
		}
		else if (bin.Left is IndexExpressionSyntax idx)
		{
			var (elementPtr, _) = GetFieldPointer(idx);
			_builder.BuildStore(right, elementPtr);
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
		return unary.Operator switch
		{
			"-" => _builder.BuildNeg(operand),
			"!" => _builder.BuildNot(operand),
			"~" => _builder.BuildXor(operand, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, unchecked((ulong)-1))),
			_ => throw new InvalidOperationException($"Unknown unary operator '{unary.Operator}'"),
		};
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

		if (typeSymbol is not null)
		{
			var llvmType = GetLLVMType(typeSymbol);
			var alloca = _builder.BuildAlloca(llvmType, varDecl.Name);
			_locals[varDecl.Name] = alloca;
			_variableTypes[varDecl.Name] = typeSymbol;

			if (varDecl.Initializer is not null)
			{
				if (varDecl.Initializer is StructInitializationExpressionSyntax structInit)
				{
					EmitStructInitializationInPlace(structInit, alloca);
				}
				else if (varDecl.Initializer is ArrayInitializationExpressionSyntax arrInit)
				{
					EmitArrayInitializationInPlace(arrInit, alloca, (typeSymbol as ArrayTypeSymbol)!);
				}
				else
				{
					var value = EmitExpression(varDecl.Initializer);
					_builder.BuildStore(value, alloca);
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

	private LLVMValueRef Load(string name)
	{
		if (!_locals.TryGetValue(name, out var ptr))
			throw new InvalidOperationException($"Undefined variable '{name}'");

		var type = _variableTypes[name];
		var ty = GetLLVMType(type);

		var reg = _builder.BuildLoad2(ty, ptr, "load_val");

		if (type is PointerTypeSymbol ptrType)
		{
			var resolvedType = ptrType.ReferencedType;
			if (resolvedType == TypeSymbol.Int || resolvedType == TypeSymbol.Double || resolvedType == TypeSymbol.Bool || resolvedType == TypeSymbol.Char)
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
			var isHeap = _heapAllocatedVars.Contains(id.Name);

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
		return expr switch
		{
			IntegerLiteralExpressionSyntax => TypeSymbol.Int,
			DoubleLiteralExpressionSyntax => TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			IdentifierExpressionSyntax id => _variableTypes.TryGetValue(id.Name, out var type) ? type : TypeSymbol.Int,
			MemberAccessExpressionSyntax m => GetMemberAccessType(m),
			IndexExpressionSyntax idx => GetIndexExpressionType(idx),
			BorrowExpressionSyntax b => new PointerTypeSymbol(GetExprType(b.Expression), false),
			StructInitializationExpressionSyntax s => _bindingContext!.ResolveType(s.StructTypeName)!,
			UnaryExpressionSyntax u => GetExprType(u.Operand),
			TernaryExpressionSyntax t => GetExprType(t.ThenExpression),
			CallExpressionSyntax call => ResolveCallReturnType(call),
			BinaryExpressionSyntax bin => ResolveBinaryExpressionType(bin),
			HeapAllocationExpressionSyntax h => GetExprType(h.Expression),
			ArrayInitializationExpressionSyntax a => new ArrayTypeSymbol(a.Elements.Count > 0 ? GetExprType(a.Elements[0]) : TypeSymbol.Int, a.Elements.Count), // <-- Added
			_ => TypeSymbol.Int
		};
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

		return lTy;
	}

	private TypeSymbol GetMemberAccessType(MemberAccessExpressionSyntax m)
	{
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

		return TypeSymbol.Int;
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

	private void EmitCleanup(IEnumerable<string> variables)
	{
		foreach (var name in variables)
		{
			if (_heapAllocatedVars.Contains(name) && !_movedVars.Contains(name))
			{
				var ptrAlloc = _locals[name];
				var actualHeapPtr = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptrAlloc, "heap_ptr");

				var freeFunc = _globals["free"];
				var freeType = _functionTypes["free"];

				// Passed "" instead of "free_call" to ensure no void register is assigned
				_builder.BuildCall2(freeType, freeFunc, new LLVMValueRef[] { actualHeapPtr }, "");
			}
		}
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

	private LLVMTypeRef GetLLVMType(TypeSymbol t)
	{
		if (t is null)
			return LLVMTypeRef.Int32;

		if (t is PointerTypeSymbol)
			return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

		if (t is SliceTypeSymbol)
		{
			var opaquePtr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
			return LLVMTypeRef.CreateStruct(new LLVMTypeRef[] { opaquePtr, LLVMTypeRef.Int32 }, false);
		}

		if (t is ArrayTypeSymbol arr)
		{
			return LLVMTypeRef.CreateArray(GetLLVMType(arr.ElementType), (uint)arr.Size);
		}

		if (t is StructTypeSymbol)
		{
			if (_llvmStructTypes.TryGetValue(t.Name, out var typeRef))
				return typeRef;
		}

		return t.Name switch
		{
			"void" => LLVMTypeRef.Void,
			"int" => LLVMTypeRef.Int32,
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
				throw new InvalidOperationException($"Undefined variable '{id.Name}'");

			var type = _variableTypes[id.Name];
			var isReference = type is PointerTypeSymbol;
			var isHeap = _heapAllocatedVars.Contains(id.Name);

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

			var structType = (StructTypeSymbol)parentType;
			var fieldIndex = GetFieldIndex(structType, m.MemberName);
			var fieldType = structType.Fields[fieldIndex].Type;

			var structLayoutTy = GetLLVMType(parentType);
			var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);

			var fieldPtr = _builder.BuildGEP2(structLayoutTy, parentPtr, new LLVMValueRef[] { zero, index }, "member_ptr");
			return (fieldPtr, fieldType);
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

		throw new InvalidOperationException("Unsupported field pointer expression");
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
		var typeSymbol = _bindingContext!.ResolveType(expr.StructTypeName) as StructTypeSymbol;
		var structLayout = GetLLVMType(typeSymbol!);

		foreach (var init in expr.Initializers)
		{
			var fieldIndex = GetFieldIndex(typeSymbol!, init.MemberName);
			var targetFieldPtr = _builder.BuildGEP2(structLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex)
			}, "init_field_ptr");

			if (init.Expression is StructInitializationExpressionSyntax nestedInit)
			{
				EmitStructInitializationInPlace(nestedInit, targetFieldPtr);
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

	private LLVMValueRef CoerceArrayToSlice(LLVMValueRef arrayPtr, ArrayTypeSymbol arrayTy, SliceTypeSymbol sliceTy)
	{
		var fatStructType = GetLLVMType(sliceTy);
		var sliceAlloc = _builder.BuildAlloca(fatStructType, "slice_tmp");

		var ptrField = _builder.BuildGEP2(fatStructType, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "ptr_field");
		var castPtr = _builder.BuildBitCast(arrayPtr, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
		_builder.BuildStore(castPtr, ptrField);

		var sizeField = _builder.BuildGEP2(fatStructType, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "size_field");
		_builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)arrayTy.Size), sizeField);

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
}
