using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax.Rewriters;

public sealed class StringInterpolationRewriter : AstRewriterBase
{
	public override SyntaxNode Rewrite(SyntaxNode node)
	{
		if (node is ExpressionStatementSyntax exprStmt &&
			exprStmt.Expression is CallExpressionSyntax call &&
			(call.FunctionName == "Console.WriteLine" || call.FunctionName == "Console.Write" ||
			 call.FunctionName == "System.Console.WriteLine" || call.FunctionName == "System.Console.Write") &&
			call.Arguments.Count == 1 &&
			call.Arguments[0] is InterpolatedStringExpressionSyntax interpolated)
		{
			return LowerInterpolatedConsoleCall(call, interpolated);
		}

		return base.Rewrite(node);
	}

	private SyntaxNode LowerInterpolatedConsoleCall(CallExpressionSyntax originalCall, InterpolatedStringExpressionSyntax interpolated)
	{
		var segments = ParseInterpolatedString(interpolated.RawText);
		var statementList = new List<SyntaxNode>();
		var isWriteLine = originalCall.FunctionName.EndsWith("WriteLine");

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

			var targetFunc = (i == segments.Count - 1 && isWriteLine) ? writeLineTarget : writeTarget;
			var callNode = new CallExpressionSyntax(originalCall.Span, targetFunc, [], [elementExpr]);

			statementList.Add(new ExpressionStatementSyntax(originalCall.Span, callNode));
		}

		return new BlockStatementSyntax(originalCall.Span, statementList);
	}

	private List<(string text, bool isExpression)> ParseInterpolatedString(string raw)
	{
		var content = raw.Substring(2, raw.Length - 3);
		var list = new List<(string text, bool isExpression)>();

		var i = 0;
		var start = 0;
		while (i < content.Length)
		{
			if (content[i] == '{')
			{
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

		// Use the public SyntaxParser API recursively!
		var parser = new SyntaxParser();
		var unit = parser.Parse(segmentContext);
		if (unit == null)
			throw new InvalidOperationException($"Failed to parse interpolated expression segment: {source}");

		// Unpack: compilationUnit -> declaration (FunctionDeclaration) -> body -> statement (ExpressionStatement) -> expression
		var func = unit.Members[0] as FunctionDeclarationSyntax;
		var stmt = func!.Body.Statements[0] as ExpressionStatementSyntax;

		// Recursively rewrite the parsed expression so nested interpolation is fully supported
		return (ExpressionSyntax)Rewrite(stmt!.Expression);
	}
}
