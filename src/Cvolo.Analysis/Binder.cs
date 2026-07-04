using Cvolo.Analysis.@struct;
using Cvolo.Core;

namespace Cvolo.Analysis;

public sealed class Binder
{
	private readonly List<BorrowSymbol> _activeBorrows = [];
	private readonly DiagnosticBag _diagnostics = new();
	private readonly SymbolTable _globals = new();
	private readonly Dictionary<string, StructTypeSymbol> _structTypes = [];

	public DiagnosticBag Diagnostics => _diagnostics;

	public void Bind(CompilationUnitSyntax unit)
	{
		foreach (var member in unit.Members)
		{
			if (member is StructDeclarationSyntax structDecl)
			{
				DeclareStruct(structDecl);
			}
		}

		// First pass: collect all declarations
		foreach (var member in unit.Members)
		{
			switch (member)
			{
				case FunctionDeclarationSyntax func:
					DeclareFunction(func);
					break;
				case ExternDeclarationSyntax ext:
					DeclareExternFunction(ext);
					break;
			}
		}

		// Second pass: check function bodies
		foreach (var member in unit.Members)
		{
			if (member is FunctionDeclarationSyntax func)
				CheckFunctionBody(func);
		}
	}

	private void DeclareStruct(StructDeclarationSyntax structDecl)
	{
		if (_structTypes.ContainsKey(structDecl.Name) || TypeSymbol.FromName(structDecl.Name) is not null)
		{
			_diagnostics.Report(structDecl.Span, $"Duplicate definition of type '{structDecl.Name}'");
			return;
		}

		var fields = new List<StructFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in structDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				_diagnostics.Report(field.Span, $"Duplicate field '{field.Name}' in struct '{structDecl.Name}'");
				continue;
			}

			var fieldType = ResolveType(field.Type);
			if (fieldType is null)
			{
				_diagnostics.Report(field.Span, $"Unknown type '{field.Type}' of field '{field.Name}'");
				continue;
			}

			fields.Add(new StructFieldSymbol(field.Name, fieldType));
		}

		var structSymbol = new StructTypeSymbol(structDecl.Name, fields);
		_structTypes[structDecl.Name] = structSymbol;
	}

	private void DeclareFunction(FunctionDeclarationSyntax func)
	{
		var type = ResolveType(func.ReturnType);
		if (type is null)
		{
			_diagnostics.Report(func.Span, $"Unknown return type '{func.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in func.Parameters)
		{
			var paramType = ResolveType(param.Type);
			if (paramType is null)
			{
				_diagnostics.Report(param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = _globals.Lookup(func.Name);
		if (existing is not null)
		{
			_diagnostics.Report(func.Span, $"Duplicate definition of '{func.Name}'");
			return;
		}

		_globals.Declare(new FunctionSymbol(func.Name, type, parameters));
	}

	private void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		var returnType = ResolveType(ext.ReturnType);
		if (returnType is null)
		{
			_diagnostics.Report(ext.Span, $"Unknown return type '{ext.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in ext.Parameters)
		{
			var paramType = ResolveType(param.Type);
			if (paramType is null)
			{
				_diagnostics.Report(param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}
			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = _globals.Lookup(ext.Name);
		if (existing is not null)
		{
			_diagnostics.Report(ext.Span, $"Duplicate definition of '{ext.Name}'");
			return;
		}

		_globals.Declare(new FunctionSymbol(ext.Name, returnType, parameters, isExtern: true, isVariadic: ext.IsVariadic));
	}

	private void CheckFunctionBody(FunctionDeclarationSyntax func)
	{
		var localScope = new SymbolTable(_globals);

		// Add parameters to local scope
		foreach (var param in func.Parameters)
		{
			var paramType = ResolveType(param.Type);
			if (paramType is not null)
			{
				var varSymbol = new VariableSymbol(param.Name, paramType, isMutable: false)
				{
					IsInitialized = true
				};
				localScope.Declare(varSymbol);
			}
		}

		CheckBlock(func.Body, localScope, func);
	}

	private void CheckBlock(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		var borrowCountBefore = _activeBorrows.Count;

		foreach (var stmt in block.Statements)
		{
			CheckStatement(stmt, scope, currentFunc);
		}

		// Automatic Lifetime Release: End borrows of variables whose lifetimes expire with this block
		if (_activeBorrows.Count > borrowCountBefore)
		{
			_activeBorrows.RemoveRange(borrowCountBefore, _activeBorrows.Count - borrowCountBefore);
		}
	}

	private void CheckStatement(SyntaxNode stmt, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		switch (stmt)
		{
			case ReturnStatementSyntax ret:
				CheckReturnStatement(ret, scope, currentFunc);
				break;
			case ExpressionStatementSyntax exprStmt:
				CheckExpression(exprStmt.Expression, scope);
				break;
			case VariableDeclarationSyntax varDecl:
				CheckVariableDeclaration(varDecl, scope, currentFunc);
				break;
			case BlockStatementSyntax block:
				CheckBlock(block, new SymbolTable(scope), currentFunc);
				break;
			case IfStatementSyntax ifStmt:
				CheckExpression(ifStmt.Condition, scope);
				CheckStatement(ifStmt.ThenStatement, scope, currentFunc);
				if (ifStmt.ElseClause is not null)
					CheckStatement(ifStmt.ElseClause.Body, scope, currentFunc);
				break;
			case WhileStatementSyntax whileStmt:
				CheckExpression(whileStmt.Condition, scope);
				CheckStatement(whileStmt.Body, scope, currentFunc);
				break;
			case ForStatementSyntax forStmt:
				{
					var forScope = new SymbolTable(scope);
					CheckVariableDeclaration(forStmt.Initializer, forScope, currentFunc);
					CheckExpression(forStmt.Condition, forScope);
					CheckExpression(forStmt.Increment, forScope);
					CheckStatement(forStmt.Body, forScope, currentFunc);
					break;
				}
		}
	}

	private void CheckVariableDeclaration(VariableDeclarationSyntax varDecl, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		var existing = scope.Lookup(varDecl.Name);
		if (existing is not null)
		{
			_diagnostics.Report(varDecl.Span, $"Variable '{varDecl.Name}' is already declared in this scope");
			return;
		}

		TypeSymbol? resolvedType = null;
		if (varDecl.Initializer is not null)
		{
			CheckExpression(varDecl.Initializer, scope);
			resolvedType = GetExpressionType(varDecl.Initializer, scope);
		}

		if (varDecl.Type == "refvar" || varDecl.Type == "ref")
		{
			if (resolvedType is null)
			{
				_diagnostics.Report(varDecl.Span, $"Reference type inference requires an initializer");
				return;
			}

			var isMutable = varDecl.Type == "refvar";

			// Enforce Borrow Checker Rules (Aliasing XOR Mutability)
			if (varDecl.Initializer is BorrowExpressionSyntax declBorrow)
			{
				var borrowedName = GetBaseIdentifierName(declBorrow.Expression);
				if (borrowedName is not null)
				{
					var conflicts = _activeBorrows.Where(b => b.BorrowedName == borrowedName).ToList();
					if (conflicts.Count > 0)
					{
						if (isMutable)
						{
							// A mutable borrow requires 100% exclusive access
							_diagnostics.Report(varDecl.Span, $"Cannot borrow '{borrowedName}' mutably because it is already borrowed by '{conflicts[0].BorrowerName}'");
						}
						else if (conflicts.Any(c => c.IsMutable))
						{
							// A read-only borrow cannot alias with an active mutable borrow
							var mutConflict = conflicts.First(c => c.IsMutable);
							_diagnostics.Report(varDecl.Span, $"Cannot borrow '{borrowedName}' as read-only because it is already borrowed mutably by '{mutConflict.BorrowerName}'");
						}
					}

					// Register the active borrow
					_activeBorrows.Add(new BorrowSymbol(varDecl.Name, borrowedName, isMutable, varDecl.Span));
				}
			}

			if (resolvedType is PointerTypeSymbol ptrType)
			{
				resolvedType = new PointerTypeSymbol(ptrType.ReferencedType, isMutable);
			}
			else
			{
				resolvedType = new PointerTypeSymbol(resolvedType, isMutable);
			}
		}
		else if (varDecl.Type is not null)
		{
			var declaredType = ResolveType(varDecl.Type);
			if (declaredType is null)
			{
				_diagnostics.Report(varDecl.Span, $"Unknown type '{varDecl.Type}'");
				return;
			}

			if (resolvedType is not null && !resolvedType.Equals(declaredType))
			{
				_diagnostics.Report(varDecl.Span, $"Cannot initialize variable of type '{declaredType.Name}' with value of type '{resolvedType.Name}'");
			}

			resolvedType = declaredType;

			if (declaredType is ArrayTypeSymbol declArr && resolvedType is ArrayTypeSymbol initArr)
			{
				if (declArr.Size != initArr.Size)
				{
					_diagnostics.Report(varDecl.Span, $"Array size mismatch: declared size is {declArr.Size}, but initializer has {initArr.Size} elements");
				}
			}
		}

		resolvedType ??= TypeSymbol.Int;

		var pointsToParam = false;
		if (resolvedType is PointerTypeSymbol && varDecl.Initializer is BorrowExpressionSyntax borrow)
		{
			var baseName = GetBaseIdentifierName(borrow.Expression);
			if (baseName is not null)
			{
				pointsToParam = currentFunc.Parameters.Any(param => param.Name == baseName);
			}
		}

		var varSymbol = new VariableSymbol(varDecl.Name, resolvedType, varDecl.IsMutable)
		{
			// Track lifetime safety
			PointsToParameter = pointsToParam,
			IsInitialized = varDecl.Initializer is not null
		};

		if (varDecl.Initializer is HeapAllocationExpressionSyntax)
		{
			var symbol = scope.Lookup(varDecl.Name) as VariableSymbol;
			symbol?.IsHeapAllocated = true;
		}

		scope.Declare(varSymbol);
	}

	private void CheckExpression(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				{
					var symbol = scope.Lookup(id.Name);

					if (symbol is null)
					{
						_diagnostics.Report(id.Span, $"Undefined variable '{id.Name}'");
					}
					else if (symbol is VariableSymbol varSymbol)
					{
						if (varSymbol.IsMoved)
						{
							_diagnostics.Report(id.Span, $"Use of moved variable '{id.Name}'");
						}
						else if (!varSymbol.IsInitialized)
						{
							_diagnostics.Report(id.Span, $"Use of possibly-uninitialized variable '{id.Name}'");
						}
					}

					break;
				}
			case MemberAccessExpressionSyntax memberAccess:
				CheckMemberAccessExpression(memberAccess, scope);
				break;
			case BorrowExpressionSyntax borrow:
				CheckBorrowExpression(borrow, scope);
				break;
			case StructInitializationExpressionSyntax structInit:
				CheckStructInitializationExpression(structInit, scope);
				break;
			case HeapAllocationExpressionSyntax heap:
				CheckExpression(heap.Expression, scope);
				break;
			case ArrayInitializationExpressionSyntax arrInit:
				foreach (var el in arrInit.Elements) CheckExpression(el, scope);
				break;
			case CallExpressionSyntax call:
				{
					var symbol = scope.Lookup(call.FunctionName);
					if (symbol is null)
					{
						_diagnostics.Report(call.Span, $"Undefined function '{call.FunctionName}'");
						return;
					}

					if (symbol is not FunctionSymbol func)
					{
						_diagnostics.Report(call.Span, $"'{call.FunctionName}' is not a function");
						return;
					}

					var argCount = call.Arguments.Count;
					var paramCount = func.Parameters.Count;
					var isVariadic = func.IsVariadic;

					if (!isVariadic && argCount != paramCount)
					{
						_diagnostics.Report(call.Span, $"Function '{call.FunctionName}' expects {paramCount} arguments but received {argCount}");
						return;
					}

					if (isVariadic && argCount < paramCount)
					{
						_diagnostics.Report(call.Span, $"Function '{call.FunctionName}' expects at least {paramCount} arguments but received {argCount}");
						return;
					}

					for (var i = 0; i < call.Arguments.Count; i++)
					{
						var arg = call.Arguments[i];
						CheckExpression(arg, scope);

						// If the argument is a variable identifier
						if (arg is IdentifierExpressionSyntax id)
						{
							var argSymbol = scope.Lookup(id.Name) as VariableSymbol;
							if (argSymbol is not null)
							{
								// Safely check parameter type, accounting for variadic arguments (like in printf)
								var isPointerParam = i < func.Parameters.Count && func.Parameters[i].Type is PointerTypeSymbol;

								// If the parameter is NOT a pointer (is passed by value) and it's a struct, it is a MOVE!
								if (!isPointerParam && argSymbol.Type is StructTypeSymbol)
								{
									argSymbol.IsMoved = true;
								}
							}
						}
					}

					break;
				}
			case BinaryExpressionSyntax bin:
				{
					if (bin.Operator == "=")
					{
						// 1. Evaluate the right-hand side first (reads and moves happen here)
						CheckExpression(bin.Right, scope);

						// 2. Evaluate the left-hand side second (re-initialization happens here)
						if (bin.Left is IdentifierExpressionSyntax id)
						{
							var varSymbol = scope.Lookup(id.Name) as VariableSymbol;
							if (varSymbol is not null)
							{
								if (!varSymbol.IsMutable)
								{
									_diagnostics.Report(id.Span, $"Cannot assign to immutable variable '{id.Name}'");
								}

								varSymbol.IsMoved = false;
								varSymbol.IsInitialized = true;
							}
							else
							{
								_diagnostics.Report(id.Span, $"Undefined variable '{id.Name}'");
							}
						}
						else
						{
							CheckExpression(bin.Left, scope);
						}
					}



					break;
				}
			case UnaryExpressionSyntax unary:
				CheckExpression(unary.Operand, scope);
				break;
		}
	}

	private TypeSymbol? CheckMemberAccessExpression(MemberAccessExpressionSyntax expr, SymbolTable scope)
	{
		CheckExpression(expr.Expression, scope);
		var leftType = GetExpressionType(expr.Expression, scope);
		if (leftType is null) return null;

		// Automatically dereference references to access underlying struct fields
		if (leftType is PointerTypeSymbol pointerType)
		{
			leftType = pointerType.ReferencedType;
		}

		// Slices have a built-in read-only 'Length' field of type 'int'
		if (leftType.Name.EndsWith("[]") && expr.MemberName == "Length")
		{
			return TypeSymbol.Int;
		}

		if (leftType is not StructTypeSymbol structType)
		{
			_diagnostics.Report(expr.Span, $"Type '{leftType.Name}' is not a struct; cannot access member '{expr.MemberName}'");
			return null;
		}

		var field = structType.FindField(expr.MemberName);
		if (field is null)
		{
			_diagnostics.Report(expr.Span, $"Struct '{structType.Name}' does not contain field '{expr.MemberName}'");
			return null;
		}

		return field.Type;
	}

	private TypeSymbol? GetExpressionType(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => (scope.Lookup(id.Name) as VariableSymbol)?.Type,
			IntegerLiteralExpressionSyntax => TypeSymbol.Int,
			DoubleLiteralExpressionSyntax => TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			MemberAccessExpressionSyntax m => CheckMemberAccessExpression(m, scope),
			BorrowExpressionSyntax b => CheckBorrowExpression(b, scope),
			StructInitializationExpressionSyntax s => CheckStructInitializationExpression(s, scope),
			HeapAllocationExpressionSyntax h => GetExpressionType(h.Expression, scope),
			IndexExpressionSyntax idx => (GetExpressionType(idx.Left, scope) as ArrayTypeSymbol)?.ElementType,
			ArrayInitializationExpressionSyntax a => CheckArrayInitialization(a, scope),
			_ => null
		};
	}

	private TypeSymbol? ResolveType(string name)
	{
		if (name.StartsWith("refvar "))
		{
			var innerName = name.Substring(7); // "refvar " is 7 characters
			var innerType = ResolveType(innerName);
			if (innerType is null) return null;
			return new PointerTypeSymbol(innerType, isMutable: true); // Writable
		}

		if (name.StartsWith("ref "))
		{
			var innerName = name.Substring(4); // "ref " is 4 characters
			var innerType = ResolveType(innerName);
			if (innerType is null) return null;
			return new PointerTypeSymbol(innerType, isMutable: false); // Read-only
		}

		if (name.EndsWith(']'))
		{
			var openBracket = name.LastIndexOf('[');
			var sizePart = name.Substring(openBracket + 1, name.Length - openBracket - 2);
			var innerName = name.Substring(0, openBracket);
			var innerType = ResolveType(innerName);
			if (innerType is not null && int.TryParse(sizePart, out var size))
				return new ArrayTypeSymbol(innerType, size);
		}

		if (name.EndsWith("[]") && !name.StartsWith("ref"))
		{
			var inner = name[..^2];
			var innerType = ResolveType(inner);
			if (innerType is not null)
				return new StructTypeSymbol(name, []); // Represent slice as struct type for symbol checks
		}

		var primitive = TypeSymbol.FromName(name);
		if (primitive is not null) return primitive;

		if (_structTypes.TryGetValue(name, out var structType))
			return structType;

		return null;
	}

	private TypeSymbol? CheckStructInitializationExpression(StructInitializationExpressionSyntax expr, SymbolTable scope)
	{
		var type = ResolveType(expr.StructTypeName);
		if (type is null)
		{
			_diagnostics.Report(expr.Span, $"Unknown type '{expr.StructTypeName}'");
			return null;
		}

		if (type is not StructTypeSymbol structType)
		{
			_diagnostics.Report(expr.Span, $"Type '{expr.StructTypeName}' is not a struct type");
			return null;
		}

		var initializedFields = new HashSet<string>();
		foreach (var init in expr.Initializers)
		{
			if (!initializedFields.Add(init.MemberName))
			{
				_diagnostics.Report(init.Span, $"Duplicate initializer for field '{init.MemberName}'");
				continue;
			}

			var field = structType.FindField(init.MemberName);
			if (field is null)
			{
				_diagnostics.Report(init.Span, $"Struct '{structType.Name}' does not contain field '{init.MemberName}'");
				continue;
			}

			CheckExpression(init.Expression, scope);
			var initType = GetExpressionType(init.Expression, scope);
			if (initType is not null && !initType.Equals(field.Type))
			{
				_diagnostics.Report(init.Span, $"Cannot initialize field '{init.MemberName}' of type '{field.Type.Name}' with value of type '{initType.Name}'");
			}
		}

		foreach (var field in structType.Fields)
		{
			if (!initializedFields.Contains(field.Name))
			{
				_diagnostics.Report(expr.Span, $"Missing initializer for field '{field.Name}' of struct '{structType.Name}'");
			}
		}

		return structType;
	}

	private TypeSymbol? CheckBorrowExpression(BorrowExpressionSyntax expr, SymbolTable scope)
	{
		CheckExpression(expr.Expression, scope);
		var innerType = GetExpressionType(expr.Expression, scope);
		if (innerType is null) return null;

		var isMutable = false;
		if (expr.Expression is IdentifierExpressionSyntax id)
		{
			var symbol = scope.Lookup(id.Name) as VariableSymbol;
			if (symbol is not null) isMutable = symbol.IsMutable;
		}

		return new PointerTypeSymbol(innerType, isMutable);
	}

	private TypeSymbol? CheckArrayInitialization(ArrayInitializationExpressionSyntax expr, SymbolTable scope)
	{
		if (expr.Elements.Count == 0) return null; // Can't infer type of empty array easily yet

		var elementType = GetExpressionType(expr.Elements[0], scope) ?? TypeSymbol.Int;

		for (var i = 1; i < expr.Elements.Count; i++)
		{
			var elType = GetExpressionType(expr.Elements[i], scope);
			if (elType is not null && !elType.Equals(elementType))
			{
				_diagnostics.Report(expr.Elements[i].Span, $"Array elements must have the same type. Expected '{elementType.Name}', found '{elType.Name}'");
			}
		}

		return new ArrayTypeSymbol(elementType, expr.Elements.Count);
	}

	private void CheckReturnStatement(ReturnStatementSyntax ret, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
	{
		if (ret.Expression is null) return;

		CheckExpression(ret.Expression, scope);

		var returnType = ResolveType(currentFunc.ReturnType);
		if (returnType is PointerTypeSymbol)
		{
			// Case A: Returning an explicit borrow (e.g., return ref p)
			if (ret.Expression is BorrowExpressionSyntax borrow)
			{
				if (borrow.Expression is IdentifierExpressionSyntax id)
				{
					bool isParam = currentFunc.Parameters.Any(p => p.Name == id.Name);
					if (!isParam)
					{
						_diagnostics.Report(ret.Expression.Span, $"Cannot return reference to local variable '{id.Name}' (dangling reference)");
					}
				}
				else if (borrow.Expression is MemberAccessExpressionSyntax m)
				{
					var baseVarName = GetBaseIdentifierName(m.Expression);
					if (baseVarName is not null)
					{
						bool isParam = currentFunc.Parameters.Any(p => p.Name == baseVarName);
						if (!isParam)
						{
							_diagnostics.Report(ret.Expression.Span, $"Cannot return reference to field of local variable '{baseVarName}' (dangling reference)");
						}
					}
				}
			}
			// Case B: Returning a reference variable directly (e.g., return p)
			else if (ret.Expression is IdentifierExpressionSyntax id)
			{
				var symbol = scope.Lookup(id.Name) as VariableSymbol;
				if (symbol is not null && symbol.Type is PointerTypeSymbol)
				{
					// Block if the reference variable points to local stack space
					if (!symbol.PointsToParameter)
					{
						_diagnostics.Report(ret.Expression.Span, $"Cannot return reference variable '{id.Name}' because it points to local stack space (dangling reference)");
					}
				}
			}
		}
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		return null;
	}
}
