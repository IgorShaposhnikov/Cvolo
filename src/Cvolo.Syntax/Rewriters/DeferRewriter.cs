using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax.Rewriters;

public sealed class DeferRewriter : AstRewriterBase
{
	public override SyntaxNode Rewrite(SyntaxNode node)
	{
		if (node is BlockStatementSyntax block)
			return RewriteBlock(block);
		return base.Rewrite(node);
	}

	private BlockStatementSyntax RewriteBlock(BlockStatementSyntax block)
	{
		// Positional, per-block registrations: a block's own defers run at any exit
		// of that block (returns at any nesting depth + natural fall-through), LIFO.
		var registered = new List<SyntaxNode>();
		var result = new List<SyntaxNode>();

		foreach (var rawStmt in block.Statements)
		{
			var stmt = Rewrite(rawStmt);

			if (stmt is DeferStatementSyntax defer)
			{
				// Lower the body first so nested defers/interpolations are processed.
				registered.Add(Rewrite(defer.Body));
				continue;
			}

			// A direct return in this block: splice LIFO defers immediately before it
			// (not wrapped), so a trailing return still ends the block.
			if (registered.Count > 0 && stmt is ReturnStatementSyntax ret)
			{
				var defers = registered.ToList();
				defers.Reverse();
				foreach (var d in defers)
					result.Add(CloneNode(d));
				result.Add(ret);
				continue;
			}

			result.Add(InjectDefers(stmt, registered));
		}

		// Natural fall-through: run this block's own defers in reverse order.
		if (registered.Count > 0 && (result.Count == 0 || result[^1] is not ReturnStatementSyntax))
		{
			var defers = registered.ToList();
			defers.Reverse();
			result.Add(new BlockStatementSyntax(block.Span, defers.Select(CloneNode).ToList()));
		}

		return new BlockStatementSyntax(block.Span, result);
	}

	private SyntaxNode InjectDefers(SyntaxNode stmt, IReadOnlyList<SyntaxNode> registered)
	{
		if (registered.Count == 0)
			return stmt;

		var defers = registered.ToList();
		defers.Reverse();
		return InjectWithDefers(stmt, defers);
	}

	private SyntaxNode InjectWithDefers(SyntaxNode stmt, IReadOnlyList<SyntaxNode> defers)
	{
		return stmt switch
		{
			ReturnStatementSyntax ret => new BlockStatementSyntax(ret.Span, [.. defers.Select(CloneNode), ret]),
			IfStatementSyntax ifStmt => InjectIntoIf(ifStmt, defers),
			WhileStatementSyntax whileStmt => InjectIntoWhile(whileStmt, defers),
			ForStatementSyntax forStmt => InjectIntoFor(forStmt, defers),
			UnsafeBlockStatementSyntax unsafeBlock => InjectIntoUnsafe(unsafeBlock, defers),
			SwitchStatementSyntax sw => InjectIntoSwitch(sw, defers),
			BlockStatementSyntax nestedBlock => InjectIntoNestedBlock(nestedBlock, defers),
			_ => stmt,
		};
	}

	private IfStatementSyntax InjectIntoIf(IfStatementSyntax ifStmt, IReadOnlyList<SyntaxNode> defers)
	{
		var then = InjectBlockOrSingle(ifStmt.ThenStatement, defers);
		var elseClause = ifStmt.ElseClause != null
			? new ElseClauseSyntax(ifStmt.ElseClause.Span, InjectBlockOrSingle(ifStmt.ElseClause.Body, defers))
			: null;
		return new IfStatementSyntax(ifStmt.Span, ifStmt.Condition, then, elseClause);
	}

	private BlockStatementSyntax InjectBlockOrSingle(SyntaxNode branch, IReadOnlyList<SyntaxNode> defers)
	{
		if (branch is BlockStatementSyntax block)
		{
			var stmts = block.Statements.Select(s => InjectWithDefers(s, defers)).ToList();
			return new BlockStatementSyntax(block.Span, stmts);
		}
		return new BlockStatementSyntax(branch.Span, [.. defers.Select(CloneNode), branch]);
	}

	private WhileStatementSyntax InjectIntoWhile(WhileStatementSyntax whileStmt, IReadOnlyList<SyntaxNode> defers)
	{
		var body = InjectWithDefers(whileStmt.Body, defers);
		return new WhileStatementSyntax(whileStmt.Span, whileStmt.Condition, body);
	}

	private ForStatementSyntax InjectIntoFor(ForStatementSyntax forStmt, IReadOnlyList<SyntaxNode> defers)
	{
		var body = InjectWithDefers(forStmt.Body, defers);
		return new ForStatementSyntax(forStmt.Span, forStmt.Initializer, forStmt.Condition, forStmt.Increment, body);
	}

	private UnsafeBlockStatementSyntax InjectIntoUnsafe(UnsafeBlockStatementSyntax unsafeBlock, IReadOnlyList<SyntaxNode> defers)
	{
		var stmts = unsafeBlock.Body.Statements.Select(s => InjectWithDefers(s, defers)).ToList();
		var newBody = new BlockStatementSyntax(unsafeBlock.Body.Span, stmts);
		return new UnsafeBlockStatementSyntax(unsafeBlock.Span, newBody);
	}

	private SwitchStatementSyntax InjectIntoSwitch(SwitchStatementSyntax sw, IReadOnlyList<SyntaxNode> defers)
	{
		var newCases = sw.Cases.Select(c =>
		{
			var stmts = c.Body.Select(s => InjectWithDefers(s, defers)).ToList();
			return new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault, stmts);
		}).ToList();
		return new SwitchStatementSyntax(sw.Span, sw.Expression, newCases);
	}

	// Descends into a nested block for returns only: the nested block's own
	// fall-through tail (already appended by its own RewriteBlock) must NOT
	// receive the enclosing block's defers.
	private BlockStatementSyntax InjectIntoNestedBlock(BlockStatementSyntax nestedBlock, IReadOnlyList<SyntaxNode> defers)
	{
		var stmts = nestedBlock.Statements.Select(s => InjectWithDefers(s, defers)).ToList();
		return new BlockStatementSyntax(nestedBlock.Span, stmts);
	}

	private SyntaxNode CloneNode(SyntaxNode node)
	{
		return node switch
		{
			ExpressionStatementSyntax exprStmt => new ExpressionStatementSyntax(exprStmt.Span, CloneExpression(exprStmt.Expression)),
			ReturnStatementSyntax ret => new ReturnStatementSyntax(ret.Span, ret.Expression != null ? CloneExpression(ret.Expression) : null),
			BlockStatementSyntax block => new BlockStatementSyntax(block.Span, block.Statements.Select(CloneNode).ToList()),
			IfStatementSyntax ifStmt => CloneIf(ifStmt),
			WhileStatementSyntax whileStmt => new WhileStatementSyntax(whileStmt.Span, CloneExpression(whileStmt.Condition), CloneNode(whileStmt.Body)),
			ForStatementSyntax forStmt => new ForStatementSyntax(forStmt.Span, CloneVarDecl(forStmt.Initializer), CloneExpression(forStmt.Condition), CloneExpression(forStmt.Increment), CloneNode(forStmt.Body)),
			UnsafeBlockStatementSyntax unsafeBlock => new UnsafeBlockStatementSyntax(unsafeBlock.Span, (BlockStatementSyntax)CloneNode(unsafeBlock.Body)),
			SwitchStatementSyntax sw => CloneSwitch(sw),
			VariableDeclarationSyntax varDecl => CloneVarDecl(varDecl),
			_ => node,
		};
	}

	private IfStatementSyntax CloneIf(IfStatementSyntax ifStmt)
	{
		var then = CloneNode(ifStmt.ThenStatement);
		var elseClause = ifStmt.ElseClause != null
			? new ElseClauseSyntax(ifStmt.ElseClause.Span, (BlockStatementSyntax)CloneNode(ifStmt.ElseClause.Body))
			: null;
		return new IfStatementSyntax(ifStmt.Span, CloneExpression(ifStmt.Condition), then, elseClause);
	}

	private SwitchStatementSyntax CloneSwitch(SwitchStatementSyntax sw)
	{
		var cases = sw.Cases.Select(c =>
		{
			var stmts = c.Body.Select(CloneNode).ToList();
			return new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault, stmts);
		}).ToList();
		return new SwitchStatementSyntax(sw.Span, sw.Expression, cases);
	}

	private VariableDeclarationSyntax CloneVarDecl(VariableDeclarationSyntax varDecl)
	{
		return new VariableDeclarationSyntax(varDecl.Span, varDecl.IsMutable, varDecl.Type, varDecl.Name,
			varDecl.Initializer != null ? CloneExpression(varDecl.Initializer) : null);
	}

	private MemberInitializerSyntax CloneMemberInit(MemberInitializerSyntax init)
	{
		return new MemberInitializerSyntax(init.Span, init.MemberName, CloneExpression(init.Expression));
	}

	private ExpressionSyntax CloneExpression(ExpressionSyntax expr)
	{
		return expr switch
		{
			CallExpressionSyntax call => new CallExpressionSyntax(call.Span, call.FunctionName, call.TypeArguments,
				call.Arguments.Select(CloneExpression).Cast<ExpressionSyntax>().ToList()),
			BinaryExpressionSyntax bin => new BinaryExpressionSyntax(bin.Span, CloneExpression(bin.Left), bin.Operator, CloneExpression(bin.Right)),
			UnaryExpressionSyntax unary => new UnaryExpressionSyntax(unary.Span, unary.Operator, CloneExpression(unary.Operand)),
			IntegerLiteralExpressionSyntax lit => new IntegerLiteralExpressionSyntax(lit.Span, lit.Value, lit.LiteralType),
			DoubleLiteralExpressionSyntax lit => new DoubleLiteralExpressionSyntax(lit.Span, lit.Value, lit.IsFloat),
			BooleanLiteralExpressionSyntax lit => new BooleanLiteralExpressionSyntax(lit.Span, lit.Value),
			StringLiteralExpressionSyntax lit => new StringLiteralExpressionSyntax(lit.Span, lit.Value),
			CharacterLiteralExpressionSyntax lit => new CharacterLiteralExpressionSyntax(lit.Span, lit.Value),
			NullLiteralExpressionSyntax lit => new NullLiteralExpressionSyntax(lit.Span),
			VoidLiteralExpressionSyntax lit => new VoidLiteralExpressionSyntax(lit.Span),
			IdentifierExpressionSyntax id => new IdentifierExpressionSyntax(id.Span, id.Name),
			MemberAccessExpressionSyntax member => new MemberAccessExpressionSyntax(member.Span, CloneExpression(member.Expression), member.MemberName),
			IndexExpressionSyntax index => new IndexExpressionSyntax(index.Span, CloneExpression(index.Left), CloneExpression(index.Index)),
			BorrowExpressionSyntax borrow => new BorrowExpressionSyntax(borrow.Span, CloneExpression(borrow.Expression), borrow.IsMutable),
			HeapAllocationExpressionSyntax heap => new HeapAllocationExpressionSyntax(heap.Span, CloneExpression(heap.Expression)),
			HeapArrayAllocationExpressionSyntax heapArr => new HeapArrayAllocationExpressionSyntax(heapArr.Span, heapArr.ElementTypeName, CloneExpression(heapArr.CountExpression)),
			ArrayInitializationExpressionSyntax arrInit => new ArrayInitializationExpressionSyntax(arrInit.Span, arrInit.Elements.Select(CloneExpression).Cast<ExpressionSyntax>().ToList()),
			ArrayReplicationExpressionSyntax arrRep => new ArrayReplicationExpressionSyntax(arrRep.Span, CloneExpression(arrRep.Value), CloneExpression(arrRep.Count)),
			StructInitializationExpressionSyntax structInit => new StructInitializationExpressionSyntax(structInit.Span, structInit.StructTypeName, structInit.Initializers.Select(CloneMemberInit).ToList()),
			ParenthesizedStructInitializerExpressionSyntax parenInit => new ParenthesizedStructInitializerExpressionSyntax(parenInit.Span, parenInit.Initializers.Select(CloneMemberInit).ToList()),
			TernaryExpressionSyntax ternary => new TernaryExpressionSyntax(ternary.Span, CloneExpression(ternary.Condition), CloneExpression(ternary.ThenExpression), CloneExpression(ternary.ElseExpression)),
			InterpolatedStringExpressionSyntax interp => new InterpolatedStringExpressionSyntax(interp.Span, interp.RawText),
			IsPatternExpressionSyntax isPat => new IsPatternExpressionSyntax(isPat.Span, CloneExpression(isPat.Operand), isPat.VariantName, isPat.BoundName),
			DefaultExpressionSyntax def => new DefaultExpressionSyntax(def.Span, def.TypeName),
			_ => expr,
		};
	}
}
