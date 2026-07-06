using System.Globalization;
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

namespace Cvolo.Emitter.LLVM;

public sealed class IrEmitter : IEmitter
{
	private readonly StringWriter _writer = new();
	private int _labelCounter;
	private int _localCounter;
	private int _stringIndex;
	private readonly Dictionary<string, string> _locals = [];
	private readonly List<string> _stringDefs = [];
	private readonly Dictionary<string, FunctionDeclarationSyntax> _astFunctions = [];
	private readonly Dictionary<string, ExternDeclarationSyntax> _astExterns = [];
	private readonly Dictionary<string, StructDeclarationSyntax> _astStructs = [];
	private readonly Dictionary<string, TypeSymbol> _functionReturnTypes = [];
	private readonly Dictionary<string, TypeSymbol> _variableTypes = [];
	private readonly Stack<List<VariableSymbol>> _blockVariables = new();
	private CompilationContext? _context;
	private BindingContext? _bindingContext;
	private CompilationUnitSyntax? _currentUnit;
	private readonly Dictionary<string, List<TypeSymbol>> _functionParameterTypes = [];

	public string Emit(IReadOnlyList<CompilationUnitSyntax> units, CompilationContext context, BindingContext bindingContext)
	{
		_context = context;
		_bindingContext = bindingContext;
		_writer.WriteLine("; ModuleID = 'cvolo_module'");
		_writer.WriteLine("source_filename = \"cvolo_module\"");
		_writer.WriteLine();

		_writer.WriteLine("declare ptr @malloc(i64)");
		_writer.WriteLine("declare void @free(ptr)");
		_writer.WriteLine("declare i32 @puts(ptr)");
		_writer.WriteLine("declare void @exit(i32)");

		// Pass 1: Register Structs
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
				{
					var mangledName = bindingContext.GetMangledName(structDecl.Name, ns);
					if (structDecl.GenericParameters.Count == 0) _astStructs[mangledName] = structDecl;
					else bindingContext.GenericStructTemplates[mangledName] = structDecl;
				}
			}
		}

		// Pass 2: Concrete Struct Layouts
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl && structDecl.GenericParameters.Count == 0)
				{
					var mangledName = bindingContext.GetMangledName(structDecl.Name, ns);
					var structType = bindingContext.ResolveType(mangledName) as StructTypeSymbol;
					if (structType != null)
					{
						var fieldTypes = string.Join(", ", structType.Fields.Select(f => Type(f.Type)));
						_writer.WriteLine($"%struct.{structType.Name} = type {{ {fieldTypes} }}");
					}
				}
			}
		}

		// Pass 2.5: Generic Struct Layouts (Deduplicated)
		var emittedStructs = new HashSet<string>();
		foreach (var structType in bindingContext.StructTypes.Values)
		{
			if (structType.Name.Contains('<') && emittedStructs.Add(structType.Name))
			{
				var fieldTypes = string.Join(", ", structType.Fields.Select(f => Type(f.Type)));
				_writer.WriteLine($"%\"struct.{structType.Name}\" = type {{ {fieldTypes} }}");
			}
		}
		_writer.WriteLine();

		// Pass 3: Register Metadata (Regular + Externs)
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			bindingContext.CurrentUnit = unit;
			bindingContext.CurrentNamespace = ns;
			var members = ns != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0 && !func.Name.Contains('<'))
				{
					var mangledName = bindingContext.GetMangledName(func.Name, ns);
					_functionReturnTypes[mangledName] = bindingContext.ResolveType(func.ReturnType)!;
					_functionParameterTypes[mangledName] = func.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
					_astFunctions[mangledName] = func;
				}
				else if (member is ExternDeclarationSyntax ext)
				{
					_astExterns[ext.Name] = ext;
					_functionReturnTypes[ext.Name] = bindingContext.ResolveType(ext.ReturnType)!;
					_functionParameterTypes[ext.Name] = ext.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
				}
			}
		}

		// Pass 3.5: Register Metadata (Monomorphized/Specialized)
		foreach (var instDecl in bindingContext.MonomorphizedFunctionDecls)
		{
			var baseMangledName = instDecl.Name.Split('<')[0];
			var originalUnit = (bindingContext.SymbolUnits.TryGetValue(baseMangledName, out var u) ? u : null) ?? units.First();
			bindingContext.CurrentUnit = originalUnit;
			bindingContext.CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;

			_functionReturnTypes[instDecl.Name] = bindingContext.ResolveType(instDecl.ReturnType)!;
			_functionParameterTypes[instDecl.Name] = instDecl.Parameters.Select(p => bindingContext.ResolveType(p.Type)!).ToList();
		}

		// Pass 4: Emit Externs
		foreach (var ext in _astExterns.Values) EmitExtern(ext);

		// Pass 5: Emit Bodies (Strictly Deduplicated)
		var emittedFunctionNames = new HashSet<string>();

		// A. Emit Regular Functions (No generics/specializations)
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
					var mangledName = bindingContext.GetMangledName(func.Name, ns);
					if (emittedFunctionNames.Add(mangledName))
					{
						var funcWriter = new StringWriter();
						EmitFunction(func, mangledName, funcWriter);
						_writer.Write(funcWriter.ToString());
					}
				}
			}
		}

		// B. Emit All Monomorphized and Explicit Specializations
		foreach (var instDecl in bindingContext.MonomorphizedFunctionDecls)
		{
			var canonicalName = bindingContext.NormalizeGenericName(instDecl.Name);
			if (emittedFunctionNames.Add(canonicalName))
			{
				var baseMangledName = instDecl.Name.Split('<')[0];
				var originalUnit = (bindingContext.SymbolUnits.TryGetValue(baseMangledName, out var u) ? u : null) ?? units.First();
				bindingContext.CurrentUnit = originalUnit;
				bindingContext.CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;
				_currentUnit = originalUnit;

				var funcWriter = new StringWriter();
				EmitFunction(instDecl, instDecl.Name, funcWriter);
				_writer.Write(funcWriter.ToString());
			}
		}

		if (_stringDefs.Count > 0)
		{
			_writer.WriteLine();
			foreach (var def in _stringDefs) _writer.WriteLine(def);
		}

		return _writer.ToString();
	}

	private void EmitExtern(ExternDeclarationSyntax ext)
	{
		var retSymbol = _bindingContext!.ResolveType(ext.ReturnType)!;
		var ret = Type(retSymbol);
		var parms = ext.Parameters.Select(p => Type(_bindingContext.ResolveType(p.Type)!)).ToList();
		if (ext.IsVariadic) parms.Add("...");
		_writer.WriteLine($"declare {ret} @{ext.Name}({string.Join(", ", parms)})");
		_writer.WriteLine();
	}

	private void EmitFunction(FunctionDeclarationSyntax func, string mangledName, StringWriter fw)
	{
		_labelCounter = 0;
		_localCounter = 0;
		_locals.Clear();

		var retSymbol = _bindingContext!.ResolveType(func.ReturnType)!;
		var ret = Type(retSymbol);

		var parmSymbols = func.Parameters.Select(p => _bindingContext.ResolveType(p.Type)!).ToList();
		var parmTypes = parmSymbols.Select(Type).ToList();
		var parmStrings = parmTypes.Zip(func.Parameters, (ty, p) => $"{ty} %{p.Name}");

		// Ensure any namespaced or capitalized entry point is generated globally as lowercase '@main'
		var emitName = mangledName == "Main" || mangledName == "main" || mangledName.EndsWith(".Main") || mangledName.EndsWith(".main")
			? "main"
			: mangledName;

		// Escape the name with quotes if it is an instantiated generic function (e.g., Swap<int>)
		var escapedName = emitName.Contains('<') ? $"\"{emitName}\"" : emitName;

		fw.WriteLine($"define {ret} @{escapedName}({string.Join(", ", parmStrings)}) {{");
		fw.WriteLine("  entry:");

		for (var i = 0; i < func.Parameters.Count; i++)
		{
			var p = func.Parameters[i];
			var ptr = NewLocal();
			_locals[p.Name] = ptr;

			var paramTypeSymbol = parmSymbols[i];
			_variableTypes[p.Name] = paramTypeSymbol;

			var llvmTy = parmTypes[i];
			fw.WriteLine($"    %{ptr} = alloca {llvmTy}");
			fw.WriteLine($"    store {llvmTy} %{p.Name}, ptr %{ptr}");
		}

		EmitBlock(func.Body, fw);

		if (func.ReturnType is "void" && !EndsWithReturn(func.Body))
			fw.WriteLine("    ret void");

		fw.WriteLine("}");
		fw.WriteLine();
	}

	private void EmitBlock(BlockStatementSyntax block, StringWriter fw)
	{
		var blockVars = new List<string>();

		foreach (var stmt in block.Statements)
		{
			if (stmt is VariableDeclarationSyntax v) blockVars.Add(v.Name);
			EmitStmt(stmt, fw);
		}

		if (!EndsWithReturn(block))
		{
			EmitCleanup(blockVars, fw);
		}
	}

	private HashSet<string> _heapAllocatedVars = new();

	private void EmitStmt(SyntaxNode stmt, StringWriter fw)
	{
		switch (stmt)
		{
			case BlockStatementSyntax b: EmitBlock(b, fw); break;
			case ReturnStatementSyntax r: EmitReturn(r, fw); break;
			case ExpressionStatementSyntax e: EmitExprStmt(e, fw); break;
			case VariableDeclarationSyntax v: EmitVar(v, fw); break;
			case IfStatementSyntax i: EmitIf(i, fw); break;
			case WhileStatementSyntax w: EmitWhile(w, fw); break;
			case ForStatementSyntax f: EmitFor(f, fw); break;
		}
	}

	private void EmitReturn(ReturnStatementSyntax r, StringWriter fw)
	{
		EmitCleanup(_locals.Keys.ToList(), fw);

		if (r.Expression is null)
		{
			fw.WriteLine("    ret void");
		}
		else
		{
			var (v, ty) = Eval(r.Expression, fw);

			if (v.StartsWith("ptr ") && ty is StructTypeSymbol structType)
			{
				var valReg = NewLocal();
				fw.WriteLine($"    %{valReg} = load %struct.{structType.Name}, ptr {v.Split(' ')[^1]}");
				fw.WriteLine($"    ret %struct.{structType.Name} %{valReg}");
			}
			else
			{
				fw.WriteLine($"    ret {v}");
			}
		}
	}

	private void EmitExprStmt(ExpressionStatementSyntax es, StringWriter fw)
	{
		switch (es.Expression)
		{
			case CallExpressionSyntax call: EmitCall(call, fw); break;
			case BinaryExpressionSyntax { Operator: "=" } assign: EmitStore(assign, fw); break;
			default:
				Eval(es.Expression, fw);
				break;
		}
	}

	private void EmitVar(VariableDeclarationSyntax v, StringWriter fw)
	{
		TypeSymbol? typeSymbol = null;
		if (v.Type is not null)
		{
			typeSymbol = _bindingContext!.ResolveType(v.Type);
		}

		if (v.Type == "refvar" || v.Type == "ref")
		{
			var (val, valTy) = Eval(v.Initializer!, fw);

			// Type-safe unwrap using C# pattern matching
			var innerType = valTy is PointerTypeSymbol ptrType ? ptrType.ReferencedType : valTy;
			var isMutable = v.Type == "refvar";

			var pointerType = new PointerTypeSymbol(innerType, isMutable);

			var ptr = NewLocal();
			_locals[v.Name] = ptr;
			_variableTypes[v.Name] = pointerType;

			fw.WriteLine($"    %{ptr} = alloca ptr");
			fw.WriteLine($"    store {val}, ptr %{ptr}");
			return;
		}

		// Handle Heap Allocations (RAII Owners) - Evaluate initializer FIRST, then allocate ptr
		if (v.Initializer is HeapAllocationExpressionSyntax heapInit)
		{
			var (val, valTy) = Eval(heapInit, fw);

			var ptr = NewLocal(); // Allocated sequentially AFTER initializer evaluation
			_locals[v.Name] = ptr;

			typeSymbol ??= valTy;
			_variableTypes[v.Name] = typeSymbol;
			_heapAllocatedVars.Add(v.Name);

			fw.WriteLine($"    %{ptr} = alloca ptr");
			fw.WriteLine($"    store {val}, ptr %{ptr}");
			return;
		}

		if (typeSymbol is not null)
		{
			// Case A: Explicitly typed variable (type is known upfront)
			var ptr = NewLocal();
			_locals[v.Name] = ptr;
			_variableTypes[v.Name] = typeSymbol;

			var ty = Type(typeSymbol);
			fw.WriteLine($"    %{ptr} = alloca {ty}");

			if (v.Initializer is not null)
			{
				if (v.Initializer is StructInitializationExpressionSyntax structInit)
				{
					EmitStructInitializationInPlace(structInit, ptr, fw);
				}
				else if (v.Initializer is ArrayInitializationExpressionSyntax arrInit)
				{
					EmitArrayInitializationInPlace(arrInit, ptr, typeSymbol as ArrayTypeSymbol, fw);
				}
				else
				{
					var (val, _) = Eval(v.Initializer, fw);
					fw.WriteLine($"    store {val}, ptr %{ptr}");
				}
			}
		}
		else
		{
			// Case B: Type Inference
			if (v.Initializer is null)
				throw new InvalidOperationException($"Type inference requires an initializer for variable '{v.Name}'");

			var (val, valTy) = Eval(v.Initializer, fw);

			var ptr = NewLocal();
			_locals[v.Name] = ptr;
			_variableTypes[v.Name] = valTy;

			if (val.StartsWith("ptr "))
			{
				var valReg = val.Split(' ')[^1].TrimStart('%');
				_locals[v.Name] = valReg;
			}
			else
			{
				var ty = Type(valTy);
				fw.WriteLine($"    %{ptr} = alloca {ty}");
				fw.WriteLine($"    store {val}, ptr %{ptr}");
			}
		}
	}

	private void EmitIf(IfStatementSyntax node, StringWriter fw)
	{
		var (c, _) = Eval(node.Condition, fw);
		var t = NextLabel();
		var e = NextLabel();
		var d = NextLabel();
		fw.WriteLine($"    br {c}, label %{t}, label %{e}");
		fw.WriteLine($"  {t}:");
		EmitStmt(node.ThenStatement, fw);
		if (!EndsWithReturn(node.ThenStatement))
			fw.WriteLine($"    br label %{d}");
		fw.WriteLine($"  {e}:");
		if (node.ElseClause is not null)
		{
			EmitStmt(node.ElseClause.Body, fw);
			if (!EndsWithReturn(node.ElseClause.Body))
				fw.WriteLine($"    br label %{d}");
		}
		else
		{
			fw.WriteLine($"    br label %{d}");
		}

		fw.WriteLine($"  {d}:");
	}

	private void EmitWhile(WhileStatementSyntax node, StringWriter fw)
	{
		var c = NextLabel();
		var b = NextLabel();
		var d = NextLabel();
		fw.WriteLine($"    br label %{c}");
		fw.WriteLine($"  {c}:");
		var (cond, _) = Eval(node.Condition, fw);
		fw.WriteLine($"    br {cond}, label %{b}, label %{d}");
		fw.WriteLine($"  {b}:");
		EmitStmt(node.Body, fw);
		fw.WriteLine($"    br label %{c}");
		fw.WriteLine($"  {d}:");
	}

	private void EmitFor(ForStatementSyntax node, StringWriter fw)
	{
		EmitVar(node.Initializer, fw);
		var c = NextLabel();
		var b = NextLabel();
		var i = NextLabel();
		var d = NextLabel();
		fw.WriteLine($"    br label %{c}");
		fw.WriteLine($"  {c}:");
		var (cond, _) = Eval(node.Condition, fw);
		fw.WriteLine($"    br {cond}, label %{b}, label %{d}");
		fw.WriteLine($"  {b}:");
		EmitStmt(node.Body, fw);
		fw.WriteLine($"    br label %{i}");
		fw.WriteLine($"  {i}:");
		Eval(node.Increment, fw);
		fw.WriteLine($"    br label %{c}");
		fw.WriteLine($"  {d}:");
	}

	private (string val, TypeSymbol ty) Eval(ExpressionSyntax expr, StringWriter fw)
	{
		return expr switch
		{
			IntegerLiteralExpressionSyntax n => ($"i32 {n.Value}", TypeSymbol.Int),
			DoubleLiteralExpressionSyntax d => (FormatDouble(d.Value), TypeSymbol.Double),
			BooleanLiteralExpressionSyntax b => ($"i1 {(b.Value ? "1" : "0")}", TypeSymbol.Bool),
			StringLiteralExpressionSyntax s => (AddString(s.Value), TypeSymbol.String),
			CharacterLiteralExpressionSyntax c => ($"i8 {(int)c.Value}", TypeSymbol.Char),
			IdentifierExpressionSyntax id => Load(id.Name, fw),
			MemberAccessExpressionSyntax m => EmitMemberAccess(m, fw),
			BorrowExpressionSyntax b => EmitBorrowExpression(b, fw),
			StructInitializationExpressionSyntax s => EmitStructInitialization(s, fw),
			HeapAllocationExpressionSyntax h => EmitHeapAllocation(h, fw),
			IndexExpressionSyntax idx => EmitIndexExpression(idx, fw),
			CallExpressionSyntax call => EmitCallExpr(call, fw),
			BinaryExpressionSyntax { Operator: "=" } assign => EmitLoadStore(assign, fw),
			BinaryExpressionSyntax bin => EmitBin(bin, fw),
			UnaryExpressionSyntax u => EmitUnary(u, fw),
			TernaryExpressionSyntax t => EmitTernaryExpression(t, fw),
			ArrayInitializationExpressionSyntax a => EmitArrayInitialization(a, fw),
			_ => throw new InvalidOperationException($"Unknown expr: {expr.GetType()}"),
		};
	}

	private void EmitCall(CallExpressionSyntax call, StringWriter fw)
	{
		var mangledName = ResolveFunctionName(call.FunctionName, _currentUnit!);
		if (call.TypeArguments.Count > 0)
		{
			mangledName = $"{mangledName}<{string.Join(", ", call.TypeArguments)}>";
		}
		List<string> args = [];

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var (val, valTy) = Eval(call.Arguments[i], fw);
			var paramTy = GetParamType(mangledName, i);
			if (paramTy is SliceTypeSymbol && valTy is ArrayTypeSymbol)
			{
				if (call.Arguments[i] is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
				{
					val = CoerceArrayToSlice($"ptr %{ptr}", valTy as ArrayTypeSymbol, paramTy as SliceTypeSymbol, fw);
				}
				else
				{
					val = CoerceArrayToSlice(val, valTy as ArrayTypeSymbol, paramTy as SliceTypeSymbol, fw);
				}
			}

			// PROMOTION FOR VARIADIC ARGUMENTS
			var isVariadic = _astExterns.TryGetValue(mangledName, out var ext) && ext.IsVariadic;
			if (isVariadic && i >= _functionParameterTypes[mangledName].Count)
			{
				if (valTy.Equals(TypeSymbol.Bool))
				{
					var promReg = NewLocal();
					fw.WriteLine($"    %{promReg} = zext i1 {V(val)} to i32");
					val = $"i32 %{promReg}";
				}
				else if (valTy.Equals(TypeSymbol.Char))
				{
					var promReg = NewLocal();
					fw.WriteLine($"    %{promReg} = zext i8 {V(val)} to i32");
					val = $"i32 %{promReg}";
				}
			}

			args.Add(val);
		}

		var isCallVariadic = _astExterns.TryGetValue(mangledName, out var externDecl) && externDecl.IsVariadic;
		var retType = _functionReturnTypes.TryGetValue(mangledName, out var foundRet) ? foundRet : TypeSymbol.Void;
		var ret = Type(retType);

		var escapedName = mangledName.Contains('<') ? $"\"{mangledName}\"" : mangledName;
		var callee = $"@{escapedName}";
		if (isCallVariadic)
		{
			var paramTys = string.Join(", ", _functionParameterTypes[mangledName].Select(Type));
			callee = $"({paramTys}, ...) @{escapedName}";
		}

		if (ret == "void")
		{
			fw.WriteLine($"    call {ret} {callee}({string.Join(", ", args)})");
		}
		else
		{
			var r = NewLocal();
			fw.WriteLine($"    %{r} = call {ret} {callee}({string.Join(", ", args)})");
		}
	}

	private (string val, TypeSymbol ty) EmitCallExpr(CallExpressionSyntax call, StringWriter fw)
	{
		var mangledName = ResolveFunctionName(call.FunctionName, _currentUnit!);
		if (call.TypeArguments.Count > 0)
		{
			mangledName = $"{mangledName}<{string.Join(", ", call.TypeArguments)}>";
		}
		List<string> args = [];

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var (val, valTy) = Eval(call.Arguments[i], fw);
			var paramTy = GetParamType(mangledName, i);
			if (paramTy is SliceTypeSymbol && valTy is ArrayTypeSymbol)
			{
				if (call.Arguments[i] is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
				{
					val = CoerceArrayToSlice($"ptr %{ptr}", valTy as ArrayTypeSymbol, paramTy as SliceTypeSymbol, fw);
				}
				else
				{
					val = CoerceArrayToSlice(val, valTy as ArrayTypeSymbol, paramTy as SliceTypeSymbol, fw);
				}
			}

			// PROMOTION FOR VARIADIC ARGUMENTS (C-ABI Promotion Rules)
			var isVariadic = _astExterns.TryGetValue(mangledName, out var ext) && ext.IsVariadic;
			if (isVariadic && i >= _functionParameterTypes[mangledName].Count)
			{
				if (valTy.Equals(TypeSymbol.Bool))
				{
					var promReg = NewLocal();
					fw.WriteLine($"    %{promReg} = zext i1 {V(val)} to i32");
					val = $"i32 %{promReg}";
				}
				else if (valTy.Equals(TypeSymbol.Char))
				{
					var promReg = NewLocal();
					fw.WriteLine($"    %{promReg} = zext i8 {V(val)} to i32");
					val = $"i32 %{promReg}";
				}
			}

			args.Add(val);
		}

		var isCallVariadic = _astExterns.TryGetValue(mangledName, out var externDecl) && externDecl.IsVariadic;
		var retTypeSymbol = _functionReturnTypes.TryGetValue(mangledName, out var ret) ? ret : TypeSymbol.Int;
		var ty = Type(retTypeSymbol);

		var escapedName = mangledName.Contains('<') ? $"\"{mangledName}\"" : mangledName;
		var callee = $"@{escapedName}";
		if (isCallVariadic)
		{
			var paramTys = string.Join(", ", _functionParameterTypes[mangledName].Select(Type));
			callee = $"({paramTys}, ...) @{escapedName}";
		}

		var r = NewLocal();
		fw.WriteLine($"    %{r} = call {ty} {callee}({string.Join(", ", args)})");
		return ($"{ty} %{r}", retTypeSymbol); // <-- Fixed missing return here!
	}

	private (string val, TypeSymbol ty) EmitBin(BinaryExpressionSyntax bin, StringWriter fw)
	{
		var (l, lTy) = Eval(bin.Left, fw);
		var (r, rTy) = Eval(bin.Right, fw);

		var isDouble = lTy.Equals(TypeSymbol.Double) || rTy.Equals(TypeSymbol.Double);

		// 1. Implicit promotion: Coerce int to double if one of the operands is double
		if (isDouble)
		{
			if (lTy.Equals(TypeSymbol.Int))
			{
				var promReg = NewLocal();
				fw.WriteLine($"    %{promReg} = sitofp i32 {V(l)} to double");
				l = $"double %{promReg}";
			}
			if (rTy.Equals(TypeSymbol.Int))
			{
				var promReg = NewLocal();
				fw.WriteLine($"    %{promReg} = sitofp i32 {V(r)} to double");
				r = $"double %{promReg}";
			}
		}

		// 2. Allocate register AFTER promotion to maintain sequential LLVM register order
		var reg = NewLocal();

		string op;
		TypeSymbol resultTy;

		if (isDouble)
		{
			// Floating Point Logic
			(op, resultTy) = bin.Operator switch
			{
				"+" => ($"fadd double {V(l)}, {V(r)}", TypeSymbol.Double),
				"-" => ($"fsub double {V(l)}, {V(r)}", TypeSymbol.Double),
				"*" => ($"fmul double {V(l)}, {V(r)}", TypeSymbol.Double),
				"/" => ($"fdiv double {V(l)}, {V(r)}", TypeSymbol.Double),
				"%" => ($"frem double {V(l)}, {V(r)}", TypeSymbol.Double),
				"==" => ($"fcmp oeq double {V(l)}, {V(r)}", TypeSymbol.Bool),
				"!=" => ($"fcmp one double {V(l)}, {V(r)}", TypeSymbol.Bool),
				"<" => ($"fcmp olt double {V(l)}, {V(r)}", TypeSymbol.Bool),
				">" => ($"fcmp ogt double {V(l)}, {V(r)}", TypeSymbol.Bool),
				"<=" => ($"fcmp ole double {V(l)}, {V(r)}", TypeSymbol.Bool),
				">=" => ($"fcmp oge double {V(l)}, {V(r)}", TypeSymbol.Bool),
				_ => throw new InvalidOperationException($"Unknown double binop '{bin.Operator}'")
			};
		}
		else
		{
			// Integer / Pointer / Boolean Logic
			var isPointer = lTy.Equals(TypeSymbol.String) || lTy is PointerTypeSymbol || rTy.Equals(TypeSymbol.String) || rTy is PointerTypeSymbol;
			var isBool = lTy.Equals(TypeSymbol.Bool) || rTy.Equals(TypeSymbol.Bool);

			// Select LLVM type: ptr for refs, i1 for booleans, i32 for integers
			var tyStr = isPointer ? "ptr" : (isBool ? "i1" : "i32");

			(op, resultTy) = bin.Operator switch
			{
				"+" => ($"add i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"-" => ($"sub i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"*" => ($"mul i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"/" => ($"sdiv i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"%" => ($"srem i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"==" => ($"icmp eq {tyStr} {V(l)}, {V(r)}", TypeSymbol.Bool),
				"!=" => ($"icmp ne {tyStr} {V(l)}, {V(r)}", TypeSymbol.Bool),
				"<" => ($"icmp slt {tyStr} {V(l)}, {V(r)}", TypeSymbol.Bool),
				">" => ($"icmp sgt {tyStr} {V(l)}, {V(r)}", TypeSymbol.Bool),
				"<=" => ($"icmp sle {tyStr} {V(l)}, {V(r)}", TypeSymbol.Bool),
				">=" => ($"icmp sge {tyStr} {V(l)}, {V(r)}", TypeSymbol.Bool),
				"&" => ($"and i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"|" => ($"or i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"^" => ($"xor i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				"&&" => ($"and i1 {V(l)}, {V(r)}", TypeSymbol.Bool),
				"||" => ($"or i1 {V(l)}, {V(r)}", TypeSymbol.Bool),
				"<<" => ($"shl i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				">>" => ($"ashr i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				">>>" => ($"lshr i32 {V(l)}, {V(r)}", TypeSymbol.Int),
				_ => throw new InvalidOperationException($"Unknown binop '{bin.Operator}'"),
			};
		}

		fw.WriteLine($"    %{reg} = {op}");
		return ($"{Type(resultTy)} %{reg}", resultTy);
	}

	private (string val, TypeSymbol ty) EmitUnary(UnaryExpressionSyntax u, StringWriter fw)
	{
		if (u.Operator.EndsWith("_postfix") || u.Operator.EndsWith("_prefix"))
		{
			return EmitIncrementDecrement(u, u.Operator.EndsWith("_prefix"), u.Operator.StartsWith("++"), fw);
		}

		var (o, oTy) = Eval(u.Operand, fw);
		var r = NewLocal();
		var (op, resultTy) = u.Operator switch
		{
			"-" => ($"sub i32 0, {V(o)}", TypeSymbol.Int),
			"!" => ($"xor i1 1, {V(o)}", TypeSymbol.Bool),
			"~" => ($"xor i32 {V(o)}, -1", TypeSymbol.Int),
			_ => throw new InvalidOperationException($"Unknown unary op '{u.Operator}'"),
		};
		fw.WriteLine($"    %{r} = {op}");
		return ($"{Type(resultTy)} %{r}", resultTy);
	}

	private void EmitStore(BinaryExpressionSyntax assign, StringWriter fw)
	{
		var (r, rTy) = Eval(assign.Right, fw);
		var llvmTy = Type(rTy);

		if (r.StartsWith("ptr ") && rTy is StructTypeSymbol)
		{
			var structVal = NewLocal();
			fw.WriteLine($"    %{structVal} = load {llvmTy}, ptr {V(r)}");
			r = $"{llvmTy} %{structVal}";
		}

		if (assign.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var typeName = _variableTypes[id.Name];

			// If the target variable itself is a pointer (reference), load the address first and store into it
			if (typeName is PointerTypeSymbol)
			{
				var actualPtr = NewLocal();
				fw.WriteLine($"    %{actualPtr} = load ptr, ptr %{ptr}");
				fw.WriteLine($"    store {llvmTy} {V(r)}, ptr %{actualPtr}");
			}
			else
			{
				fw.WriteLine($"    store {llvmTy} {V(r)}, ptr %{ptr}");
			}
		}
		else if (assign.Left is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, _) = GetFieldPointer(m, fw);
			fw.WriteLine($"    store {llvmTy} {V(r)}, ptr %{fieldPtr}");
		}
		else if (assign.Left is IndexExpressionSyntax idx)
		{
			var (elementPtr, _) = GetFieldPointer(idx, fw);
			fw.WriteLine($"    store {llvmTy} {V(r)}, ptr %{elementPtr}");
		}
	}

	private (string val, TypeSymbol ty) EmitLoadStore(BinaryExpressionSyntax assign, StringWriter fw)
	{
		if (assign.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var (r, rTy) = Eval(assign.Right, fw);
			fw.WriteLine($"    store {r}, ptr %{ptr}");
			return (r, rTy);
		}

		throw new InvalidOperationException($"Cannot assign to non-variable");
	}

	private (string val, TypeSymbol ty) Load(string name, StringWriter fw)
	{
		if (!_locals.TryGetValue(name, out var ptr))
			throw new InvalidOperationException($"Undefined variable '{name}'");

		var type = _variableTypes[name];
		var ty = Type(type);

		var reg = NewLocal();
		fw.WriteLine($"    %{reg} = load {ty}, ptr %{ptr}");

		if (type is PointerTypeSymbol ptrType)
		{
			var resolvedType = ptrType.ReferencedType;
			if (resolvedType == TypeSymbol.Int || resolvedType == TypeSymbol.Double || resolvedType == TypeSymbol.Bool || resolvedType == TypeSymbol.Char)
			{
				var valReg = NewLocal();
				var innerTy = Type(resolvedType);
				fw.WriteLine($"    %{valReg} = load {innerTy}, ptr %{reg}");
				return ($"{innerTy} %{valReg}", resolvedType);
			}
		}

		return ($"{ty} %{reg}", type);
	}

	private string AddString(string value)
	{
		var idx = _stringIndex++;

		// 1. Encode the .NET string (UTF-16) into UTF-8 bytes for LLVM i8 arrays
		var utf8Bytes = System.Text.Encoding.UTF8.GetBytes(value);

		var sb = new System.Text.StringBuilder();
		foreach (var b in utf8Bytes)
		{
			// 2. Keep standard printable ASCII as characters, escape everything else as \xx
			if (b >= 32 && b <= 126 && b != (byte)'"' && b != (byte)'\\')
			{
				sb.Append((char)b);
			}
			else
			{
				sb.Append($"\\{b:x2}");
			}
		}

		var escaped = sb.ToString();

		// 3. The array size must be the number of BYTES + 1 for null terminator
		_stringDefs.Add($"@str{idx} = private unnamed_addr constant [{utf8Bytes.Length + 1} x i8] c\"{escaped}\\00\"");

		return $"ptr @str{idx}";
	}

	private string NextLabel() => $"L{_labelCounter++}";
	private string NewLocal() => (_localCounter++).ToString();

	private string Type(TypeSymbol t)
	{
		if (t is null) return "i32";
		if (t is PointerTypeSymbol) return "ptr";
		if (t is SliceTypeSymbol) return "{ ptr, i32 }";

		if (t is ArrayTypeSymbol arr)
			return $"[{arr.Size} x {Type(arr.ElementType)}]";

		if (t is StructTypeSymbol)
		{
			var name = t.Name;
			if (name.Contains('<'))
				return $"%\"struct.{name}\"";
			return $"%struct.{name}";
		}

		var primitive = t.Name switch
		{
			"void" => "void",
			"int" => "i32",
			"double" => "double",
			"bool" => "i1",
			"string" or "ptr" => "ptr",
			"char" => "i8",
			_ => null
		};
		return primitive ?? $"%struct.{t.Name}";
	}

	private static string V(string typed) => typed.Split(' ')[^1];
	private static bool EndsWithReturn(SyntaxNode s) => s switch
	{
		BlockStatementSyntax b => b.Statements.Count > 0 && b.Statements[^1] is ReturnStatementSyntax,
		ReturnStatementSyntax => true,
		_ => false,
	};

	private (string val, TypeSymbol ty) EmitMemberAccess(MemberAccessExpressionSyntax m, StringWriter fw)
	{
		var (fieldPtr, fieldType) = GetFieldPointer(m, fw);
		var loadedReg = NewLocal();
		var fTy = Type(fieldType);
		fw.WriteLine($"    %{loadedReg} = load {fTy}, ptr %{fieldPtr}");
		return ($"{fTy} %{loadedReg}", fieldType);
	}

	private (string ptr, TypeSymbol type) GetFieldPointer(ExpressionSyntax expr, StringWriter fw)
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
				var actualPtr = NewLocal();
				fw.WriteLine($"    %{actualPtr} = load ptr, ptr %{structPtr}");
				var innerType = type is PointerTypeSymbol ptrType ? ptrType.ReferencedType : type;
				return (actualPtr, innerType);
			}

			return (structPtr, type);
		}
		else if (expr is MemberAccessExpressionSyntax m)
		{
			var (parentPtr, parentType) = GetFieldPointer(m.Expression, fw);

			if (parentType is SliceTypeSymbol sliceType && m.MemberName == "Length")
			{
				var lengthPtrReg = NewLocal();
				fw.WriteLine($"    %{lengthPtrReg} = getelementptr inbounds {{ ptr, i32 }}, ptr %{parentPtr}, i32 0, i32 1");
				return (lengthPtrReg, TypeSymbol.Int);
			}

			if (parentType is not StructTypeSymbol structType)
				throw new InvalidOperationException($"Cannot access field of non-struct type '{parentType?.Name ?? "null"}'");

			var fieldIndex = -1;
			TypeSymbol? fieldType = TypeSymbol.Int;

			// Read fields directly from the TypeSymbol object instead of the AST!
			for (var i = 0; i < structType.Fields.Count; i++)
			{
				if (structType.Fields[i].Name == m.MemberName)
				{
					fieldIndex = i;
					fieldType = structType.Fields[i].Type;
					break;
				}
			}

			var fieldPtrReg = NewLocal();
			var structTy = parentType.Name.Contains('<') ? $"%\"struct.{parentType.Name}\"" : $"%struct.{parentType.Name}";
			fw.WriteLine($"    %{fieldPtrReg} = getelementptr inbounds {structTy}, ptr %{parentPtr}, i32 0, i32 {fieldIndex}");

			return (fieldPtrReg, fieldType!);
		}
		else if (expr is IndexExpressionSyntax idx)
		{
			var (parentPtr, parentType) = GetFieldPointer(idx.Left, fw);
			var (indexVal, _) = Eval(idx.Index, fw);

			if (parentType is SliceTypeSymbol sliceType)
			{
				var arrPtrReg = NewLocal();
				fw.WriteLine($"    %{arrPtrReg} = getelementptr inbounds {{ ptr, i32 }}, ptr %{parentPtr}, i32 0, i32 0");
				var arrayPtr = NewLocal();
				fw.WriteLine($"    %{arrayPtr} = load ptr, ptr %{arrPtrReg}");

				var lenPtrReg = NewLocal();
				fw.WriteLine($"    %{lenPtrReg} = getelementptr inbounds {{ ptr, i32 }}, ptr %{parentPtr}, i32 0, i32 1");
				var lengthReg = NewLocal();
				fw.WriteLine($"    %{lengthReg} = load i32, ptr %{lenPtrReg}");

				var isSafeLabel = NextLabel();
				var panicLabel = NextLabel();
				var checkReg = NewLocal();

				fw.WriteLine($"    %{checkReg} = icmp ult i32 {V(indexVal)}, %{lengthReg}");
				fw.WriteLine($"    br i1 %{checkReg}, label %{isSafeLabel}, label %{panicLabel}");

				EmitRuntimeError(idx, panicLabel, fw);

				fw.WriteLine($"  {isSafeLabel}:");
				var elementPtrReg = NewLocal();
				var elementLlvmTy = Type(sliceType.ElementType);
				fw.WriteLine($"    %{elementPtrReg} = getelementptr inbounds {elementLlvmTy}, ptr %{arrayPtr}, i32 {V(indexVal)}");
				return (elementPtrReg, sliceType.ElementType);
			}
			else if (parentType is ArrayTypeSymbol arrayType)
			{
				var isArraySafeLabel = NextLabel();
				var arrayPanicLabel = NextLabel();

				var arrayCheckReg = NewLocal();
				fw.WriteLine($"    %{arrayCheckReg} = icmp ult i32 {V(indexVal)}, {arrayType.Size}");
				fw.WriteLine($"    br i1 %{arrayCheckReg}, label %{isArraySafeLabel}, label %{arrayPanicLabel}");

				EmitRuntimeError(idx, arrayPanicLabel, fw);

				fw.WriteLine($"  {isArraySafeLabel}:");
				var arrayElementPtrReg = NewLocal();
				var llvmArrTy = Type(arrayType);
				fw.WriteLine($"    %{arrayElementPtrReg} = getelementptr inbounds {llvmArrTy}, ptr %{parentPtr}, i32 0, i32 {V(indexVal)}");

				return (arrayElementPtrReg, arrayType.ElementType);
			}
		}

		throw new InvalidOperationException("Unsupported field pointer expression");
	}

	private (string val, TypeSymbol ty) EmitStructInitialization(StructInitializationExpressionSyntax expr, StringWriter fw)
	{
		var type = _bindingContext!.ResolveType(expr.StructTypeName) as StructTypeSymbol;
		var tempPtrReg = NewLocal();
		var structTy = type!.Name.Contains('<') ? $"%\"struct.{type.Name}\"" : $"%struct.{type.Name}";
		fw.WriteLine($"    %{tempPtrReg} = alloca {structTy}");
		EmitStructInitializationInPlace(expr, tempPtrReg, fw);
		return ($"ptr %{tempPtrReg}", type);
	}

	private void EmitStructInitializationInPlace(StructInitializationExpressionSyntax expr, string destPtr, StringWriter fw)
	{
		var mangledName = _bindingContext!.ResolveType(expr.StructTypeName) as StructTypeSymbol;
		var structTy = mangledName!.Name.Contains('<') ? $"%\"struct.{mangledName.Name}\"" : $"%struct.{mangledName.Name}";
		foreach (var init in expr.Initializers)
		{
			var fieldIndex = -1;
			var fieldType = "int";

			// Read fields directly from the TypeSymbol object instead of the AST!
			for (var i = 0; i < mangledName.Fields.Count; i++)
			{
				if (mangledName.Fields[i].Name == init.MemberName)
				{
					fieldIndex = i;
					fieldType = mangledName.Fields[i].Type.Name;
					break;
				}
			}

			var fieldPtrReg = NewLocal();
			fw.WriteLine($"    %{fieldPtrReg} = getelementptr inbounds {structTy}, ptr %{destPtr}, i32 0, i32 {fieldIndex}");

			if (init.Expression is StructInitializationExpressionSyntax nestedInit)
			{
				EmitStructInitializationInPlace(nestedInit, fieldPtrReg, fw);
			}
			else
			{
				var (val, _) = Eval(init.Expression, fw);
				fw.WriteLine($"    store {val}, ptr %{fieldPtrReg}");
			}
		}
	}


	private (string val, TypeSymbol ty) EmitBorrowExpression(BorrowExpressionSyntax expr, StringWriter fw)
	{
		if (expr.Expression is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var type = _variableTypes[id.Name];

			var isReference = type is PointerTypeSymbol;
			var isHeap = _heapAllocatedVars.Contains(id.Name);

			if (isReference || isHeap)
			{
				var actualPtr = NewLocal();
				fw.WriteLine($"    %{actualPtr} = load ptr, ptr %{ptr}");
				return ($"ptr %{actualPtr}", type);
			}

			var ptrType = _bindingContext!.ResolveType($"refvar {type.Name}")!;
			return ($"ptr %{ptr}", ptrType);
		}
		else if (expr.Expression is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, fieldType) = GetFieldPointer(m, fw);
			var ptrType = _bindingContext!.ResolveType($"refvar {fieldType.Name}")!;
			return ($"ptr %{fieldPtr}", ptrType);
		}
		else if (expr.Expression is IndexExpressionSyntax idx)
		{
			var (elementPtr, elementType) = GetFieldPointer(idx, fw);
			var ptrType = _bindingContext!.ResolveType($"refvar {elementType.Name}")!;
			return ($"ptr %{elementPtr}", ptrType);
		}

		throw new InvalidOperationException("Can only borrow variables or member fields");
	}

	private (string val, TypeSymbol ty) EmitIncrementDecrement(UnaryExpressionSyntax u, bool isPrefix, bool isIncrement, StringWriter fw)
	{
		string ptrReg;
		TypeSymbol type;

		if (u.Operand is IdentifierExpressionSyntax id)
		{
			if (!_locals.TryGetValue(id.Name, out var structPtr))
				throw new InvalidOperationException($"Undefined variable '{id.Name}'");
			ptrReg = structPtr;
			type = _variableTypes[id.Name];
		}
		else if (u.Operand is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, fieldType) = GetFieldPointer(m, fw);
			ptrReg = fieldPtr;
			type = fieldType;
		}
		else
		{
			throw new InvalidOperationException("Increment/decrement operand must be a variable or struct field");
		}

		var ty = Type(type);

		// 1. Load current value
		var currentValReg = NewLocal();
		fw.WriteLine($"    %{currentValReg} = load {ty}, ptr %{ptrReg}");

		// 2. Compute new value (currentVal +/- 1)
		var newValReg = NewLocal();
		var op = isIncrement ? "add" : "sub";
		fw.WriteLine($"    %{newValReg} = {op} {ty} %{currentValReg}, 1");

		// 3. Store new value back
		fw.WriteLine($"    store {ty} %{newValReg}, ptr %{ptrReg}");

		// 4. Return correct value based on Prefix vs Postfix rules
		if (isPrefix)
		{
			return ($"{ty} %{newValReg}", type); // Prefix returns the NEW value
		}
		else
		{
			return ($"{ty} %{currentValReg}", type); // Postfix returns the OLD value
		}
	}

	private (string val, TypeSymbol ty) EmitHeapAllocation(HeapAllocationExpressionSyntax expr, StringWriter fw)
	{
		if (expr.Expression is not StructInitializationExpressionSyntax s)
			throw new InvalidOperationException("Heap allocation currently only supported for structs");

		var type = _bindingContext!.ResolveType(s.StructTypeName) as StructTypeSymbol;

		// Calculate the size directly from the TypeSymbol fields!
		var size = type!.Fields.Count * 8;

		var ptrReg = NewLocal();
		fw.WriteLine($"    %{ptrReg} = call ptr @malloc(i64 {size})");

		EmitStructInitializationInPlace(s, ptrReg, fw);

		return ($"ptr %{ptrReg}", type);
	}

	private (string val, TypeSymbol ty) EmitIndexExpression(IndexExpressionSyntax idx, StringWriter fw)
	{
		// 1. Get the pointer to the specific array element
		var (elementPtr, elementType) = GetFieldPointer(idx, fw);

		// 2. Load the value from that pointer
		var loadedReg = NewLocal();
		var fTy = Type(elementType);
		fw.WriteLine($"    %{loadedReg} = load {fTy}, ptr %{elementPtr}");

		return ($"{fTy} %{loadedReg}", elementType);
	}

	private void EmitCleanup(IEnumerable<string> variableNames, StringWriter fw)
	{
		foreach (var name in variableNames)
		{
			if (_heapAllocatedVars.Contains(name))
			{
				var ptr = _locals[name];
				var actualPtr = NewLocal();
				fw.WriteLine($"    %{actualPtr} = load ptr, ptr %{ptr}");
				fw.WriteLine($"    call void @free(ptr %{actualPtr})");
			}
		}
	}

	private void EmitRuntimeError(IndexExpressionSyntax idx, string errorLabel, StringWriter fw)
	{
		fw.WriteLine($"  {errorLabel}:");

		var errorLines = _context!.FormatDiagnostic("Runtime Error", "Index was outside the bounds of the array.", idx.Span, true);

		foreach (var line in errorLines)
		{
			var strPtr = AddString(line); // Just add the raw string!
			var reg = NewLocal();
			fw.WriteLine($"    %{reg} = call i32 @puts(ptr {strPtr.Split(' ')[^1]})");
		}

		fw.WriteLine($"    call void @exit(i32 1)");
		fw.WriteLine($"    unreachable");
	}

	private void EmitArrayInitializationInPlace(ArrayInitializationExpressionSyntax expr, string destPtr, ArrayTypeSymbol arrayType, StringWriter fw)
	{
		var llvmArrTy = Type(arrayType);

		for (int i = 0; i < expr.Elements.Count; i++)
		{
			var elementPtrReg = NewLocal();
			// Get pointer to arr[i]
			fw.WriteLine($"    %{elementPtrReg} = getelementptr inbounds {llvmArrTy}, ptr %{destPtr}, i32 0, i32 {i}");

			var element = expr.Elements[i];
			if (element is StructInitializationExpressionSyntax structInit)
			{
				// In-Place Initialization: Write values directly into the array element address
				EmitStructInitializationInPlace(structInit, elementPtrReg, fw);
			}
			else if (element is ArrayInitializationExpressionSyntax nestedArr)
			{
				EmitArrayInitializationInPlace(nestedArr, elementPtrReg, arrayType.ElementType as ArrayTypeSymbol, fw);
			}
			else
			{
				var (val, _) = Eval(element, fw);
				fw.WriteLine($"    store {val}, ptr %{elementPtrReg}");
			}
		}
	}

	private string CoerceArrayToSlice(string arrayVal, ArrayTypeSymbol arrayTy, SliceTypeSymbol sliceTy, StringWriter fw)
	{
		var slicePtr = NewLocal();
		fw.WriteLine($"    %{slicePtr} = alloca {{ ptr, i32 }}");

		// Store array pointer into index 0
		var ptrField = NewLocal();
		fw.WriteLine($"    %{ptrField} = getelementptr inbounds {{ ptr, i32 }}, ptr %{slicePtr}, i32 0, i32 0");
		fw.WriteLine($"    store {arrayVal}, ptr %{ptrField}");

		// Store size into index 1
		var sizeField = NewLocal();
		fw.WriteLine($"    %{sizeField} = getelementptr inbounds {{ ptr, i32 }}, ptr %{slicePtr}, i32 0, i32 1");
		fw.WriteLine($"    store i32 {arrayTy.Size}, ptr %{sizeField}");

		// LOAD the slice struct value so we pass it BY VALUE!
		var sliceVal = NewLocal();
		fw.WriteLine($"    %{sliceVal} = load {{ ptr, i32 }}, ptr %{slicePtr}");

		return $"{{ ptr, i32 }} %{sliceVal}";
	}

	private TypeSymbol GetParamType(string mangledFuncName, int index)
	{
		if (_functionParameterTypes.TryGetValue(mangledFuncName, out var paramTypes) && index < paramTypes.Count)
		{
			return paramTypes[index];
		}

		return TypeSymbol.Int;
	}

	private (string val, TypeSymbol ty) EmitTernaryExpression(TernaryExpressionSyntax expr, StringWriter fw)
	{
		var (cond, _) = Eval(expr.Condition, fw);

		var t = NextLabel();
		var e = NextLabel();
		var d = NextLabel();

		var (_, thenTy) = Eval(expr.ThenExpression, fw);
		var llvmTy = Type(thenTy);

		var resultPtr = NewLocal();
		fw.WriteLine($"    %{resultPtr} = alloca {llvmTy}");
		fw.WriteLine($"    br {cond}, label %{t}, label %{e}");

		fw.WriteLine($"  {t}:");
		var (thenVal, _) = Eval(expr.ThenExpression, fw);
		fw.WriteLine($"    store {thenVal}, ptr %{resultPtr}");
		fw.WriteLine($"    br label %{d}");

		fw.WriteLine($"  {e}:");
		var (elseVal, _) = Eval(expr.ElseExpression, fw);
		fw.WriteLine($"    store {elseVal}, ptr %{resultPtr}");
		fw.WriteLine($"    br label %{d}");

		fw.WriteLine($"  {d}:");
		var loadedReg = NewLocal();
		fw.WriteLine($"    %{loadedReg} = load {llvmTy}, ptr %{resultPtr}");

		return ($"{llvmTy} %{loadedReg}", thenTy);
	}

	private string ResolveFunctionName(string name, CompilationUnitSyntax activeUnit)
	{
		if (name == "main" || name == "Main") return "main";

		// 1. Is it already a full name, an extern, or a generic template?
		if (_astFunctions.ContainsKey(name) || _astExterns.ContainsKey(name) || _bindingContext!.GenericFunctionTemplates.ContainsKey(name))
			return name;

		// 2. Try the current namespace
		var ns = activeUnit.NamespaceDeclaration?.Name;
		var localMangled = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
		if (_astFunctions.ContainsKey(localMangled) || _bindingContext!.GenericFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		// 3. Try lookup across all active 'using' namespaces in this file
		var activeUsings = new List<string>(activeUnit.Usings.Select(u => u.NamespaceName));
		if (activeUnit.NamespaceDeclaration is not null)
			activeUsings.AddRange(activeUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

		foreach (var importNs in activeUsings)
		{
			var candidateMangled = $"{importNs}.{name}";
			if (_astFunctions.ContainsKey(candidateMangled) || _bindingContext!.GenericFunctionTemplates.ContainsKey(candidateMangled))
				return candidateMangled;
		}

		return name;
	}

	private (string val, TypeSymbol ty) EmitArrayInitialization(ArrayInitializationExpressionSyntax expr, StringWriter fw)
	{
		var elementType = expr.Elements.Count > 0 ? GetExprType(expr.Elements[0]) : TypeSymbol.Int;
		var arrayType = new ArrayTypeSymbol(elementType, expr.Elements.Count);

		var tempPtrReg = NewLocal();
		var llvmArrTy = Type(arrayType);
		fw.WriteLine($"    %{tempPtrReg} = alloca {llvmArrTy}");

		EmitArrayInitializationInPlace(expr, tempPtrReg, arrayType, fw);

		return ($"ptr %{tempPtrReg}", arrayType);
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
			_ => TypeSymbol.Int
		};
	}

	private static string FormatDouble(double value)
	{
		var dblStr = value.ToString(CultureInfo.InvariantCulture);
		if (!dblStr.Contains('.') && !dblStr.Contains('e') && !dblStr.Contains('E'))
		{
			dblStr += ".0";
		}

		return $"double {dblStr}";
	}
}
