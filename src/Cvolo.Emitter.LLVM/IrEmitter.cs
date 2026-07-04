using Cvolo.Analysis;
using Cvolo.Core;

namespace Cvolo.Emitter.LLVM;

public sealed class IrEmitter
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
	private readonly Dictionary<string, string> _functionReturnTypes = [];
	private readonly Dictionary<string, string> _variableTypes = [];
	private readonly Stack<List<VariableSymbol>> _blockVariables = new();
	private CompilationContext? _context;
	private string? _currentNamespace;
	private CompilationUnitSyntax? _currentUnit;

	public string Emit(IReadOnlyList<CompilationUnitSyntax> units, CompilationContext context)
	{
		_context = context;
		_writer.WriteLine("; ModuleID = 'cvolo_module'");
		_writer.WriteLine("source_filename = \"cvolo_module\"");

		_writer.WriteLine("declare ptr @malloc(i64)");
		_writer.WriteLine("declare void @free(ptr)");
		_writer.WriteLine("declare i32 @puts(ptr)");
		_writer.WriteLine("declare void @exit(i32)");

		// First pass: register all struct types
		foreach (var unit in units)
		{
			_currentNamespace = unit.NamespaceDeclaration?.Name;
			var members = _currentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
				{
					var mangledName = string.IsNullOrEmpty(_currentNamespace) ? structDecl.Name : $"{_currentNamespace}.{structDecl.Name}";
					_astStructs[mangledName] = structDecl;
					var fieldTypes = string.Join(", ", structDecl.Fields.Select(f => Type(f.Type)));
					_writer.WriteLine($"%struct.{mangledName} = type {{ {fieldTypes} }}");
				}
			}
		}

		if (_astStructs.Count > 0)
			_writer.WriteLine();

		// Second pass: register all functions and extern declarations
		foreach (var unit in units)
		{
			_currentUnit = unit;
			_currentNamespace = unit.NamespaceDeclaration?.Name;
			var members = _currentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
				{
					var mangledName = string.IsNullOrEmpty(_currentNamespace) ? func.Name : $"{_currentNamespace}.{func.Name}";
					_astFunctions[mangledName] = func;
					_functionReturnTypes[mangledName] = func.ReturnType;
				}
				else if (member is ExternDeclarationSyntax ext)
				{
					_astExterns[ext.Name] = ext;
					_functionReturnTypes[ext.Name] = ext.ReturnType;
				}
			}
		}

		// Emit externs
		foreach (var ext in _astExterns.Values)
		{
			EmitExtern(ext);
		}

		// Emit user-defined functions
		foreach (var unit in units)
		{
			_currentNamespace = unit.NamespaceDeclaration?.Name;
			var members = _currentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
				{
					var funcWriter = new StringWriter();
					EmitFunction(func, funcWriter);
					_writer.Write(funcWriter.ToString());
				}
			}
		}

		if (_stringDefs.Count > 0)
		{
			_writer.WriteLine();
			foreach (var def in _stringDefs)
				_writer.WriteLine(def);
		}

		return _writer.ToString();
	}

	private void EmitExtern(ExternDeclarationSyntax ext)
	{
		var ret = Type(ext.ReturnType);
		var parms = ext.Parameters.Select(p => Type(p.Type)).ToList();
		if (ext.IsVariadic) parms.Add("...");
		_writer.WriteLine($"declare {ret} @{ext.Name}({string.Join(", ", parms)})");
		_writer.WriteLine();
	}

	private void EmitFunction(FunctionDeclarationSyntax func, StringWriter fw)
	{
		_labelCounter = 0;
		_localCounter = 0;
		_locals.Clear();

		var ret = Type(func.ReturnType);
		var parms = string.Join(", ", func.Parameters.Select(p => $"{Type(p.Type)} %{p.Name}"));
		fw.Write($"define {ret} @{func.Name}({parms})");
		fw.WriteLine(" {");
		fw.WriteLine("  entry:");

		foreach (var p in func.Parameters)
		{
			var ptr = NewLocal();
			_locals[p.Name] = ptr;
			// Track parameter types dynamically
			_variableTypes[p.Name] = p.Type;
			fw.WriteLine($"    %{ptr} = alloca {Type(p.Type)}");
			fw.WriteLine($"    store {Type(p.Type)} %{p.Name}, ptr %{ptr}");
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

		// Only emit automatic cleanup if the block does NOT end with a return.
		// If it has a return, EmitReturn handles the cleanup before the 'ret' instruction.
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
		// RAII: Free all heap variables in the current function before returning
		// For an MVP, we free all active heap variables tracked in the emitter.
		EmitCleanup(_locals.Keys.ToList(), fw);

		if (r.Expression is null)
		{
			fw.WriteLine("    ret void");
		}
		else
		{
			var (v, ty) = Eval(r.Expression, fw);

			if (v.StartsWith("ptr ") && _astStructs.ContainsKey(ty))
			{
				var valReg = NewLocal();
				var rawPtrReg = v.Split(' ')[^1];
				fw.WriteLine($"    %{valReg} = load %struct.{ty}, ptr {rawPtrReg}");
				fw.WriteLine($"    ret %struct.{ty} %{valReg}");
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
				// Evaluate the expression to write its side-effects (load, compute, store),
				// but do not print the returned value register to the output file.
				Eval(es.Expression, fw);
				break;
		}
	}

	private void EmitVar(VariableDeclarationSyntax v, StringWriter fw)
	{
		var typeName = v.Type;

		// Handle Explicit Reference Variables (ref / refvar)
		if (v.Type == "refvar" || v.Type == "ref")
		{
			var (val, valTy) = Eval(v.Initializer!, fw);
			var innerType = valTy.StartsWith("refvar ") ? valTy.Substring(7) : valTy.StartsWith("ref ") ? valTy.Substring(4) : valTy;
			typeName = v.Type == "refvar" ? $"refvar {innerType}" : $"ref {innerType}";

			var ptr = NewLocal();
			_locals[v.Name] = ptr;
			_variableTypes[v.Name] = typeName;

			fw.WriteLine($"    %{ptr} = alloca ptr");
			fw.WriteLine($"    store {val}, ptr %{ptr}");
			return;
		}

		// Handle Heap Allocations (RAII Owners)
		if (v.Initializer is HeapAllocationExpressionSyntax heapInit)
		{
			var (val, valTy) = Eval(heapInit, fw);
			var ptr = NewLocal();
			_locals[v.Name] = ptr;
			_variableTypes[v.Name] = valTy;
			_heapAllocatedVars.Add(v.Name);

			fw.WriteLine($"    %{ptr} = alloca ptr");
			fw.WriteLine($"    store {val}, ptr %{ptr}");
			return;
		}

		// Handle Standard Variables (Stack Structs or Primitives)
		if (typeName is not null)
		{
			var ptr = NewLocal();
			_locals[v.Name] = ptr;
			_variableTypes[v.Name] = typeName;

			var ty = Type(typeName);
			fw.WriteLine($"    %{ptr} = alloca {ty}");

			if (v.Initializer is not null)
			{
				if (v.Initializer is StructInitializationExpressionSyntax structInit)
				{
					EmitStructInitializationInPlace(structInit, ptr, fw);
				}
				else if (v.Initializer is ArrayInitializationExpressionSyntax arrInit)
				{
					EmitArrayInitializationInPlace(arrInit, ptr, typeName, fw);
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

			if (v.Initializer is ArrayInitializationExpressionSyntax arrInitInf)
			{
				var (_, elementTy) = Eval(arrInitInf.Elements[0], fw);
				var inferredType = $"{elementTy}[{arrInitInf.Elements.Count}]";

				var ptr = NewLocal();
				_locals[v.Name] = ptr;
				_variableTypes[v.Name] = inferredType;

				fw.WriteLine($"    %{ptr} = alloca {Type(inferredType)}");
				EmitArrayInitializationInPlace(arrInitInf, ptr, inferredType, fw);
				return;
			}

			var (val, valTy) = Eval(v.Initializer, fw);

			if (val.StartsWith("ptr "))
			{
				var valReg = val.Split(' ')[^1].TrimStart('%');
				_locals[v.Name] = valReg;
				_variableTypes[v.Name] = valTy;
			}
			else
			{
				var ptr = NewLocal();
				_locals[v.Name] = ptr;
				_variableTypes[v.Name] = valTy;

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

	private (string val, string ty) Eval(ExpressionSyntax expr, StringWriter fw)
	{
		return expr switch
		{
			IntegerLiteralExpressionSyntax n => ($"i32 {n.Value}", "i32"),
			DoubleLiteralExpressionSyntax d => ($"double {d.Value}", "double"),
			BooleanLiteralExpressionSyntax b => ($"i1 {(b.Value ? "1" : "0")}", "i1"),
			StringLiteralExpressionSyntax s => (AddString(s.Value), "ptr"),
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
			_ => throw new InvalidOperationException($"Unknown expr: {expr.GetType()}"),
		};
	}

	private void EmitCall(CallExpressionSyntax call, StringWriter fw)
	{
		List<string> args = [];

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var (val, valTy) = Eval(call.Arguments[i], fw);
			var paramTy = GetParamType(call.FunctionName, i);

			// If the parameter expects a slice but we are passing an array, perform coercion
			if (paramTy.Contains("[]") && valTy.Contains('['))
			{
				var coerced = CoerceArrayToSlice(val, valTy, paramTy, fw);
				args.Add(coerced);
			}
			else
			{
				args.Add(val);
			}
		}

		fw.WriteLine($"    call void @{call.FunctionName}({string.Join(", ", args)})");
	}

	private (string val, string ty) EmitCallExpr(CallExpressionSyntax call, StringWriter fw)
	{
		List<string> args = []; // C# 12 Collection Expression []

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var (val, valTy) = Eval(call.Arguments[i], fw);
			var paramTy = GetParamType(call.FunctionName, i);

			if (paramTy.Contains("[]") && valTy.Contains("["))
			{
				var coerced = CoerceArrayToSlice(val, valTy, paramTy, fw);
				args.Add(coerced);
			}
			else
			{
				args.Add(val);
			}
		}

		var r = NewLocal();
		var retTypeName = _functionReturnTypes.TryGetValue(call.FunctionName, out var ret) ? ret : "int";
		var ty = Type(retTypeName);

		fw.WriteLine($"    %{r} = call {ty} @{call.FunctionName}({string.Join(", ", args)})");
		return ($"{ty} %{r}", retTypeName);
	}

	private (string val, string ty) EmitBin(BinaryExpressionSyntax bin, StringWriter fw)
	{
		var (l, _) = Eval(bin.Left, fw);
		var (r, _) = Eval(bin.Right, fw);
		var reg = NewLocal();
		var (op, resultTy) = bin.Operator switch
		{
			"+" => ($"add i32 {V(l)}, {V(r)}", "i32"),
			"-" => ($"sub i32 {V(l)}, {V(r)}", "i32"),
			"*" => ($"mul i32 {V(l)}, {V(r)}", "i32"),
			"/" => ($"sdiv i32 {V(l)}, {V(r)}", "i32"),
			"%" => ($"srem i32 {V(l)}, {V(r)}", "i32"),
			"==" => ($"icmp eq i32 {V(l)}, {V(r)}", "i1"),
			"!=" => ($"icmp ne i32 {V(l)}, {V(r)}", "i1"),
			"<" => ($"icmp slt i32 {V(l)}, {V(r)}", "i1"),
			">" => ($"icmp sgt i32 {V(l)}, {V(r)}", "i1"),
			"<=" => ($"icmp sle i32 {V(l)}, {V(r)}", "i1"),
			">=" => ($"icmp sge i32 {V(l)}, {V(r)}", "i1"),
			"&" => ($"and i32 {V(l)}, {V(r)}", "i32"),
			"|" => ($"or i32 {V(l)}, {V(r)}", "i32"),
			"^" => ($"xor i32 {V(l)}, {V(r)}", "i32"),
			"<<" => ($"shl i32 {V(l)}, {V(r)}", "i32"),
			">>" => ($"ashr i32 {V(l)}, {V(r)}", "i32"),  // Arithmetic Right Shift
			">>>" => ($"lshr i32 {V(l)}, {V(r)}", "i32"),  // Logical Right Shift
			_ => throw new InvalidOperationException($"Unknown binop '{bin.Operator}'"),
		};
		fw.WriteLine($"    %{reg} = {op}");
		return ($"{resultTy} %{reg}", resultTy);
	}

	private (string val, string ty) EmitUnary(UnaryExpressionSyntax u, StringWriter fw)
	{
		if (u.Operator.EndsWith("_postfix") || u.Operator.EndsWith("_prefix"))
		{
			return EmitIncrementDecrement(u, u.Operator.EndsWith("_prefix"), u.Operator.StartsWith("++"), fw);
		}

		var (o, _) = Eval(u.Operand, fw);
		var r = NewLocal();
		var (op, resultTy) = u.Operator switch
		{
			"-" => ($"sub i32 0, {V(o)}", "i32"),
			"!" => ($"xor i1 1, {V(o)}", "i1"),
			"~" => ($"xor i32 {V(o)}, -1", "i32"),
			_ => throw new InvalidOperationException($"Unknown unary op '{u.Operator}'"),
		};
		fw.WriteLine($"    %{r} = {op}");
		return ($"{resultTy} %{r}", resultTy);
	}

	private void EmitStore(BinaryExpressionSyntax assign, StringWriter fw)
	{
		if (assign.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var (r, _) = Eval(assign.Right, fw);
			fw.WriteLine($"    store {r}, ptr %{ptr}");
		}
		else if (assign.Left is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, _) = GetFieldPointer(m, fw);
			var (r, _) = Eval(assign.Right, fw);
			fw.WriteLine($"    store {r}, ptr %{fieldPtr}");
		}
		else if (assign.Left is IndexExpressionSyntax idx)
		{
			var (elementPtr, _) = GetFieldPointer(idx, fw);
			var (r, _) = Eval(assign.Right, fw);
			fw.WriteLine($"    store {r}, ptr %{elementPtr}");
		}
	}

	private (string val, string ty) EmitLoadStore(BinaryExpressionSyntax assign, StringWriter fw)
	{
		if (assign.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var (r, _) = Eval(assign.Right, fw);
			fw.WriteLine($"    store {r}, ptr %{ptr}");
			return (r, "i32");
		}

		throw new InvalidOperationException($"Cannot assign to non-variable");
	}

	private (string val, string ty) Load(string name, StringWriter fw)
	{
		if (!_locals.TryGetValue(name, out var ptr))
			throw new InvalidOperationException($"Undefined variable '{name}'");

		var typeName = _variableTypes[name];
		var ty = Type(typeName);

		var reg = NewLocal();
		fw.WriteLine($"    %{reg} = load {ty}, ptr %{ptr}");

		// If the variable is a pointer/reference, we perform a second load 
		// to retrieve the actual primitive value (int, double, bool, char)
		if (typeName.StartsWith("ref ") || typeName.StartsWith("refvar "))
		{
			var resolvedType = typeName.StartsWith("refvar ") ? typeName.Substring(7) : typeName.Substring(4);
			if (resolvedType == "int" || resolvedType == "double" || resolvedType == "bool" || resolvedType == "char")
			{
				var valReg = NewLocal();
				var innerTy = Type(resolvedType);
				fw.WriteLine($"    %{valReg} = load {innerTy}, ptr %{reg}");
				return ($"{innerTy} %{valReg}", resolvedType);
			}
		}

		return ($"{ty} %{reg}", ty);
	}

	private string AddString(string value)
	{
		var idx = _stringIndex++;
		var escaped = string.Concat(value.Select(c => c switch
		{
			'\n' => "\\0a",
			'\r' => "\\0d",
			'\t' => "\\09",
			'"' => "\\22",
			'\\' => "\\5c",
			_ when c < 32 || c > 126 => $"\\{(int)c:x2}", // Converts \x1b automatically to \1b for LLVM
			_ => c.ToString(),
		}));
		_stringDefs.Add($"@str{idx} = private unnamed_addr constant [{value.Length + 1} x i8] c\"{escaped}\\00\"");
		return $"ptr @str{idx}";
	}

	private string NextLabel() => $"L{_labelCounter++}";
	private string NewLocal() => (_localCounter++).ToString();

	private string Type(string t)
	{
		if (t.StartsWith("ref ") || t.StartsWith("refvar "))
			return "ptr";

		// 1. Dynamic Slice Types (e.g. "int[]" or "refvar int[]" which was unpacked)
		if (t.EndsWith("[]"))
			return "{ ptr, i32 }";

		// 2. Static Array Types (e.g. "int[5]")
		if (t.Contains('['))
		{
			var openBracket = t.LastIndexOf('[');
			var size = t.Substring(openBracket + 1, t.Length - openBracket - 2);
			var inner = t.Substring(0, openBracket);
			return $"[{size} x {Type(inner)}]";
		}

		return t switch
		{
			"void" => "void",
			"int" or "Int32" => "i32",
			"double" or "Double" => "double",
			"bool" or "Boolean" => "i1",
			"string" or "String" => "ptr",
			"char" or "Char" => "i8",
			_ => _astStructs.ContainsKey(t) ? $"%struct.{t}" : "i32",
		};
	}

	private static string V(string typed) => typed.Split(' ')[^1];
	private static bool EndsWithReturn(SyntaxNode s) => s switch
	{
		BlockStatementSyntax b => b.Statements.Count > 0 && b.Statements[^1] is ReturnStatementSyntax,
		ReturnStatementSyntax => true,
		_ => false,
	};

	private (string val, string ty) EmitMemberAccess(MemberAccessExpressionSyntax m, StringWriter fw)
	{
		var (fieldPtr, fieldTypeName) = GetFieldPointer(m, fw);
		var loadedReg = NewLocal();
		var fTy = Type(fieldTypeName);
		fw.WriteLine($"    %{loadedReg} = load {fTy}, ptr %{fieldPtr}");
		return ($"{fTy} %{loadedReg}", fTy);
	}

	private (string ptr, string typeName) GetFieldPointer(ExpressionSyntax expr, StringWriter fw)
	{
		if (expr is IdentifierExpressionSyntax id)
		{
			if (!_locals.TryGetValue(id.Name, out var structPtr))
				throw new InvalidOperationException($"Undefined variable '{id.Name}'");

			var typeName = _variableTypes[id.Name];

			// If the variable is a reference OR a heap-allocated owner, 
			// we must load the pointer from the stack first.
			if (typeName.StartsWith("ref ") || typeName.StartsWith("refvar ") || _heapAllocatedVars.Contains(id.Name))
			{
				var actualPtr = NewLocal();
				fw.WriteLine($"    %{actualPtr} = load ptr, ptr %{structPtr}");

				// Clean up the type name for field lookup
				var innerType = typeName.StartsWith("refvar ") ? typeName.Substring(7) :
							   typeName.StartsWith("ref ") ? typeName.Substring(4) : typeName;

				// Resolve namespaced type name
				innerType = ResolveStructName(innerType, _currentUnit!);
				return (actualPtr, innerType);
			}

			// Resolve namespaced type name
			var resolvedTypeName = ResolveStructName(typeName, _currentUnit!);
			return (structPtr, resolvedTypeName);
		}
		else if (expr is MemberAccessExpressionSyntax m)
		{
			var (parentPtr, parentTypeName) = GetFieldPointer(m.Expression, fw);

			// 1. Slices have a built-in 'Length' field at index 1 of the anonymous struct { ptr, i32 }
			if (parentTypeName.EndsWith("[]") && m.MemberName == "Length")
			{
				var lengthPtrReg = NewLocal();
				fw.WriteLine($"    %{lengthPtrReg} = getelementptr inbounds {{ ptr, i32 }}, ptr %{parentPtr}, i32 0, i32 1");
				return (lengthPtrReg, "int");
			}

			// Resolve namespaced type name
			parentTypeName = ResolveStructName(parentTypeName, _currentUnit!);

			var structDecl = _astStructs[parentTypeName];
			var fieldIndex = -1;
			var fieldType = "int";

			for (var i = 0; i < structDecl.Fields.Count; i++)
			{
				if (structDecl.Fields[i].Name == m.MemberName)
				{
					fieldIndex = i;
					fieldType = structDecl.Fields[i].Type;
					break;
				}
			}

			var fieldPtrReg = NewLocal();
			var structTy = $"%struct.{parentTypeName}";
			fw.WriteLine($"    %{fieldPtrReg} = getelementptr inbounds {structTy}, ptr %{parentPtr}, i32 0, i32 {fieldIndex}");

			return (fieldPtrReg, fieldType);
		}
		else if (expr is IndexExpressionSyntax idx)
		{
			var (parentPtr, parentTypeName) = GetFieldPointer(idx.Left, fw);
			var (indexVal, _) = Eval(idx.Index, fw);

			// Case A: Dynamic Slice indexing (type name ends with "[]", e.g., "int[]")
			if (parentTypeName.EndsWith("[]"))
			{
				// 1. Load the actual array pointer from field 0 of the slice struct { ptr, i32 }
				var arrPtrReg = NewLocal();
				fw.WriteLine($"    %{arrPtrReg} = getelementptr inbounds {{ ptr, i32 }}, ptr %{parentPtr}, i32 0, i32 0");
				var arrayPtr = NewLocal();
				fw.WriteLine($"    %{arrayPtr} = load ptr, ptr %{arrPtrReg}");

				// 2. Load the slice length from field 1 for Bounds Checking
				var lenPtrReg = NewLocal();
				fw.WriteLine($"    %{lenPtrReg} = getelementptr inbounds {{ ptr, i32 }}, ptr %{parentPtr}, i32 0, i32 1");
				var lengthReg = NewLocal();
				fw.WriteLine($"    %{lengthReg} = load i32, ptr %{lenPtrReg}");

				// 3. Bounds check (index < length)
				var isSafeLabel = NextLabel();
				var panicLabel = NextLabel();
				var checkReg = NewLocal();

				fw.WriteLine($"    %{checkReg} = icmp ult i32 {V(indexVal)}, %{lengthReg}");
				fw.WriteLine($"    br i1 %{checkReg}, label %{isSafeLabel}, label %{panicLabel}");

				// Panic Block (Outputs professional C# Style error message)
				fw.WriteLine($"  {panicLabel}:");
				var errorLines = _context!.FormatDiagnostic("Runtime Error", "Index was outside the bounds of the array.", idx.Span);
				foreach (var line in errorLines)
				{
					var strPtr = AddString(line);
					var reg = NewLocal();
					fw.WriteLine($"    %{reg} = call i32 @puts(ptr {strPtr.Split(' ')[^1]})");
				}

				fw.WriteLine($"    call void @exit(i32 1)");
				fw.WriteLine($"    unreachable");

				// Safe Block: Get element address
				fw.WriteLine($"  {isSafeLabel}:");
				var elementPtrReg = NewLocal();
				var innerType = parentTypeName[..^2];
				var elementLlvmTy = Type(innerType);
				fw.WriteLine($"    %{elementPtrReg} = getelementptr inbounds {elementLlvmTy}, ptr %{arrayPtr}, i32 {V(indexVal)}");
				return (elementPtrReg, innerType);
			}
			else
			{
				// Case B: Static Array indexing (type name contains "[size]", e.g., "int[5]")
				var openBracket = parentTypeName.LastIndexOf('[');
				var innerTypeName = parentTypeName.Substring(0, openBracket);
				var sizeStr = parentTypeName.Substring(openBracket + 1, parentTypeName.Length - openBracket - 2);

				var isArraySafeLabel = NextLabel();
				var arrayPanicLabel = NextLabel();

				// Bounds check (index < static_size)
				var arrayCheckReg = NewLocal();
				fw.WriteLine($"    %{arrayCheckReg} = icmp ult i32 {V(indexVal)}, {sizeStr}");
				fw.WriteLine($"    br i1 %{arrayCheckReg}, label %{isArraySafeLabel}, label %{arrayPanicLabel}");

				// Runtime Error Block
				fw.WriteLine($"  {arrayPanicLabel}:");
				var arrErrorLines = _context!.FormatDiagnostic("Runtime Error", "Index was outside the bounds of the array.", idx.Span);
				foreach (var line in arrErrorLines)
				{
					var strPtr = AddString(line);
					var reg = NewLocal();
					fw.WriteLine($"    %{reg} = call i32 @puts(ptr {strPtr.Split(' ')[^1]})");
				}

				fw.WriteLine($"    call void @exit(i32 1)");
				fw.WriteLine($"    unreachable");

				// Safe Block: Get element address
				fw.WriteLine($"  {isArraySafeLabel}:");
				var arrayElementPtrReg = NewLocal();
				var llvmArrTy = Type(parentTypeName);
				fw.WriteLine($"    %{arrayElementPtrReg} = getelementptr inbounds {llvmArrTy}, ptr %{parentPtr}, i32 0, i32 {V(indexVal)}");

				return (arrayElementPtrReg, innerTypeName);
			}
		}

		throw new InvalidOperationException("Unsupported field pointer expression");
	}

	private (string val, string ty) EmitStructInitialization(StructInitializationExpressionSyntax expr, StringWriter fw)
	{
		var mangledName = ResolveStructName(expr.StructTypeName, _currentUnit!);
		var tempPtrReg = NewLocal();
		var structTy = $"%struct.{mangledName}";
		fw.WriteLine($"    %{tempPtrReg} = alloca {structTy}");
		EmitStructInitializationInPlace(expr, tempPtrReg, fw);
		return ($"ptr %{tempPtrReg}", mangledName);
	}

	private void EmitStructInitializationInPlace(StructInitializationExpressionSyntax expr, string destPtr, StringWriter fw)
	{
		var mangledName = ResolveStructName(expr.StructTypeName, _currentUnit!);
		var structTy = $"%struct.{mangledName}";
		foreach (var init in expr.Initializers)
		{
			var structDecl = _astStructs[mangledName];
			var fieldIndex = -1;
			var fieldType = "int";

			for (var i = 0; i < structDecl.Fields.Count; i++)
			{
				if (structDecl.Fields[i].Name == init.MemberName)
				{
					fieldIndex = i;
					fieldType = structDecl.Fields[i].Type;
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


	private (string val, string ty) EmitBorrowExpression(BorrowExpressionSyntax expr, StringWriter fw)
	{
		if (expr.Expression is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
		{
			var typeName = _variableTypes[id.Name];

			// If the variable is already a reference, load and return its pointer value
			if (typeName.StartsWith("ref ") || typeName.StartsWith("refvar "))
			{
				var actualPtr = NewLocal();
				fw.WriteLine($"    %{actualPtr} = load ptr, ptr %{ptr}");
				return ($"ptr %{actualPtr}", typeName);
			}

			return ($"ptr %{ptr}", $"refvar {typeName}");
		}
		else if (expr.Expression is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, fieldTypeName) = GetFieldPointer(m, fw);
			return ($"ptr %{fieldPtr}", $"refvar {fieldTypeName}");
		}

		throw new InvalidOperationException("Can only borrow variables or member fields");
	}

	private (string val, string ty) EmitIncrementDecrement(UnaryExpressionSyntax u, bool isPrefix, bool isIncrement, StringWriter fw)
	{
		string ptrReg;
		string typeName;

		if (u.Operand is IdentifierExpressionSyntax id)
		{
			if (!_locals.TryGetValue(id.Name, out var structPtr))
				throw new InvalidOperationException($"Undefined variable '{id.Name}'");
			ptrReg = structPtr;
			typeName = _variableTypes[id.Name];
		}
		else if (u.Operand is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, fieldTypeName) = GetFieldPointer(m, fw);
			ptrReg = fieldPtr;
			typeName = fieldTypeName;
		}
		else
		{
			throw new InvalidOperationException("Increment/decrement operand must be a variable or struct field");
		}

		var ty = Type(typeName);

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
			return ($"{ty} %{newValReg}", typeName); // Prefix returns the NEW value
		}
		else
		{
			return ($"{ty} %{currentValReg}", typeName); // Postfix returns the OLD value
		}
	}

	private (string val, string ty) EmitHeapAllocation(HeapAllocationExpressionSyntax expr, StringWriter fw)
	{
		if (expr.Expression is not StructInitializationExpressionSyntax s)
			throw new InvalidOperationException("Heap allocation currently only supported for structs");

		var mangledName = ResolveStructName(s.StructTypeName, _currentUnit!);
		var structDecl = _astStructs[mangledName];

		var size = structDecl.Fields.Count * 8;

		var ptrReg = NewLocal();
		fw.WriteLine($"    %{ptrReg} = call ptr @malloc(i64 {size})");

		EmitStructInitializationInPlace(s, ptrReg, fw);

		return ($"ptr %{ptrReg}", mangledName);
	}

	private (string val, string ty) EmitIndexExpression(IndexExpressionSyntax idx, StringWriter fw)
	{
		// 1. Get the pointer to the specific array element
		var (elementPtr, elementTypeName) = GetFieldPointer(idx, fw);

		// 2. Load the value from that pointer
		var loadedReg = NewLocal();
		var fTy = Type(elementTypeName);
		fw.WriteLine($"    %{loadedReg} = load {fTy}, ptr %{elementPtr}");

		return ($"{fTy} %{loadedReg}", elementTypeName);
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

	private void EmitArrayInitializationInPlace(ArrayInitializationExpressionSyntax expr, string destPtr, string arrayTypeName, StringWriter fw)
	{
		var llvmArrTy = Type(arrayTypeName);

		for (int i = 0; i < expr.Elements.Count; i++)
		{
			var elementPtrReg = NewLocal();
			// Get pointer to arr[i]
			fw.WriteLine($"    %{elementPtrReg} = getelementptr inbounds {llvmArrTy}, ptr %{destPtr}, i32 0, i32 {i}");

			// Evaluate the element and store it
			var (val, _) = Eval(expr.Elements[i], fw);
			fw.WriteLine($"    store {val}, ptr %{elementPtrReg}");
		}
	}

	private string CoerceArrayToSlice(string arrayVal, string arrayTy, string sliceTy, StringWriter fw)
	{
		var openBracket = arrayTy.LastIndexOf('[');
		var sizeStr = arrayTy.Substring(openBracket + 1, arrayTy.Length - openBracket - 2);

		var slicePtr = NewLocal();
		fw.WriteLine($"    %{slicePtr} = alloca {{ ptr, i32 }}");

		// Store array pointer into index 0
		var ptrField = NewLocal();
		fw.WriteLine($"    %{ptrField} = getelementptr inbounds {{ ptr, i32 }}, ptr %{slicePtr}, i32 0, i32 0");
		fw.WriteLine($"    store {arrayVal}, ptr %{ptrField}");

		// Store size into index 1
		var sizeField = NewLocal();
		fw.WriteLine($"    %{sizeField} = getelementptr inbounds {{ ptr, i32 }}, ptr %{slicePtr}, i32 0, i32 1");
		fw.WriteLine($"    store i32 {sizeStr}, ptr %{sizeField}");

		return $"ptr %{slicePtr}";
	}

	private string GetParamType(string funcName, int index)
	{
		if (_astFunctions.TryGetValue(funcName, out var func) && index < func.Parameters.Count)
			return func.Parameters[index].Type;

		if (_astExterns.TryGetValue(funcName, out var ext) && index < ext.Parameters.Count)
			return ext.Parameters[index].Type;

		return "int";
	}

	private string ResolveStructName(string name, CompilationUnitSyntax activeUnit)
	{
		if (_astStructs.ContainsKey(name)) return name;

		var currentNamespace = activeUnit.NamespaceDeclaration?.Name;
		var localMangled = string.IsNullOrEmpty(currentNamespace) ? name : $"{currentNamespace}.{name}";
		if (_astStructs.ContainsKey(localMangled)) return localMangled;

		var activeUsings = new List<string>(activeUnit.Usings.Select(u => u.NamespaceName));
		if (activeUnit.NamespaceDeclaration is not null)
			activeUsings.AddRange(activeUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

		foreach (var ns in activeUsings)
		{
			var candidateMangled = $"{ns}.{name}";
			if (_astStructs.ContainsKey(candidateMangled)) return candidateMangled;
		}

		return name;
	}
}
