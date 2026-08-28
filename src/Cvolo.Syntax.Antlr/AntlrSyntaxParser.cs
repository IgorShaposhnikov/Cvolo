using System.Globalization;
using Antlr4.Runtime;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax;

namespace Cvolo.Syntax.Antlr;

public sealed class AntlrSyntaxParser : ISyntaxParser
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
		if (context.unionDeclaration() is { } unionDecl)
			return BuildUnionDeclaration(unionDecl);
		if (context.extensionDeclaration() is { } extensionDecl)
			return BuildExtensionDeclaration(extensionDecl);
		if (context.interfaceDeclaration() is { } interfaceDecl)
			return BuildInterfaceDeclaration(interfaceDecl);
		if (context.globalVariableDeclaration() is { } globalDecl)
			return BuildGlobalVariableDeclaration(globalDecl);
		return null;
	}

	private GlobalVariableDeclarationSyntax BuildGlobalVariableDeclaration(CvoloParser.GlobalVariableDeclarationContext context)
	{
		var isMutable = context.VAR() is not null;
		var type = GetTypeName(context.type());
		var name = context.Identifier().GetText();
		ExpressionSyntax? initializer = null;
		if (context.expression() is { } exprCtx)
			initializer = BuildExpression(exprCtx);
		return new GlobalVariableDeclarationSyntax(SpanOf(context), type, name, initializer, isMutable);
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
		var attributes = BuildAttributeList(context.attributeList());

		// Extract optional function modifier (unsafe / unbound)
		SafetyTier? modifier = null;
		if (context.functionModifier() is { } modCtx)
			modifier = modCtx.GetText() == "unsafe" ? SafetyTier.Unsafe : SafetyTier.Unbound;

		return new FunctionDeclarationSyntax(SpanOf(context), returnType, name, generics, parameters, body, attributes, modifier);
	}

	private List<AttributeSyntax> BuildAttributeList(IEnumerable<CvoloParser.AttributeListContext> contexts)
	{
		var attributes = new List<AttributeSyntax>();
		foreach (var listCtx in contexts)
		{
			foreach (var attrCtx in listCtx.attribute())
			{
				var args = new List<ExpressionSyntax>();
				if (attrCtx.argumentList() is { } argList)
				{
					foreach (var arg in argList.expression())
						args.Add(BuildExpression(arg));
				}

				attributes.Add(new AttributeSyntax(SpanOf(attrCtx), attrCtx.qualifiedName().GetText(), args));
			}
		}

		return attributes;
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
		var structAttributes = BuildAttributeList(context.attributeList());
		return new StructDeclarationSyntax(SpanOf(context), name, generics, fields, structAttributes);
	}

	private ParameterSyntax BuildParameter(CvoloParser.ParameterContext context)
	{
		var type = GetTypeName(context.type());
		var name = context.Identifier().GetText();
		var attributes = BuildAttributeList(context.attributeList());
		return new ParameterSyntax(SpanOf(context), type, name, attributes);
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
			return new ExpressionStatementSyntax(SpanOf(exprStmt), expr);
		}
		if (context.variableDeclaration() is { } varDecl)
			return BuildVariableDeclaration(varDecl);
		if (context.blockStatement() is { } block)
			return BuildBlockStatement(block);
		if (context.ifStatement() is { } ifStmt)
			return BuildIfStatement(ifStmt);
		if (context.switchStatement() is { } sw)
			return BuildSwitchStatement(sw);
		if (context.whileStatement() is { } whileStmt)
			return BuildWhileStatement(whileStmt);
		if (context.forStatement() is { } forStmt)
			return BuildForStatement(forStmt);
		if (context.unsafeBlockStatement() is { } unsafeBlock)
			return new UnsafeBlockStatementSyntax(SpanOf(unsafeBlock), BuildBlockStatement(unsafeBlock.blockStatement()));
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
			case CvoloParser.NullLiteralExpressionContext nullCtx:
				return new NullLiteralExpressionSyntax(SpanOf(nullCtx));
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
			case CvoloParser.ArrowMemberAccessExpressionContext arrowCtx:
				{
					var left = BuildExpression(arrowCtx.expression());
					var memberName = arrowCtx.Identifier().GetText();
					return new MemberAccessExpressionSyntax(SpanOf(arrowCtx), left, memberName, "->");
				}
			case CvoloParser.DereferenceExpressionContext derefCtx:
				return new UnaryExpressionSyntax(SpanOf(derefCtx), "*", BuildExpression(derefCtx.expression()));
			case CvoloParser.AddressOfExpressionContext addrCtx:
				return new UnaryExpressionSyntax(SpanOf(addrCtx), "&", BuildExpression(addrCtx.expression()));
			case CvoloParser.BorrowExpressionContext borrowCtx:
				{
					var expr = BuildExpression(borrowCtx.expression());
					var isMutable = borrowCtx.GetText().StartsWith("refvar");
					return new BorrowExpressionSyntax(SpanOf(borrowCtx), expr, isMutable);
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
			case CvoloParser.VoidLiteralExpressionContext voidCtx:
				return new VoidLiteralExpressionSyntax(SpanOf(voidCtx));
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

				// Determine correct element type name for target-typing
				var elementTypeName = typeCtx is CvoloParser.ArrayTypeContext arrCtx ? GetTypeName(arrCtx.type()) : baseTypeName;
				valueExpr = new StructInitializationExpressionSyntax(SpanOf(context), elementTypeName, initializers);
			}
			else
			{
				valueExpr = BuildExpression(context.expression());
			}

			// Check if it is a REPLICATED ARRAY or a SCALAR STRUCT initializer
			if (typeCtx is CvoloParser.ArrayTypeContext arrayCtx)
			{
				var countVal = int.Parse(arrayCtx.IntegerLiteral().GetText());
				var countExpr = new IntegerLiteralExpressionSyntax(SpanOf(arrayCtx), countVal);
				var implicitInitializer = new ArrayReplicationExpressionSyntax(SpanOf(context), valueExpr, countExpr);
				return new VariableDeclarationSyntax(SpanOf(context), isMutable, baseTypeName, name, implicitInitializer);
			}
			else
			{
				// Scalar Struct Initialization: var Point p(X: 10, Y: 20);
				return new VariableDeclarationSyntax(SpanOf(context), isMutable, baseTypeName, name, valueExpr);
			}
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

		if (context is CvoloParser.PointerTypeContext ptrCtx)
		{
			return $"{GetTypeName(ptrCtx.type())}*";
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

	private ExtensionDeclarationSyntax BuildExtensionDeclaration(CvoloParser.ExtensionDeclarationContext context)
	{
		var extendedTypeName = context.Identifier().GetText();
		var methods = new List<FunctionDeclarationSyntax>();
		foreach (var funcCtx in context.functionDeclaration())
		{
			methods.Add(BuildFunctionDeclaration(funcCtx));
		}

		var destructors = new List<DestructorDeclarationSyntax>();
		foreach (var dtorCtx in context.destructorDeclaration())
		{
			var dtorAttributes = BuildAttributeList(dtorCtx.attributeList());
			destructors.Add(new DestructorDeclarationSyntax(SpanOf(dtorCtx), dtorCtx.Identifier().GetText(), BuildBlockStatement(dtorCtx.blockStatement()), dtorAttributes));
		}

		var constructors = new List<ConstructorDeclarationSyntax>();
		foreach (var ctorCtx in context.constructorDeclaration())
		{
			var ctorParams = new List<ParameterSyntax>();
			if (ctorCtx.parameterList() is { } paramListCtx)
			{
				foreach (var param in paramListCtx.parameter())
					ctorParams.Add(BuildParameter(param));
			}

			var ctorAttributes = BuildAttributeList(ctorCtx.attributeList());
			constructors.Add(new ConstructorDeclarationSyntax(SpanOf(ctorCtx), ctorCtx.Identifier().GetText(), ctorParams, BuildBlockStatement(ctorCtx.blockStatement()), ctorAttributes));
		}

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		string? conformsTo = null;
		if (context.qualifiedName() is { } conformsCtx)
			conformsTo = conformsCtx.GetText();

		return new ExtensionDeclarationSyntax(SpanOf(context), extendedTypeName, methods, destructors, constructors, generics, conformsTo);
	}

	private InterfaceDeclarationSyntax BuildInterfaceDeclaration(CvoloParser.InterfaceDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var members = new List<InterfaceMethodDeclarationSyntax>();
		foreach (var memberCtx in context.interfaceMember())
		{
			var returnType = GetReturnTypeName(memberCtx.returnType());
			var memberName = memberCtx.Identifier().GetText();

			var parameters = new List<ParameterSyntax>();
			if (memberCtx.parameterList() is { } paramListCtx)
			{
				foreach (var param in paramListCtx.parameter())
					parameters.Add(BuildParameter(param));
			}

			members.Add(new InterfaceMethodDeclarationSyntax(SpanOf(memberCtx), returnType, memberName, parameters));
		}

		var attributes = BuildAttributeList(context.attributeList());
		return new InterfaceDeclarationSyntax(SpanOf(context), name, generics, members, attributes);
	}

	private UnionDeclarationSyntax BuildUnionDeclaration(CvoloParser.UnionDeclarationContext context)
	{
		var name = context.Identifier().GetText();

		var generics = new List<string>();
		if (context.genericParameterList() is { } genList)
		{
			foreach (var id in genList.Identifier())
				generics.Add(id.GetText());
		}

		var fields = new List<UnionFieldSyntax>();
		foreach (var field in context.unionField())
			fields.Add(new UnionFieldSyntax(SpanOf(field), GetTypeName(field.type()), field.Identifier().GetText()));

		var unionAttributes = BuildAttributeList(context.attributeList());
		return new UnionDeclarationSyntax(SpanOf(context), name, generics, fields, unionAttributes);
	}

	private SyntaxNode BuildSwitchStatement(CvoloParser.SwitchStatementContext context)
	{
		var expr = BuildExpression(context.expression());
		var cases = new List<SwitchCaseSyntax>();
		foreach (var caseCtx in context.switchCase())
		{
			if (caseCtx.DEFAULT() is not null)
			{
				var body = new List<SyntaxNode>();
				foreach (var stmt in caseCtx.statement())
				{
					var stmtNode = BuildStatement(stmt);
					if (stmtNode is not null) body.Add(stmtNode);
				}
				cases.Add(new SwitchCaseSyntax(SpanOf(caseCtx), "", null, isDefault: true, body));
			}
			else
			{
				var patternCtx = caseCtx.pattern();
				string variantName = "";
				string? variableName = null;

				if (patternCtx is CvoloParser.VariantPatternContext varPat)
				{
					variantName = varPat.Identifier(0).GetText();
					variableName = varPat.Identifier(1).GetText();
				}
				else if (patternCtx is CvoloParser.ConstantPatternContext constPat)
				{
					variantName = constPat.Identifier().GetText();
				}

				var body = new List<SyntaxNode>();
				foreach (var stmt in caseCtx.statement())
				{
					var stmtNode = BuildStatement(stmt);
					if (stmtNode is not null) body.Add(stmtNode);
				}

				cases.Add(new SwitchCaseSyntax(SpanOf(caseCtx), variantName, variableName, isDefault: false, body));
			}
		}

		return new SwitchStatementSyntax(SpanOf(context), expr, cases);
	}
}
