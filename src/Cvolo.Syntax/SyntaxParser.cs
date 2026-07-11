using System.Globalization;
using Antlr4.Runtime;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax;

public sealed class SyntaxParser
{
	private readonly DiagnosticBag _diagnostics = new();

	public DiagnosticBag Diagnostics => _diagnostics;

	public CompilationUnitSyntax? Parse(CompilationContext context)
	{
		var inputStream = new AntlrInputStream(context.Source);
		var lexer = new CvoloLexer(inputStream);
		var tokenStream = new CommonTokenStream(lexer);
		var parser = new CvoloParser(tokenStream);

		parser.RemoveErrorListeners();
		parser.AddErrorListener(new SyntaxErrorListener(_diagnostics, context));

		var tree = parser.compilationUnit();
		if (_diagnostics.HasErrors)
			return null;

		return BuildCompilationUnit(tree, context);
	}

	private CompilationUnitSyntax BuildCompilationUnit(CvoloParser.CompilationUnitContext context, CompilationContext compilationContext)
	{
		var usings = new List<UsingDirectiveSyntax>();
		foreach (var u in context.usingDirective())
			usings.Add(BuildUsingDirective(u));

		NamespaceDeclarationSyntax? nsDecl = null;
		if (context.namespaceDeclaration() is { } ns)
			nsDecl = BuildNamespaceDeclaration(ns);

		var members = new List<SyntaxNode>();
		if (nsDecl is null)
		{
			foreach (var decl in context.declaration())
			{
				var node = BuildDeclaration(decl);
				if (node is not null)
					members.Add(node);
			}
		}

		// Pass compilationContext as the second argument
		return new CompilationUnitSyntax(SpanOf(context), compilationContext, usings, nsDecl, members);
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

	private ExternDeclarationSyntax BuildExternDeclaration(CvoloParser.ExternDeclarationContext context)
	{
		var returnType = GetReturnTypeName(context.returnType());
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

	private FunctionDeclarationSyntax BuildFunctionDeclaration(CvoloParser.FunctionDeclarationContext context)
	{
		var returnType = GetReturnTypeName(context.returnType());
		var name = context.Identifier().GetText();

		// Parse optional generic parameters list (which can now contain concrete types like <int>)
		var generics = new List<string>();
		if (context.typeList() is { } typeListCtx)
		{
			foreach (var t in typeListCtx.type())
				generics.Add(GetTypeName(t));
		}

		var parameters = new List<ParameterSyntax>();
		if (context.parameterList() is { } paramList)
		{
			foreach (var param in paramList.parameter())
				parameters.Add(BuildParameter(param));
		}

		var body = BuildBlockStatement(context.blockStatement());
		return new FunctionDeclarationSyntax(SpanOf(context), returnType, name, generics, parameters, body);
	}

	private StructDeclarationSyntax BuildStructDeclaration(CvoloParser.StructDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		// Parse optional generic parameters list: <T>
		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var fields = new List<StructFieldSyntax>();
		foreach (var field in context.structField())
			fields.Add(new StructFieldSyntax(SpanOf(field), GetTypeName(field.type()), field.Identifier().GetText()));
		return new StructDeclarationSyntax(SpanOf(context), name, generics, fields);
	}

	private ParameterSyntax BuildParameter(CvoloParser.ParameterContext context)
	{
		var type = GetTypeName(context.type());
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
		{
			var expr = BuildExpression(exprStmt.expression());

			// Intercept console write calls containing interpolated strings to lower them at compile-time
			if (expr is CallExpressionSyntax call &&
				(call.FunctionName == "Console.WriteLine" || call.FunctionName == "Console.Write" ||
				 call.FunctionName == "System.Console.WriteLine" || call.FunctionName == "System.Console.Write") &&
				call.Arguments.Count == 1 &&
				call.Arguments[0] is InterpolatedStringExpressionSyntax interpolated)
			{
				return LowerInterpolatedConsoleCall(call, interpolated);
			}

			return new ExpressionStatementSyntax(SpanOf(exprStmt), expr);
		}
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
			case CvoloParser.InterpolatedStringExpressionContext interCtx:
				return new InterpolatedStringExpressionSyntax(SpanOf(interCtx), interCtx.InterpolatedStringLiteral().GetText());
			case CvoloParser.CharLiteralExpressionContext charCtx:
				{
					var text = charCtx.CharLiteral().GetText();
					var val = text[1..^1]; // Strip single quotes
					if (val.StartsWith("\\"))
					{
						val = val switch
						{
							"\\n" => "\n",
							"\\t" => "\t",
							"\\r" => "\r",
							"\\'" => "'",
							"\\\\" => "\\",
							"\\0" => "\0",
							_ => val[1..]
						};
					}

					return new CharacterLiteralExpressionSyntax(SpanOf(charCtx), val[0]);
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
			case CvoloParser.ParenthesizedStructInitializerContext parenCtx:
				{
					var initializers = new List<MemberInitializerSyntax>();
					if (parenCtx.structInitializerList() is { } listCtx)
					{
						foreach (var memberInit in listCtx.structMemberInitializer())
						{
							var memberName = memberInit.Identifier().GetText();
							var expr = BuildExpression(memberInit.expression());
							initializers.Add(new MemberInitializerSyntax(SpanOf(memberInit), memberName, expr));
						}
					}

					return new ParenthesizedStructInitializerExpressionSyntax(SpanOf(parenCtx), initializers);
				}
			case CvoloParser.CallExpressionContext callCtx:
				{
					var funcName = callCtx.qualifiedName().GetText();

					// (The rest of this block remains exactly the same!)
					var typeArgs = new List<string>();
					if (callCtx.typeList() is { } typeListCtx)
					{
						foreach (var t in typeListCtx.type())
							typeArgs.Add(GetTypeName(t));
					}

					var args = new List<ExpressionSyntax>();
					if (callCtx.argumentList() is { } argList)
					{
						foreach (var arg in argList.expression())
							args.Add(BuildExpression(arg));
					}

					return new CallExpressionSyntax(SpanOf(callCtx), funcName, typeArgs, args);
				}

			case CvoloParser.UnaryMinusExpressionContext unaryMinus:
				return new UnaryExpressionSyntax(SpanOf(unaryMinus), "-", BuildExpression(unaryMinus.expression()));
			case CvoloParser.LogicalNotExpressionContext notCtx:
				return new UnaryExpressionSyntax(SpanOf(notCtx), "!", BuildExpression(notCtx.expression()));
			case CvoloParser.CastExpressionContext castCtx:
				{
					var inner = BuildExpression(castCtx.expression());
					return new UnaryExpressionSyntax(SpanOf(castCtx), $"({GetTypeName(castCtx.type())})", inner);
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
			case CvoloParser.CompoundAssignmentExpressionContext compoundCtx:
				{
					var left = BuildExpression(compoundCtx.expression(0));
					var opAssign = compoundCtx.GetChild(1).GetText(); // e.g. "+="
					var right = BuildExpression(compoundCtx.expression(1));

					// Desugar: extract the math operator (remove the '=')
					var op = opAssign[..^1]; // e.g., "+=" -> "+"

					// Create the math sub-expression: left + right
					var mathExpr = new BinaryExpressionSyntax(SpanOf(compoundCtx), left, op, right);

					// Return the assignment: left = (left + right)
					return new BinaryExpressionSyntax(SpanOf(compoundCtx), left, "=", mathExpr);
				}
			case CvoloParser.PostfixIncrementExpressionContext incCtx:
				return new UnaryExpressionSyntax(SpanOf(incCtx), "++_postfix", BuildExpression(incCtx.expression()));
			case CvoloParser.PostfixDecrementExpressionContext decCtx:
				return new UnaryExpressionSyntax(SpanOf(decCtx), "--_postfix", BuildExpression(decCtx.expression()));
			case CvoloParser.BitwiseNotExpressionContext notCtx:
				return new UnaryExpressionSyntax(SpanOf(notCtx), "~", BuildExpression(notCtx.expression()));
			case CvoloParser.ShiftExpressionContext shiftCtx:
				return new BinaryExpressionSyntax(SpanOf(shiftCtx), BuildExpression(shiftCtx.expression(0)), shiftCtx.GetChild(1).GetText(), BuildExpression(shiftCtx.expression(1)));
			case CvoloParser.BitwiseAndExpressionContext andCtx:
				return new BinaryExpressionSyntax(SpanOf(andCtx), BuildExpression(andCtx.expression(0)), "&", BuildExpression(andCtx.expression(1)));
			case CvoloParser.BitwiseXorExpressionContext xorCtx:
				return new BinaryExpressionSyntax(SpanOf(xorCtx), BuildExpression(xorCtx.expression(0)), "^", BuildExpression(xorCtx.expression(1)));
			case CvoloParser.BitwiseOrExpressionContext orCtx:
				return new BinaryExpressionSyntax(SpanOf(orCtx), BuildExpression(orCtx.expression(0)), "|", BuildExpression(orCtx.expression(1)));
			case CvoloParser.PrefixIncrementExpressionContext preIncCtx:
				return new UnaryExpressionSyntax(SpanOf(preIncCtx), "++_prefix", BuildExpression(preIncCtx.expression()));
			case CvoloParser.PrefixDecrementExpressionContext preDecCtx:
				return new UnaryExpressionSyntax(SpanOf(preDecCtx), "--_prefix", BuildExpression(preDecCtx.expression()));
			case CvoloParser.MemberAccessExpressionContext memberCtx:
				{
					var left = BuildExpression(memberCtx.expression());
					var memberName = memberCtx.Identifier().GetText();
					return new MemberAccessExpressionSyntax(SpanOf(memberCtx), left, memberName);
				}
			case CvoloParser.BorrowExpressionContext borrowCtx:
				{
					var expr = BuildExpression(borrowCtx.expression());
					return new BorrowExpressionSyntax(SpanOf(borrowCtx), expr);
				}
			case CvoloParser.StructInitializationExpressionContext structInitCtx:
				{
					var structName = structInitCtx.Identifier().GetText();

					// Reconstruct the full generic type name (e.g. Point<int>) if type arguments are present
					if (structInitCtx.typeList() is { } typeListCtx)
					{
						var args = typeListCtx.type().Select(GetTypeName);
						structName = $"{structName}<{string.Join(", ", args)}>";
					}

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
			case CvoloParser.HeapAllocationExpressionContext heapCtx:
				return new HeapAllocationExpressionSyntax(SpanOf(heapCtx), BuildExpression(heapCtx.expression()));
			case CvoloParser.HeapArrayAllocationExpressionContext heapArrCtx:
				{
					var typeName = GetTypeName(heapArrCtx.type());
					var countExpr = BuildExpression(heapArrCtx.expression());
					return new HeapArrayAllocationExpressionSyntax(SpanOf(heapArrCtx), typeName, countExpr);
				}
			case CvoloParser.IndexExpressionContext idxCtx:
				return new IndexExpressionSyntax(SpanOf(idxCtx), BuildExpression(idxCtx.expression(0)), BuildExpression(idxCtx.expression(1)));
			case CvoloParser.ArrayInitializationExpressionContext arrInitCtx:
				{
					var elements = new List<ExpressionSyntax>();
					foreach (var exprCtx in arrInitCtx.expression())
					{
						elements.Add(BuildExpression(exprCtx));
					}

					return new ArrayInitializationExpressionSyntax(SpanOf(arrInitCtx), elements);
				}
			case CvoloParser.TernaryExpressionContext ternaryCtx:
				{
					var cond = BuildExpression(ternaryCtx.expression(0));
					var thenExpr = BuildExpression(ternaryCtx.expression(1));
					var elseExpr = BuildExpression(ternaryCtx.expression(2));
					return new TernaryExpressionSyntax(SpanOf(ternaryCtx), cond, thenExpr, elseExpr);
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
		if (context.LPAREN() is not null)
		{
			var isMutable = context.VAR() is not null;
			var typeCtx = context.type();
			var baseTypeName = GetTypeName(typeCtx);
			var name = context.Identifier().GetText();

			// Resolve counts
			ExpressionSyntax countExpr;
			var elementTypeName = baseTypeName;
			if (typeCtx is CvoloParser.ArrayTypeContext arrCtx)
			{
				var countVal = int.Parse(arrCtx.IntegerLiteral().GetText());
				countExpr = new IntegerLiteralExpressionSyntax(SpanOf(arrCtx), countVal);
				elementTypeName = GetTypeName(arrCtx.type());
			}
			else
			{
				countExpr = new IntegerLiteralExpressionSyntax(SpanOf(typeCtx), 0);
			}

			// Resolve the value expression (either structural constructor or primitive expression)
			ExpressionSyntax valueExpr;
			if (context.structInitializerList() is { } listCtx)
			{
				var initializers = new List<MemberInitializerSyntax>();
				foreach (var memberInit in listCtx.structMemberInitializer())
				{
					var memberName = memberInit.Identifier().GetText();
					var fieldExpr = BuildExpression(memberInit.expression());
					initializers.Add(new MemberInitializerSyntax(SpanOf(memberInit), memberName, fieldExpr));
				}

				valueExpr = new StructInitializationExpressionSyntax(SpanOf(context), elementTypeName, initializers);
			}
			else
			{
				valueExpr = BuildExpression(context.expression());
			}

			var implicitInitializer = new ArrayReplicationExpressionSyntax(SpanOf(context), valueExpr, countExpr);
			return new VariableDeclarationSyntax(SpanOf(context), isMutable, baseTypeName, name, implicitInitializer);
		}

		// Standard variable declaration
		if (context.REFVAR() is not null)
		{
			var refVarName = context.Identifier().GetText();
			var refVarExpr = BuildExpression(context.expression());
			return new VariableDeclarationSyntax(SpanOf(context), isMutable: true, "refvar", refVarName, refVarExpr);
		}

		if (context.REF() is not null)
		{
			var refName = context.Identifier().GetText();
			var refExpr = BuildExpression(context.expression());
			return new VariableDeclarationSyntax(SpanOf(context), isMutable: false, "ref", refName, refExpr);
		}

		var isVarMutable = context.VAR() is not null;
		var type = context.type() is not null ? GetTypeName(context.type()) : null;
		var standardName = context.Identifier().GetText();

		ExpressionSyntax? initializer = null;
		if (context.expression() is { } expr)
			initializer = BuildExpression(expr);

		return new VariableDeclarationSyntax(SpanOf(context), isVarMutable, type, standardName, initializer);
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

	private string GetTypeName(CvoloParser.TypeContext context)
	{
		if (context is CvoloParser.RefVarTypeContext refVarCtx)
		{
			return $"refvar {GetTypeName(refVarCtx.type())}";
		}

		if (context is CvoloParser.ReadOnlyRefTypeContext refCtx)
		{
			return $"ref {GetTypeName(refCtx.type())}";
		}

		if (context is CvoloParser.ArrayTypeContext arrCtx)
		{
			return $"{GetTypeName(arrCtx.type())}[{arrCtx.IntegerLiteral().GetText()}]";
		}

		if (context is CvoloParser.SliceTypeContext sliceCtx)
		{
			return $"{GetTypeName(sliceCtx.type())}[]";
		}

		if (context is CvoloParser.QualifiedTypeContext qualCtx)
		{
			return qualCtx.qualifiedName().GetText();
		}

		if (context is CvoloParser.GenericInstantiationTypeContext genInstCtx)
		{
			var innerTypeName = GetTypeName(genInstCtx.type());
			var args = genInstCtx.typeList().type().Select(GetTypeName);
			return $"{innerTypeName}<{string.Join(", ", args)}>";
		}

		return context.GetText();
	}

	private string GetReturnTypeName(CvoloParser.ReturnTypeContext context)
	{
		if (context.VOID() is not null) return "void";
		return GetTypeName(context.type());
	}

	private sealed class SyntaxErrorListener(DiagnosticBag diagnostics, CompilationContext context) : BaseErrorListener
	{
		public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
		{
			var span = new TextSpan(offendingSymbol?.StartIndex ?? 0, offendingSymbol?.Text?.Length ?? 0);

			diagnostics.Report(context, span, $"({line},{charPositionInLine}): {msg}");
		}
	}

	public static (int Line, int Column) GetLineAndColumn(string text, int position)
	{
		var line = 1;
		var column = 1;
		for (var i = 0; i < position && i < text.Length; i++)
		{
			if (text[i] == '\n')
			{
				line++;
				column = 1;
			}
			else
			{
				column++;
			}
		}

		return (line, column);
	}

	private UsingDirectiveSyntax BuildUsingDirective(CvoloParser.UsingDirectiveContext context)
	{
		return new UsingDirectiveSyntax(SpanOf(context), context.qualifiedName().GetText());
	}

	private NamespaceDeclarationSyntax BuildNamespaceDeclaration(CvoloParser.NamespaceDeclarationContext context)
	{
		var name = context.qualifiedName().GetText();
		var usings = new List<UsingDirectiveSyntax>();
		foreach (var u in context.usingDirective())
			usings.Add(BuildUsingDirective(u));

		var members = new List<SyntaxNode>();
		foreach (var decl in context.declaration())
		{
			var node = BuildDeclaration(decl);
			if (node is not null)
				members.Add(node);
		}

		return new NamespaceDeclarationSyntax(SpanOf(context), name, usings, members);
	}

	private SyntaxNode LowerInterpolatedConsoleCall(CallExpressionSyntax originalCall, InterpolatedStringExpressionSyntax interpolated)
	{
		var segments = ParseInterpolatedString(interpolated.RawText);
		var statementList = new List<SyntaxNode>();
		var isWriteLine = originalCall.FunctionName.EndsWith("WriteLine");

		// Resolve safe base write target paths
		var writeTarget = originalCall.FunctionName.Replace("WriteLine", "Write");
		var writeLineTarget = originalCall.FunctionName;

		for (var i = 0; i < segments.Count; i++)
		{
			var (text, isExpression) = segments[i];
			ExpressionSyntax elementExpr;

			if (isExpression)
			{
				elementExpr = ParseExpressionSegment(text, originalCall.Span);
			}
			else
			{
				elementExpr = new StringLiteralExpressionSyntax(originalCall.Span, text);
			}

			// The very last segment uses WriteLine if the original call was WriteLine
			var targetFunc = (i == segments.Count - 1 && isWriteLine) ? writeLineTarget : writeTarget;
			var callNode = new CallExpressionSyntax(originalCall.Span, targetFunc, [], [elementExpr]);

			statementList.Add(new ExpressionStatementSyntax(originalCall.Span, callNode));
		}

		return new BlockStatementSyntax(originalCall.Span, statementList);
	}

	private List<(string text, bool isExpression)> ParseInterpolatedString(string raw)
	{
		// Strip $" and the trailing "
		var content = raw.Substring(2, raw.Length - 3);
		var list = new List<(string text, bool isExpression)>();

		var i = 0;
		var start = 0;
		while (i < content.Length)
		{
			if (content[i] == '{')
			{
				// Escape double brace match {{
				if (i + 1 < content.Length && content[i + 1] == '{')
				{
					i += 2;
					continue;
				}

				if (i > start)
				{
					list.Add((content.Substring(start, i - start), false));
				}

				var depth = 1;
				var exprStart = i + 1;
				i++;
				while (i < content.Length && depth > 0)
				{
					if (content[i] == '{') depth++;
					else if (content[i] == '}') depth--;
					i++;
				}

				var exprText = content.Substring(exprStart, i - exprStart - 1);
				list.Add((exprText, true));
				start = i;
			}
			else if (content[i] == '}')
			{
				// Escape double brace match }}
				if (i + 1 < content.Length && content[i + 1] == '}')
				{
					i += 2;
					continue;
				}
				i++;
			}
			else
			{
				i++;
			}
		}

		if (start < content.Length)
		{
			list.Add((content.Substring(start), false));
		}

		return list;
	}

	private ExpressionSyntax ParseExpressionSegment(string source, TextSpan parentSpan)
	{
		// Wrap the expression segment inside a dummy method body to parse cleanly
		var dummySource = $"void dummy() {{ {source}; }}";
		var segmentContext = new CompilationContext(dummySource, "interpolated_segment");

		var inputStream = new AntlrInputStream(segmentContext.Source);
		var lexer = new CvoloLexer(inputStream);
		var tokenStream = new CommonTokenStream(lexer);
		var parser = new CvoloParser(tokenStream);

		parser.RemoveErrorListeners();
		var tree = parser.compilationUnit();

		var decl = tree.declaration(0);
		var func = decl.functionDeclaration();
		var block = func.blockStatement();
		var stmt = block.statement(0);
		var exprStmt = stmt.expressionStatement();

		return BuildExpression(exprStmt.expression());
	}
}
