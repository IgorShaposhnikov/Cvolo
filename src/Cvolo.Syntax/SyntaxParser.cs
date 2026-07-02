using System.Globalization;
using Antlr4.Runtime;
using Cvolo.Core;

namespace Cvolo.Syntax;

public sealed class SyntaxParser
{
    private readonly DiagnosticBag _diagnostics = new();

    public DiagnosticBag Diagnostics => _diagnostics;

    public CompilationUnitSyntax? Parse(string sourceCode)
    {
        var inputStream = new AntlrInputStream(sourceCode);
        var lexer = new CvoloLexer(inputStream);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new CvoloParser(tokenStream);

        parser.RemoveErrorListeners();
        parser.AddErrorListener(new SyntaxErrorListener(_diagnostics));

        var tree = parser.compilationUnit();
        if (_diagnostics.HasErrors)
            return null;

        return BuildCompilationUnit(tree);
    }

    private CompilationUnitSyntax BuildCompilationUnit(CvoloParser.CompilationUnitContext context)
    {
        var members = new List<SyntaxNode>();
        foreach (var decl in context.declaration())
        {
            var node = BuildDeclaration(decl);
            if (node is not null)
                members.Add(node);
        }

        var span = SpanOf(context);
        return new CompilationUnitSyntax(span, members);
    }

    private SyntaxNode? BuildDeclaration(CvoloParser.DeclarationContext context)
    {
        if (context.functionDeclaration() is { } func)
            return BuildFunctionDeclaration(func);
        if (context.externDeclaration() is { } ext)
            return BuildExternDeclaration(ext);
        if (context.structDeclaration() is { } structDecl)
            return BuildStructDeclaration(structDecl);
        return null;
    }

    private FunctionDeclarationSyntax BuildFunctionDeclaration(CvoloParser.FunctionDeclarationContext context)
    {
        var returnType = context.returnType().GetText();
        var name = context.Identifier().GetText();
        var parameters = new List<ParameterSyntax>();
        if (context.parameterList() is { } paramList)
        {
            foreach (var param in paramList.parameter())
                parameters.Add(BuildParameter(param));
        }

        var body = BuildBlockStatement(context.blockStatement());
        return new FunctionDeclarationSyntax(SpanOf(context), returnType, name, parameters, body);
    }

    private ExternDeclarationSyntax BuildExternDeclaration(CvoloParser.ExternDeclarationContext context)
    {
        var returnType = context.returnType().GetText();
        var name = context.Identifier().GetText();
        var parameters = new List<ParameterSyntax>();
        var isVariadic = false;
        if (context.externParameterList() is { } paramList)
        {
            foreach (var param in paramList.externParameter())
            {
                if (param.ELLIPSIS() is not null)
                    isVariadic = true;
                else
                    parameters.Add(new ParameterSyntax(SpanOf(param), param.type().GetText(), param.Identifier().GetText()));
            }
        }

        return new ExternDeclarationSyntax(SpanOf(context), returnType, name, parameters, isVariadic);
    }

    private StructDeclarationSyntax BuildStructDeclaration(CvoloParser.StructDeclarationContext context)
    {
        var name = context.Identifier().GetText();
        var fields = new List<StructFieldSyntax>();
        foreach (var field in context.structField())
            fields.Add(new StructFieldSyntax(SpanOf(field), field.type().GetText(), field.Identifier().GetText()));
        return new StructDeclarationSyntax(SpanOf(context), name, fields);
    }

    private ParameterSyntax BuildParameter(CvoloParser.ParameterContext context)
    {
        var type = context.type().GetText();
        var name = context.Identifier().GetText();
        return new ParameterSyntax(SpanOf(context), type, name);
    }

    private BlockStatementSyntax BuildBlockStatement(CvoloParser.BlockStatementContext context)
    {
        var statements = new List<SyntaxNode>();
        foreach (var stmt in context.statement())
        {
            var node = BuildStatement(stmt);
            if (node is not null)
                statements.Add(node);
        }

        return new BlockStatementSyntax(SpanOf(context), statements);
    }

    private SyntaxNode? BuildStatement(CvoloParser.StatementContext context)
    {
        if (context.returnStatement() is { } ret)
            return BuildReturnStatement(ret);
        if (context.expressionStatement() is { } exprStmt)
            return BuildExpressionStatement(exprStmt);
        if (context.variableDeclaration() is { } varDecl)
            return BuildVariableDeclaration(varDecl);
        if (context.blockStatement() is { } block)
            return BuildBlockStatement(block);
        if (context.ifStatement() is { } ifStmt)
            return BuildIfStatement(ifStmt);
        if (context.whileStatement() is { } whileStmt)
            return BuildWhileStatement(whileStmt);
        if (context.forStatement() is { } forStmt)
            return BuildForStatement(forStmt);
        return null;
    }

    private ReturnStatementSyntax BuildReturnStatement(CvoloParser.ReturnStatementContext context)
    {
        ExpressionSyntax? expr = null;
        if (context.expression() is { } exprCtx)
            expr = BuildExpression(exprCtx);
        return new ReturnStatementSyntax(SpanOf(context), expr);
    }

    private ExpressionStatementSyntax BuildExpressionStatement(CvoloParser.ExpressionStatementContext context)
    {
        var expr = BuildExpression(context.expression());
        return new ExpressionStatementSyntax(SpanOf(context), expr);
    }

    private ExpressionSyntax BuildExpression(CvoloParser.ExpressionContext context)
    {
        switch (context)
        {
            case CvoloParser.StringLiteralExpressionContext strCtx:
                {
                    var raw = strCtx.StringLiteral().GetText();

                    // Safely strip quotes only if they are present
                    if (raw.StartsWith("\\\"") && raw.EndsWith("\\\""))
                    {
                        // Slice off the first 2 characters and the last 2 characters
                        raw = raw[2..^2];
                    }

                    var value = DecodeString(raw);
                    return new StringLiteralExpressionSyntax(SpanOf(strCtx), value);
                }

            case CvoloParser.IntegerLiteralExpressionContext intCtx:
                return new IntegerLiteralExpressionSyntax(SpanOf(intCtx), int.Parse(intCtx.IntegerLiteral().GetText(), CultureInfo.InvariantCulture));
            case CvoloParser.DoubleLiteralExpressionContext dblCtx:
                return new DoubleLiteralExpressionSyntax(SpanOf(dblCtx), double.Parse(dblCtx.DoubleLiteral().GetText(), CultureInfo.InvariantCulture));
            case CvoloParser.BooleanLiteralExpressionContext boolCtx:
                return new BooleanLiteralExpressionSyntax(SpanOf(boolCtx), boolCtx.TRUE() is not null);
            case CvoloParser.IdentifierExpressionContext idCtx:
                return new IdentifierExpressionSyntax(SpanOf(idCtx), idCtx.Identifier().GetText());
            case CvoloParser.ParenthesizedExpressionContext parenCtx:
                return BuildExpression(parenCtx.expression());
            case CvoloParser.CallExpressionContext callCtx:
                {
                    var funcName = callCtx.Identifier().GetText();
                    var args = new List<ExpressionSyntax>();
                    if (callCtx.argumentList() is { } argList)
                    {
                        foreach (var arg in argList.expression())
                            args.Add(BuildExpression(arg));
                    }

                    return new CallExpressionSyntax(SpanOf(callCtx), funcName, args);
                }

            case CvoloParser.UnaryMinusExpressionContext unaryMinus:
                return new UnaryExpressionSyntax(SpanOf(unaryMinus), "-", BuildExpression(unaryMinus.expression()));
            case CvoloParser.LogicalNotExpressionContext notCtx:
                return new UnaryExpressionSyntax(SpanOf(notCtx), "!", BuildExpression(notCtx.expression()));
            case CvoloParser.CastExpressionContext castCtx:
                {
                    var inner = BuildExpression(castCtx.expression());
                    return new UnaryExpressionSyntax(SpanOf(castCtx), $"({castCtx.type().GetText()})", inner);
                }

            case CvoloParser.MultiplicativeExpressionContext multCtx:
                return new BinaryExpressionSyntax(SpanOf(multCtx), BuildExpression(multCtx.expression(0)), multCtx.GetChild(1).GetText(), BuildExpression(multCtx.expression(1)));
            case CvoloParser.AdditiveExpressionContext addCtx:
                return new BinaryExpressionSyntax(SpanOf(addCtx), BuildExpression(addCtx.expression(0)), addCtx.GetChild(1).GetText(), BuildExpression(addCtx.expression(1)));
            case CvoloParser.RelationalExpressionContext relCtx:
                return new BinaryExpressionSyntax(SpanOf(relCtx), BuildExpression(relCtx.expression(0)), relCtx.GetChild(1).GetText(), BuildExpression(relCtx.expression(1)));
            case CvoloParser.EqualityExpressionContext eqCtx:
                return new BinaryExpressionSyntax(SpanOf(eqCtx), BuildExpression(eqCtx.expression(0)), eqCtx.GetChild(1).GetText(), BuildExpression(eqCtx.expression(1)));
            case CvoloParser.LogicalAndExpressionContext andCtx:
                return new BinaryExpressionSyntax(SpanOf(andCtx), BuildExpression(andCtx.expression(0)), "&&", BuildExpression(andCtx.expression(1)));
            case CvoloParser.LogicalOrExpressionContext orCtx:
                return new BinaryExpressionSyntax(SpanOf(orCtx), BuildExpression(orCtx.expression(0)), "||", BuildExpression(orCtx.expression(1)));
            case CvoloParser.AssignmentExpressionContext assignCtx:
                return new BinaryExpressionSyntax(SpanOf(assignCtx), BuildExpression(assignCtx.expression(0)), "=", BuildExpression(assignCtx.expression(1)));
            case CvoloParser.MemberAccessExpressionContext memberCtx:
                {
                    var left = BuildExpression(memberCtx.expression());
                    var memberName = memberCtx.Identifier().GetText();
                    return new MemberAccessExpressionSyntax(SpanOf(memberCtx), left, memberName);
                }
            case CvoloParser.StructInitializationExpressionContext structInitCtx:
                {
                    var structName = structInitCtx.Identifier().GetText();
                    var initializers = new List<MemberInitializerSyntax>();
                    if (structInitCtx.structInitializerList() is { } listCtx)
                    {
                        foreach (var memberInit in listCtx.structMemberInitializer())
                        {
                            var memberName = memberInit.Identifier().GetText();
                            var expr = BuildExpression(memberInit.expression());
                            initializers.Add(new MemberInitializerSyntax(SpanOf(memberInit), memberName, expr));
                        }
                    }

                    return new StructInitializationExpressionSyntax(SpanOf(structInitCtx), structName, initializers);
                }
            default:
                return new IdentifierExpressionSyntax(SpanOf(context), context.GetText());
        }

        string DecodeString(string literal)
        {
            return literal[1..^1]
                .Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\0", "\0");
        }
    }

    private VariableDeclarationSyntax BuildVariableDeclaration(CvoloParser.VariableDeclarationContext context)
    {
        var isMutable = context.VAL() is null;
        var type = context.type()?.GetText();
        var name = context.Identifier().GetText();
        ExpressionSyntax? initializer = null;
        if (context.expression() is { } expr)
            initializer = BuildExpression(expr);
        return new VariableDeclarationSyntax(SpanOf(context), isMutable, type, name, initializer);
    }

    private IfStatementSyntax BuildIfStatement(CvoloParser.IfStatementContext context)
    {
        var condition = BuildExpression(context.expression());
        var thenStmt = BuildStatement(context.statement(0))!;
        ElseClauseSyntax? elseClause = null;
        if (context.ELSE() is not null && context.statement(1) is { } elseBody)
        {
            var elseStmt = BuildStatement(elseBody)!;
            var elseBlock = elseStmt as BlockStatementSyntax ?? new BlockStatementSyntax(SpanOf(elseBody), [elseStmt]);
            elseClause = new ElseClauseSyntax(SpanOf(elseBody), elseBlock);
        }

        return new IfStatementSyntax(SpanOf(context), condition, thenStmt, elseClause);
    }

    private WhileStatementSyntax BuildWhileStatement(CvoloParser.WhileStatementContext context)
    {
        var condition = BuildExpression(context.expression());
        var body = BuildStatement(context.statement())!;
        return new WhileStatementSyntax(SpanOf(context), condition, body);
    }

    private ForStatementSyntax BuildForStatement(CvoloParser.ForStatementContext context)
    {
        var init = BuildVariableDeclaration(context.variableDeclaration());
        var condition = BuildExpression(context.expression(0));
        var increment = BuildExpression(context.expression(1));
        var body = BuildStatement(context.statement())!;
        return new ForStatementSyntax(SpanOf(context), init, condition, increment, body);
    }

    private static TextSpan SpanOf(Antlr4.Runtime.ParserRuleContext context)
    {
        if (context is null || context.Start is null)
            return new TextSpan(0, 0);
        var start = context.Start.StartIndex;
        var end = context.Stop?.StopIndex + 1 ?? start;
        return TextSpan.FromBounds(start, end);
    }

    private sealed class SyntaxErrorListener(DiagnosticBag diagnostics) : BaseErrorListener
    {
        public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
        {
            var span = new TextSpan(offendingSymbol?.StartIndex ?? 0, offendingSymbol?.Text?.Length ?? 0);
            diagnostics.Report(span, $"({line},{charPositionInLine}): {msg}");
        }
    }
}
