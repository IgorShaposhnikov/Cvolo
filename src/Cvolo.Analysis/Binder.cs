using Cvolo.Analysis.@struct;
using Cvolo.Core;

namespace Cvolo.Analysis;

public sealed class Binder
{
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
                localScope.Declare(new VariableSymbol(param.Name, paramType, isMutable: false));
        }

        CheckBlock(func.Body, localScope, func);
    }

    private void CheckBlock(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
    {
        foreach (var stmt in block.Statements)
        {
            CheckStatement(stmt, scope, currentFunc);
        }
    }

    private void CheckStatement(SyntaxNode stmt, SymbolTable scope, FunctionDeclarationSyntax currentFunc)
    {
        switch (stmt)
        {
            case ReturnStatementSyntax ret:
                if (ret.Expression is not null)
                    CheckExpression(ret.Expression, scope);
                break;
            case ExpressionStatementSyntax exprStmt:
                CheckExpression(exprStmt.Expression, scope);
                break;
            case VariableDeclarationSyntax varDecl:
                CheckVariableDeclaration(varDecl, scope);
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
                CheckVariableDeclaration(forStmt.Initializer, forScope);
                CheckExpression(forStmt.Condition, forScope);
                CheckExpression(forStmt.Increment, forScope);
                CheckStatement(forStmt.Body, forScope, currentFunc);
                break;
            }
        }
    }

    private void CheckVariableDeclaration(VariableDeclarationSyntax varDecl, SymbolTable scope)
    {
        var existing = scope.Lookup(varDecl.Name);
        if (existing is not null)
        {
            _diagnostics.Report(varDecl.Span, $"Variable '{varDecl.Name}' is already declared in this scope");
            return;
        }

        if (varDecl.Initializer is not null)
            CheckExpression(varDecl.Initializer, scope);

        var resolvedType = varDecl.Type is not null ? ResolveType(varDecl.Type) : TypeSymbol.Int;
        if (resolvedType is null)
        {
            _diagnostics.Report(varDecl.Span, $"Unknown type '{varDecl.Type}'");
            return;
        }

        scope.Declare(new VariableSymbol(varDecl.Name, resolvedType, varDecl.IsMutable));
    }

    private void CheckExpression(ExpressionSyntax expr, SymbolTable scope)
    {
        switch (expr)
        {
            case IdentifierExpressionSyntax id:
            {
                var symbol = scope.Lookup(id.Name);
                if (symbol is null)
                    _diagnostics.Report(id.Span, $"Undefined variable '{id.Name}'");
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

                foreach (var arg in call.Arguments)
                    CheckExpression(arg, scope);
                break;
            }
            case BinaryExpressionSyntax bin:
                CheckExpression(bin.Left, scope);
                CheckExpression(bin.Right, scope);
                break;
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
            _ => null
        };
    }

    private TypeSymbol? ResolveType(string name)
    {
        if (name.StartsWith("ref "))
        {
            var parts = name.Split(' ', 3);
            var isMutable = parts[1] == "var";
            var innerType = ResolveType(parts[2]);
            if (innerType is null) return null;
            return new PointerTypeSymbol(innerType, isMutable);
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
}
