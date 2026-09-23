using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Syntax.Rewriters;

/// <summary>
/// Source-level expansion of array/slice <c>foreach</c> loops into the cached-length index
/// <c>for</c> loop the LLVM emitter would otherwise synthesize (EmitForEachArray /
/// EmitForEachSlice). Runs after binding so the binder-stamped
/// <see cref="ForEachStatementSyntax.EnumeratorTypeName"/> and
/// <see cref="ForEachStatementSyntax.ArraySize"/> fields distinguish arrays/slices from
/// enumerator-based iterators:
/// — static arrays bound the loop with the compile-time element count (a literal);
/// — slices/heap arrays bound it with the cached <c>collection.Length</c>;
/// — enumerator-based foreach is kept as-is (stamps preserved) for the emitter to expand.
/// </summary>
/// <remarks>
/// IMPORTANT: this pass runs POST-binding, so it must never rebuild expression nodes —
/// the emitter's <c>_bindingContext.ResolvedCalls</c> is keyed by CallExpressionSyntax
/// instance. Only statement/declaration containers and the foreach node itself are rebuilt;
/// every expression is passed through unchanged.
/// </remarks>
public sealed class ForeachLoweringRewriter : AstRewriterBase
{
	private int _counter;

	public override SyntaxNode Rewrite(SyntaxNode node)
	{
		if (node is ForEachStatementSyntax forEach && forEach.EnumeratorTypeName is null)
		{
			return LowerForeach(forEach);
		}

		switch (node)
		{
			case CompilationUnitSyntax compilationUnit:
				{
					var members = compilationUnit.Members.Select(Rewrite).ToList();
					var ns = compilationUnit.NamespaceDeclaration != null
						? (NamespaceDeclarationSyntax)Rewrite(compilationUnit.NamespaceDeclaration)
						: null;
					return new CompilationUnitSyntax(compilationUnit.Span, compilationUnit.Context, compilationUnit.Usings, ns, members);
				}

			case NamespaceDeclarationSyntax ns:
				{
					var members = ns.Members.Select(Rewrite).ToList();
					return new NamespaceDeclarationSyntax(ns.Span, ns.Name, ns.Usings, members);
				}

			case ExtensionDeclarationSyntax ext:
				{
					var methods = ext.Methods.Select(Rewrite).Cast<FunctionDeclarationSyntax>().ToList();
					var destructors = ext.Destructors.Select(Rewrite).Cast<DestructorDeclarationSyntax>().ToList();
					var constructors = ext.Constructors.Select(Rewrite).Cast<ConstructorDeclarationSyntax>().ToList();
					return new ExtensionDeclarationSyntax(ext.Span, ext.ExtendedTypeName, methods, destructors, constructors, ext.GenericParameters, ext.ConformsTo, ext.Visibility);
				}

			case FunctionDeclarationSyntax func:
				{
					var body = func.Body != null ? (BlockStatementSyntax)Rewrite(func.Body) : null;
					return new FunctionDeclarationSyntax(func.Span, func.ReturnType, func.Name, func.GenericParameters, func.Parameters, body, func.Attributes, func.Modifier, func.Receiver, func.Visibility, callingConvention: func.CallingConvention);
				}

			case DestructorDeclarationSyntax dtor:
				{
					var body = (BlockStatementSyntax)Rewrite(dtor.Body);
					return new DestructorDeclarationSyntax(dtor.Span, dtor.StructName, body, dtor.Attributes, dtor.Visibility);
				}

			case ConstructorDeclarationSyntax ctor:
				{
					var body = (BlockStatementSyntax)Rewrite(ctor.Body);
					return new ConstructorDeclarationSyntax(ctor.Span, ctor.StructName, ctor.Parameters, body, ctor.ConstructorArguments, ctor.ConstructorInitializerSpan, ctor.Attributes, ctor.SyntacticVisibility);
				}

			case BlockStatementSyntax block:
				{
					var statements = block.Statements.Select(Rewrite).ToList();
					return new BlockStatementSyntax(block.Span, statements);
				}

			case LabeledBlockStatementSyntax labeled:
				{
					var body = (BlockStatementSyntax)Rewrite(labeled.Body);
					return new LabeledBlockStatementSyntax(labeled.Span, labeled.Label, body);
				}

			case ForEachStatementSyntax fe:
				{
					var body = (BlockStatementSyntax)Rewrite(fe.Body);
					var rebuilt = new ForEachStatementSyntax(fe.Span, fe.BindingKind, fe.ExplicitItemType, fe.ItemName, fe.Collection, body, fe.Label)
					{
						ItemTypeName = fe.ItemTypeName,
						ItemBindingTypeName = fe.ItemBindingTypeName,
						IsReferenceBinding = fe.IsReferenceBinding,
						CurrentReturnsReference = fe.CurrentReturnsReference,
						GetEnumeratorFunctionName = fe.GetEnumeratorFunctionName,
						MoveNextFunctionName = fe.MoveNextFunctionName,
						CurrentFunctionName = fe.CurrentFunctionName,
						EnumeratorTypeName = fe.EnumeratorTypeName,
						ArraySize = fe.ArraySize
					};
					return rebuilt;
				}

			case IfStatementSyntax ifStmt:
				{
					var elseClause = ifStmt.ElseClause != null
						? new ElseClauseSyntax(ifStmt.ElseClause.Span, (BlockStatementSyntax)Rewrite(ifStmt.ElseClause.Body))
						: null;
					return new IfStatementSyntax(ifStmt.Span, ifStmt.Condition, Rewrite(ifStmt.ThenStatement), elseClause);
				}

			case WhileStatementSyntax whileStmt:
				{
					var body = Rewrite(whileStmt.Body);
					return new WhileStatementSyntax(whileStmt.Span, whileStmt.Condition, body, whileStmt.Label);
				}

			case ForStatementSyntax forStmt:
				{
					var body = Rewrite(forStmt.Body);
					return new ForStatementSyntax(forStmt.Span, forStmt.Initializer, forStmt.Condition, forStmt.Increment, body, forStmt.Label);
				}

			case UnsafeBlockStatementSyntax unsafeBlock:
				{
					var body = (BlockStatementSyntax)Rewrite(unsafeBlock.Body);
					return new UnsafeBlockStatementSyntax(unsafeBlock.Span, body);
				}

			case SwitchStatementSyntax sw:
				{
					var cases = sw.Cases
						.Select(c => new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault, c.Body.Select(Rewrite).ToList()))
						.ToList();
					return new SwitchStatementSyntax(sw.Span, sw.Expression, cases);
				}

			default:
				return node;
		}
	}

	private SyntaxNode LowerForeach(ForEachStatementSyntax forEach)
	{
		var span = forEach.Span;
		var indexName = $"__fe_i{_counter}";
		var lengthName = $"__fe_len{_counter}";
		_counter++;

		var indexId = new IdentifierExpressionSyntax(span, indexName);
		var lengthId = new IdentifierExpressionSyntax(span, lengthName);
		var zero = new IntegerLiteralExpressionSyntax(span, 0);
		var one = new IntegerLiteralExpressionSyntax(span, 1);

		var indexedElement = new IndexExpressionSyntax(span, forEach.Collection, indexId);

		var isRefBinding = forEach.BindingKind == ForEachVariableKind.RefVar;
		var itemDecl = new VariableDeclarationSyntax(
			span,
			isMutable: forEach.BindingKind == ForEachVariableKind.Var,
			type: isRefBinding ? "refvar" : forEach.ExplicitItemType,
			name: forEach.ItemName,
			initializer: isRefBinding
				? new BorrowExpressionSyntax(span, indexedElement, isMutable: true)
				: indexedElement);

		var bodyStatements = new List<SyntaxNode> { itemDecl };
		bodyStatements.AddRange(forEach.Body.Statements.Select(Rewrite));

		ExpressionSyntax lengthExpr = forEach.ArraySize is int arraySize
			? new IntegerLiteralExpressionSyntax(span, (ulong)arraySize)
			: new MemberAccessExpressionSyntax(span, forEach.Collection, "Length");

		var lengthDecl = new VariableDeclarationSyntax(span, isMutable: false, "int", lengthName, lengthExpr);
		var forLoop = new ForStatementSyntax(
			span,
			new VariableDeclarationSyntax(span, isMutable: true, "int", indexName, zero),
			new BinaryExpressionSyntax(span, indexId, "<", lengthId),
			new BinaryExpressionSyntax(span, indexId, "=",
				new BinaryExpressionSyntax(span, indexId, "+", one)),
			new BlockStatementSyntax(span, bodyStatements),
			forEach.Label);

		return new BlockStatementSyntax(span, new List<SyntaxNode> { lengthDecl, forLoop });
	}
}
