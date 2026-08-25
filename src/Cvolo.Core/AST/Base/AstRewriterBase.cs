using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Core.AST.Base;

public abstract class AstRewriterBase
{
	public virtual SyntaxNode Rewrite(SyntaxNode node)
	{
		if (node is CompilationUnitSyntax compilationUnit)
		{
			var rewrittenMembers = compilationUnit.Members.Select(Rewrite).ToList();
			return new CompilationUnitSyntax(
				compilationUnit.Span,
				compilationUnit.Context,
				compilationUnit.Usings,
				compilationUnit.NamespaceDeclaration != null ? (NamespaceDeclarationSyntax)Rewrite(compilationUnit.NamespaceDeclaration) : null,
				rewrittenMembers
			);
		}

		if (node is NamespaceDeclarationSyntax ns)
		{
			var rewrittenMembers = ns.Members.Select(Rewrite).ToList();
			return new NamespaceDeclarationSyntax(ns.Span, ns.Name, ns.Usings, rewrittenMembers);
		}

		if (node is ExtensionDeclarationSyntax extDecl)
		{
			var rewrittenMethods = extDecl.Methods.Select(Rewrite).Cast<FunctionDeclarationSyntax>().ToList();
			var rewrittenDestructors = extDecl.Destructors.Select(Rewrite).Cast<DestructorDeclarationSyntax>().ToList();
			return new ExtensionDeclarationSyntax(extDecl.Span, extDecl.ExtendedTypeName, rewrittenMethods, rewrittenDestructors);
		}

		if (node is FunctionDeclarationSyntax func)
		{
			var rewrittenBody = (BlockStatementSyntax)Rewrite(func.Body);
			return new FunctionDeclarationSyntax(func.Span, func.ReturnType, func.Name, func.GenericParameters, func.Parameters, rewrittenBody);
		}

		if (node is DestructorDeclarationSyntax dtor)
		{
			var rewrittenDtorBody = (BlockStatementSyntax)Rewrite(dtor.Body);
			return new DestructorDeclarationSyntax(dtor.Span, dtor.StructName, rewrittenDtorBody);
		}

		if (node is BlockStatementSyntax block)
		{
			var rewrittenStatements = block.Statements.Select(Rewrite).ToList();
			return new BlockStatementSyntax(block.Span, rewrittenStatements);
		}

		if (node is ExpressionStatementSyntax exprStmt)
		{
			var rewrittenExpr = (ExpressionSyntax)Rewrite(exprStmt.Expression);
			return new ExpressionStatementSyntax(exprStmt.Span, rewrittenExpr);
		}

		if (node is ReturnStatementSyntax ret)
		{
			var rewrittenExpr = ret.Expression != null ? (ExpressionSyntax)Rewrite(ret.Expression) : null;
			return new ReturnStatementSyntax(ret.Span, rewrittenExpr);
		}

		if (node is IfStatementSyntax ifStmt)
		{
			var cond = (ExpressionSyntax)Rewrite(ifStmt.Condition);
			var then = Rewrite(ifStmt.ThenStatement);
			var elseClause = ifStmt.ElseClause != null ? new ElseClauseSyntax(ifStmt.ElseClause.Span, (BlockStatementSyntax)Rewrite(ifStmt.ElseClause.Body)) : null;
			return new IfStatementSyntax(ifStmt.Span, cond, then, elseClause);
		}

		if (node is WhileStatementSyntax whileStmt)
		{
			var cond = (ExpressionSyntax)Rewrite(whileStmt.Condition);
			var body = Rewrite(whileStmt.Body);
			return new WhileStatementSyntax(whileStmt.Span, cond, body);
		}

		if (node is ForStatementSyntax forStmt)
		{
			var init = (VariableDeclarationSyntax)Rewrite(forStmt.Initializer);
			var cond = (ExpressionSyntax)Rewrite(forStmt.Condition);
			var inc = (ExpressionSyntax)Rewrite(forStmt.Increment);
			var body = Rewrite(forStmt.Body);
			return new ForStatementSyntax(forStmt.Span, init, cond, inc, body);
		}

		if (node is VariableDeclarationSyntax varDecl)
		{
			var init = varDecl.Initializer != null ? (ExpressionSyntax)Rewrite(varDecl.Initializer) : null;
			return new VariableDeclarationSyntax(varDecl.Span, varDecl.IsMutable, varDecl.Type, varDecl.Name, init);
		}

		if (node is CallExpressionSyntax call)
		{
			var rewrittenArgs = call.Arguments.Select(Rewrite).Cast<ExpressionSyntax>().ToList();
			return new CallExpressionSyntax(call.Span, call.FunctionName, call.TypeArguments, rewrittenArgs);
		}

		if (node is BinaryExpressionSyntax bin)
		{
			var left = (ExpressionSyntax)Rewrite(bin.Left);
			var right = (ExpressionSyntax)Rewrite(bin.Right);
			return new BinaryExpressionSyntax(bin.Span, left, bin.Operator, right);
		}

		if (node is UnaryExpressionSyntax unary)
		{
			var operand = (ExpressionSyntax)Rewrite(unary.Operand);
			return new UnaryExpressionSyntax(unary.Span, unary.Operator, operand);
		}

		if (node is TernaryExpressionSyntax ternary)
		{
			var cond = (ExpressionSyntax)Rewrite(ternary.Condition);
			var then = (ExpressionSyntax)Rewrite(ternary.ThenExpression);
			var elseExpr = (ExpressionSyntax)Rewrite(ternary.ElseExpression);
			return new TernaryExpressionSyntax(ternary.Span, cond, then, elseExpr);
		}

		return node;
	}
}
