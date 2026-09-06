using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Core.AST;

public sealed class CvoloSourcePrinter
{
	public static string Print(SyntaxNode node, int indent = 0)
	{
		var ind = new string(' ', indent * 4);
		switch (node)
		{
			case CompilationUnitSyntax c:
				var usings = string.Join("", c.Usings.Select(u => Print(u, indent)));
				var nsd = c.NamespaceDeclaration != null ? Print(c.NamespaceDeclaration, indent) : "";
				var members = string.Join("\n", c.Members.Select(m => Print(m, indent)));
				return $"{usings}{nsd}{members}";

			case UsingDirectiveSyntax u:
				return $"{ind}using {u.NamespaceName};\n";

			case NamespaceDeclarationSyntax ns:
				var nsUsings = string.Join("", ns.Usings.Select(u => Print(u, indent + 1)));
				var nsMembers = string.Join("\n", ns.Members.Select(m => Print(m, indent + 1)));
				return $"{ind}namespace {ns.Name};\n{nsUsings}{nsMembers}\n";

			case FunctionDeclarationSyntax f:
				var generics = f.GenericParameters.Count > 0 ? $"<{string.Join(", ", f.GenericParameters)}>" : "";
				var parms = string.Join(", ", f.Parameters.Select(p => $"{p.Type} {p.Name}"));
				return $"\n{ind}{f.ReturnType} {f.Name}{generics}({parms}) {Print(f.Body, indent)}";

			case ExtensionDeclarationSyntax ext:
				var extGenerics = ext.GenericParameters.Count > 0 ? $"<{string.Join(", ", ext.GenericParameters)}>" : "";
				var extMembers = string.Join("\n", ext.Methods.Select(m => Print(m, indent + 1)));
				return $"\n{ind}extension {ext.ExtendedTypeName}{extGenerics} {{\n{extMembers}\n{ind}}}";

			case BlockStatementSyntax b:
				var stmts = string.Join("", b.Statements.Select(s => Print(s, indent + 1)));
				return $"{{\n{stmts}{ind}}}\n";

			case ExpressionStatementSyntax e:
				return $"{ind}{Print(e.Expression)};\n";

			case ReturnStatementSyntax r:
				return $"{ind}return{(r.Expression != null ? " " + Print(r.Expression) : "")};\n";

			case VariableDeclarationSyntax v:
				var declWord = v.IsMutable ? "var" : "val";
				var typeStr = v.Type != null ? $" {v.Type}" : "";
				var initStr = v.Initializer != null ? $" = {Print(v.Initializer)}" : "";
				return $"{ind}{declWord}{typeStr} {v.Name}{initStr};\n";

			case CallExpressionSyntax call:
				var callGenerics = call.TypeArguments.Count > 0 ? $"<{string.Join(", ", call.TypeArguments)}>" : "";
				var args = string.Join(", ", call.Arguments.Select(Print));
				return $"{call.FunctionName}{callGenerics}({args})";

			case StringLiteralExpressionSyntax str:
				return $"\"{str.Value.Replace("\n", "\\n")}\"";

			case IntegerLiteralExpressionSyntax i:
				return i.Value.ToString();

			case DoubleLiteralExpressionSyntax d:
				return d.Value.ToString();

			case BooleanLiteralExpressionSyntax b:
				return b.Value ? "true" : "false";

			case IdentifierExpressionSyntax id:
				return id.Name;

			case MemberAccessExpressionSyntax m:
				return $"{Print(m.Expression)}.{m.MemberName}";

			case IndexExpressionSyntax idx:
				return $"{Print(idx.Left)}[{Print(idx.Index)}]";

			case BorrowExpressionSyntax b:
				return $"ref {Print(b.Expression)}";

			case StructInitializationExpressionSyntax s:
				var inits = string.Join(", ", s.Initializers.Select(i => $"{i.MemberName}: {Print(i.Expression)}"));
				return $"{s.StructTypeName} {{ {inits} }}";

			case ParenthesizedStructInitializerExpressionSyntax p:
				var pInits = string.Join(", ", p.Initializers.Select(i => $"{i.MemberName}: {Print(i.Expression)}"));
				return $"({pInits})";

			case BinaryExpressionSyntax bin:
				return $"{Print(bin.Left)} {bin.Operator} {Print(bin.Right)}";

			case UnaryExpressionSyntax u:
				return $"{u.Operator}{Print(u.Operand)}";

			case TernaryExpressionSyntax t:
				return $"{Print(t.Condition)} ? {Print(t.ThenExpression)} : {Print(t.ElseExpression)}";

			case VoidLiteralExpressionSyntax:
				return "void";

			case DefaultExpressionSyntax def:
				return def.TypeName is null ? "default" : $"default({def.TypeName})";

			case UnionDeclarationSyntax u:
				var uGenerics = u.GenericParameters.Count > 0 ? $"<{string.Join(", ", u.GenericParameters)}>" : "";
				var uFields = string.Join("\n", u.Fields.Select(f => $"{ind}    {f.Type} {f.Name};"));
				return $"\n{ind}union {u.Name}{uGenerics} {{\n{uFields}\n{ind}}}\n";

			case EnumDeclarationSyntax e:
				var storage = e.StorageType != null ? $" : {e.StorageType}" : "";
				var variants = string.Join(", ", e.Variants.Select(v => v.Value != null ? $"{v.Name} = {Print(v.Value)}" : v.Name));
				return $"\n{ind}enum {e.Name}{storage} {{ {variants} }}\n";

			case EnumVariantDeclarationSyntax ev:
				return ev.Value != null ? $"{ev.Name} = {Print(ev.Value)}" : ev.Name;

			case TypeAliasDeclarationSyntax a:
				var aGenerics = a.GenericParameters.Count > 0 ? $"<{string.Join(", ", a.GenericParameters)}>" : "";
				return $"{ind}alias {a.Name}{aGenerics} = {a.Type};\n";

			case SwitchStatementSyntax sw:
				var casesStr = string.Join("", sw.Cases.Select(c => {
					var patternStr = c.IsDefault ? "default" : (c.VariableName != null ? $"{c.VariantName} {c.VariableName}" : c.VariantName);
					var bodyStr = string.Join("", c.Body.Select(s => Print(s, indent + 2)));
					return $"{ind}    case {patternStr}:\n{bodyStr}";
				}));
				return $"{ind}switch ({Print(sw.Expression)}) {{\n{casesStr}{ind}}}\n";

			default:
				return node.ToString() ?? "";
		}
	}
}
