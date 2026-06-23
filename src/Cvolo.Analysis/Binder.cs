using Cvolo.Core;

namespace Cvolo.Analysis;

public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly SymbolTable _globals = new();

    public DiagnosticBag Diagnostics => _diagnostics;

    public void Bind(CompilationUnitSyntax unit)
    {
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

    private static TypeSymbol? ResolveType(string name)
    {
        return TypeSymbol.FromName(name);
    }
}
