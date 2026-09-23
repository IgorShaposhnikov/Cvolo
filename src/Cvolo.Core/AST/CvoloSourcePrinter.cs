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
				return $"{ind}{(u.IsExposed ? "expose " : "")}using {u.NamespaceName};\n";

			case NamespaceDeclarationSyntax ns:
				var nsUsings = string.Join("", ns.Usings.Select(u => Print(u, indent + 1)));
				var nsMembers = string.Join("\n", ns.Members.Select(m => Print(m, indent + 1)));
				return $"{ind}namespace {ns.Name};\n{nsUsings}{nsMembers}\n";

			case AttributeSyntax attr:
				var attrArgs = attr.Arguments.Count == 0
					? ""
					: "(" + string.Join(", ", attr.Arguments.Select((arg, i) =>
					{
						var argName = attr.ArgumentNames is { } names ? names.ElementAtOrDefault(i) : null;
						return argName is null ? Print(arg) : $"{argName}: {Print(arg)}";
					})) + ")";
				return $"{ind}[{attr.Name}{attrArgs}]";

			case FunctionDeclarationSyntax f:
				var fAttrs = PrintAttributes(f.Attributes, indent);
				var fGenerics = f.GenericParameters.Count > 0 ? $"<{string.Join(", ", f.GenericParameters)}>" : "";
				var fParms = string.Join(", ", f.Parameters.Select(Print));
				var fBody = f.Body != null ? $" {Print(f.Body, indent)}" : ";\n";
				var fPrefix = f.CallingConvention is not null
					? $"unsafe \"{f.CallingConvention}\" "
					: f.Modifier is not null ? $"{f.Modifier.ToString()!.ToLowerInvariant()} " : "";
				return $"{fAttrs}\n{ind}{fPrefix}{f.ReturnType} {f.Name}{fGenerics}({fParms}){fBody}";

			case ExternDeclarationSyntax ed:
				var edParms = string.Join(", ", ed.Parameters.Select(Print)) + (ed.IsVariadic ? ", ..." : "");
				return $"{ind}extern {ed.ReturnType} {ed.Name}({edParms});\n";

			case ParameterSyntax p:
				return $"{p.Type} {p.Name}";

			case StructFieldSyntax sf:
				return $"{sf.Type} {sf.Name};\n";

			case ExtensionDeclarationSyntax ext:
				var extGenerics = Generics(ext.GenericParameters);
				var extConform = ext.ConformsTo != null ? $" : {ext.ConformsTo}" : "";
				var extConstructors = string.Join("\n", ext.Constructors.Select(c => Print(c, indent + 1)));
				var extDestructors = string.Join("\n", ext.Destructors.Select(d => Print(d, indent + 1)));
				var extMethods = string.Join("\n", ext.Methods.Select(m => Print(m, indent + 1)));
				var extAll = string.Join("\n", new[] { extConstructors, extDestructors, extMethods }.Where(s => s.Length > 0));
				return $"\n{ind}extension {ext.ExtendedTypeName}{extGenerics}{extConform} {{\n{extAll}\n{ind}}}";

			case StructDeclarationSyntax st:
				var stFields = string.Join("", st.Fields.Select(f => $"{ind}    {Print(f)}"));
				return $"{PrintAttributes(st.Attributes, indent)}\n{ind}struct {st.Name}{Generics(st.GenericParameters)}{(st.EmbeddedType != null ? $" : {st.EmbeddedType}" : "")} {{\n{stFields}{ind}}}\n";

			case ProtocolDeclarationSyntax proto:
				var protoMembers = string.Join("", proto.Members.Select(pm => $"{ind}    {pm.ReturnType} {pm.Name}({string.Join(", ", pm.Parameters.Select(Print))});\n"));
				return $"{PrintAttributes(proto.Attributes, indent)}\n{ind}protocol {proto.Name}{Generics(proto.GenericParameters)}{(proto.Bases is { Count: > 0 } ? $" : {string.Join(" & ", proto.Bases)}" : "")} {{\n{protoMembers}{ind}}}\n";

			case InterfaceDeclarationSyntax ifc:
				var ifcMembers = string.Join("", ifc.Members.Select(im => $"{ind}    {im.ReturnType} {im.Name}({string.Join(", ", im.Parameters.Select(Print))});\n"));
				return $"{PrintAttributes(ifc.Attributes, indent)}\n{ind}interface {ifc.Name}{Generics(ifc.GenericParameters)}{(ifc.Bases is { Count: > 0 } ? $" : {string.Join(" & ", ifc.Bases)}" : "")} {{\n{ifcMembers}{ind}}}\n";

			case ProtocolMethodDeclarationSyntax pm:
				return $"{pm.ReturnType} {pm.Name}({string.Join(", ", pm.Parameters.Select(Print))});\n";

			case InterfaceMethodDeclarationSyntax im:
				return $"{im.ReturnType} {im.Name}({string.Join(", ", im.Parameters.Select(Print))});\n";

			case ConstructorDeclarationSyntax ctor:
				var ctorInit = ctor.ConstructorArguments is { } cargs && cargs.Count > 0
					? $" : {ctor.StructName}({string.Join(", ", cargs.Select(Print))})"
					: "";
				return $"{PrintAttributes(ctor.Attributes, indent)}\n{ind}{ctor.StructName}({string.Join(", ", ctor.Parameters.Select(Print))}){ctorInit} {Print(ctor.Body, indent)}";

			case DestructorDeclarationSyntax dtor:
				return $"{PrintAttributes(dtor.Attributes, indent)}\n{ind}~{dtor.StructName}() {Print(dtor.Body, indent)}";

			case BlockStatementSyntax b:
				var stmts = string.Join("", b.Statements.Select(s => Print(s, indent + 1)));
				return $"{{\n{stmts}{ind}}}\n";

			case LabeledBlockStatementSyntax lb:
				return $"{ind}{lb.Label}: {Print(lb.Body, indent)}";

			case ExpressionStatementSyntax e:
				return $"{ind}{Print(e.Expression)};\n";

			case ReturnStatementSyntax r:
				return $"{ind}return{(r.Expression != null ? " " + Print(r.Expression) : "")};\n";

			case VariableDeclarationSyntax v:
				var declWord = v.IsMutable ? "var" : "val";
				var typeStr = v.Type != null ? $" {v.Type}" : "";
				var initStr = v.Initializer != null ? $" = {Print(v.Initializer)}" : "";
				return $"{ind}{declWord}{typeStr} {v.Name}{initStr};\n";

			case IfStatementSyntax ifSt:
				var elsePart = ifSt.ElseClause is { } elseClause ? $"else {Print(elseClause.Body, indent)}" : "";
				return $"{ind}if ({Print(ifSt.Condition)}) {Print(ifSt.ThenStatement, indent)}{elsePart}";

			case ElseClauseSyntax elseCl:
				return $"else {Print(elseCl.Body, indent)}";

			case WhileStatementSyntax w:
				return $"{ind}{(w.Label is not null ? $"{w.Label}: " : "")}while ({Print(w.Condition)}) {Print(w.Body, indent)}";

			case ForStatementSyntax forSt:
				var forInit = forSt.Initializer is { } initDecl
					? $"{(initDecl.IsMutable ? "var" : "val")}{(initDecl.Type != null ? $" {initDecl.Type}" : "")} {initDecl.Name}{(initDecl.Initializer != null ? $" = {Print(initDecl.Initializer)}" : "")}"
					: "";
				return $"{ind}{(forSt.Label is not null ? $"{forSt.Label}: " : "")}for ({forInit}; {Print(forSt.Condition)}; {Print(forSt.Increment)}) {Print(forSt.Body, indent)}";

			case ForEachStatementSyntax fe:
				var feDecl = fe.BindingKind == ForEachVariableKind.Var ? "var" : fe.BindingKind == ForEachVariableKind.RefVar ? "refvar" : "val";
				var feType = fe.ExplicitItemType != null ? $" {fe.ExplicitItemType}" : "";
				return $"{ind}{(fe.Label is not null ? $"{fe.Label}: " : "")}foreach ({feDecl}{feType} {fe.ItemName} in {Print(fe.Collection)}) {Print(fe.Body, indent)}";

			case UnsafeBlockStatementSyntax ub:
				return $"{ind}unsafe {Print(ub.Body, indent)}";

			case DeferStatementSyntax df:
				return $"{ind}defer {Print(df.Body, indent)}";

			case CallExpressionSyntax call:
				var callGenerics = call.TypeArguments.Count > 0 ? $"<{string.Join(", ", call.TypeArguments)}>" : "";
				var args = string.Join(", ", call.Arguments.Select(Print));
				return $"{call.FunctionName}{callGenerics}({args})";

			case StringLiteralExpressionSyntax str:
				return $"\"{str.Value.Replace("\n", "\\n")}\"";

			case CharacterLiteralExpressionSyntax chr:
				return $"'{chr.Value}'";

			case IntegerLiteralExpressionSyntax i:
				return i.Value.ToString();

			case DoubleLiteralExpressionSyntax d:
				return d.Value.ToString();

			case BooleanLiteralExpressionSyntax bl:
				return bl.Value ? "true" : "false";

			case NullLiteralExpressionSyntax:
				return "null";

			case IdentifierExpressionSyntax id:
				return id.Name;

			case MemberAccessExpressionSyntax m:
				return $"{Print(m.Expression)}.{m.MemberName}";

			case IndexExpressionSyntax idx:
				return $"{Print(idx.Left)}[{Print(idx.Index)}]";

			case BorrowExpressionSyntax bw:
				return $"ref {Print(bw.Expression)}";

			case HeapAllocationExpressionSyntax hp:
				return $"heap {Print(hp.Expression)}";

			case HeapArrayAllocationExpressionSyntax hpa:
				return $"heap {hpa.ElementTypeName}[{Print(hpa.CountExpression)}]";

			case ArrayInitializationExpressionSyntax ai:
				return $"[{string.Join(", ", ai.Elements.Select(Print))}]";

			case ArrayReplicationExpressionSyntax ar:
				return $"[{Print(ar.Value)}; {Print(ar.Count)}]";

			case MemberInitializerSyntax mi:
				return $"{mi.MemberName}: {Print(mi.Expression)}";

			case StructInitializationExpressionSyntax si:
				var siInits = string.Join(", ", si.Initializers.Select(i => $"{i.MemberName}: {Print(i.Expression)}"));
				return $"{si.StructTypeName} {{ {siInits} }}";

			case ParenthesizedStructInitializerExpressionSyntax psi:
				var psiInits = string.Join(", ", psi.Initializers.Select(i => $"{i.MemberName}: {Print(i.Expression)}"));
				return $"({psiInits})";

			case BinaryExpressionSyntax bin:
				return $"{Print(bin.Left)} {bin.Operator} {Print(bin.Right)}";

			case UnaryExpressionSyntax un:
				return $"{un.Operator}{Print(un.Operand)}";

			case TernaryExpressionSyntax tn:
				return $"{Print(tn.Condition)} ? {Print(tn.ThenExpression)} : {Print(tn.ElseExpression)}";

			case IsPatternExpressionSyntax ip:
				return $"{Print(ip.Operand)} is {ip.VariantName}{(ip.BoundName != null ? $" {ip.BoundName}" : "")}";

			case VoidLiteralExpressionSyntax:
				return "void";

			case DefaultExpressionSyntax dfl:
				return dfl.TypeName is null ? "default" : $"default({dfl.TypeName})";

			case NameofExpressionSyntax nf:
				return $"nameof({Print(nf.Argument)})";

			case TypeofExpressionSyntax tf:
				return $"typeof({tf.TypeName})";

			case InterpolatedStringExpressionSyntax istr:
				return istr.RawText;

			case GlobalVariableDeclarationSyntax g:
				var gWord = g.IsMutable ? "global var" : "global";
				var gInit = g.Initializer != null ? $" = {Print(g.Initializer)}" : "";
				return $"{ind}{gWord} {g.Type} {g.Name}{gInit};\n";

			case UnionDeclarationSyntax ud:
				var udFields = string.Join("", ud.Fields.Select(uf => $"{ind}    {Print(uf)}"));
				return $"{PrintAttributes(ud.Attributes, indent)}\n{ind}{(ud.IsUnsafe ? "unsafe " : "")}union {ud.Name}{Generics(ud.GenericParameters)} {{\n{udFields}{ind}}}\n";

			case UnionFieldSyntax uf:
				return $"{uf.Type} {uf.Name};\n";

			case EnumDeclarationSyntax en:
				var storage = en.StorageType != null ? $" : {en.StorageType}" : "";
				var variants = string.Join(", ", en.Variants.Select(v => v.Value != null ? $"{v.Name} = {Print(v.Value)}" : v.Name));
				return $"\n{ind}enum {en.Name}{storage} {{ {variants} }}\n";

			case EnumVariantDeclarationSyntax ev:
				return ev.Value != null ? $"{ev.Name} = {Print(ev.Value)}" : ev.Name;

			case TypeAliasDeclarationSyntax ta:
				return $"{ind}alias {ta.Name}{Generics(ta.GenericParameters)} = {ta.Type};\n";

			case DelegateDeclarationSyntax dd:
				var ddNative = dd.IsNative ? $"unsafe \"{dd.CallingConvention}\" " : "";
				var ddVisibility = dd.SyntacticVisibility is null ? "" : $"{dd.Visibility.ToString().ToLowerInvariant()} ";
				var ddParms = string.Join(", ", dd.Parameters.Select(Print));
				return $"{ind}{ddVisibility}{ddNative}delegate {dd.ReturnType} {dd.Name}{Generics(dd.GenericParameters)}({ddParms});\n";

			case DelegateBlockDeclarationSyntax db:
				var dbMembers = string.Join("", db.Delegates.Select(d =>
				{
					var visibility = d.SyntacticVisibility is null ? "" : $"{d.Visibility.ToString().ToLowerInvariant()} ";
					var parms = string.Join(", ", d.Parameters.Select(Print));
					return $"{ind}    {visibility}delegate {d.ReturnType} {d.Name}{Generics(d.GenericParameters)}({parms});\n";
				}));
				return $"{ind}unsafe \"{db.CallingConvention}\" {{\n{dbMembers}{ind}}}\n";

			case SwitchStatementSyntax sw:
				var swCases = string.Join("", sw.Cases.Select(sc => $"{ind}    case {(sc.IsDefault ? "default" : sc.VariableName != null ? $"{sc.VariantName} {sc.VariableName}" : sc.VariantName)}:\n{string.Join("", sc.Body.Select(s => Print(s, indent + 2)))}"));
				return $"{ind}switch ({Print(sw.Expression)}) {{\n{swCases}{ind}}}\n";

			case SwitchCaseSyntax swc:
				return $"{ind}case {(swc.IsDefault ? "default" : swc.VariableName != null ? $"{swc.VariantName} {swc.VariableName}" : swc.VariantName)}:\n{string.Join("", swc.Body.Select(s => Print(s, indent + 1)))}";

			case TryStatementSyntax t:
				var catchStr = string.Join("", t.CatchClauses.Select(c =>
				{
					var clausesBodyStr = string.Join("", c.Body.Statements.Select(s => Print(s, indent + 2)));
					var patternStr = c.IsBare
						? " {"
						: c.VariantName != null
							? c.ErrorTypeName + "." + c.VariantName + ") {"
							: c.BindingName != null
								? $"{c.ErrorTypeName} {c.BindingName}) {{"
								: $"{c.ErrorTypeName}) {{";
					return $"{ind}    catch ({patternStr}\n{clausesBodyStr}{ind}    }}\n";
				}));
				var tryBodyStr = string.Join("", t.Body.Statements.Select(s => Print(s, indent + 1)));
				var finallyStr = t.FinallyBody is { } finallyBody
					? $"{ind}finally {{\n{string.Join("", finallyBody.Statements.Select(s => Print(s, indent + 1)))}{ind}}}\n"
					: "";
				return $"{ind}try {{\n{tryBodyStr}{ind}}} {catchStr}{finallyStr}\n";

			case CatchClauseSyntax cc:
				var ccPattern = cc.IsBare
					? ""
					: cc.VariantName != null
						? cc.ErrorTypeName + "." + cc.VariantName
						: cc.BindingName != null
							? $"{cc.ErrorTypeName} {cc.BindingName}"
							: cc.ErrorTypeName ?? "";
				return $"{ind}catch ({ccPattern}) {Print(cc.Body, indent)}";

			case CatchExpressionSyntax cExpr:
				return $"{Print(cExpr.Operand)} catch {(cExpr.Lambda != null ? $"({cExpr.Lambda.ErrorName}) => {Print(cExpr.Lambda.Body, indent)}" : Print(cExpr.Fallback!))}";

			case CatchLambdaExpressionSyntax clb:
				return $"({clb.ErrorName}) => {Print(clb.Body)}";

			case LambdaExpressionSyntax lam:
				var lamMode = lam.CaptureMode switch
				{
					LambdaCaptureMode.Move => "move ",
					LambdaCaptureMode.Ref => "ref ",
					LambdaCaptureMode.RefVar => "refvar ",
					_ => "",
				};
				var lamParms = string.Join(", ", lam.Parameters.Select(Print));
				var lamBody = lam.BlockBody is not null ? Print(lam.BlockBody) : Print(lam.ExpressionBody!);
				return $"{lamMode}({lamParms}) => {lamBody}";

			case LambdaParameterSyntax lp:
				return lp.ExplicitType != null ? $"{lp.ExplicitType} {lp.Name}" : lp.Name;

			case BreakStatementSyntax brk:
				return brk.TargetLabel is not null ? $"{ind}break {brk.TargetLabel};\n" : $"{ind}break;\n";

			case ContinueStatementSyntax cont:
				return cont.Label is not null ? $"{ind}continue {cont.Label};\n" : $"{ind}continue;\n";

			case ExternBlockSyntax exb:
				var exbFns = string.Join("", exb.Functions.Select(fn =>
				{
					var fnParms = string.Join(", ", fn.Parameters.Select(Print)) + (fn.IsVariadic ? ", ..." : "");
					var visibility = fn.SyntacticVisibility is null ? "" : $"{fn.Visibility.ToString().ToLowerInvariant()} ";
					return $"{ind}    {visibility}{fn.ReturnType} {fn.Name}({fnParms});\n";
				}));
				return $"{PrintAttributes(exb.Attributes, indent)}\n{ind}extern {(exb.CallingConvention != null ? $"{exb.CallingConvention} " : "")}{{\n{exbFns}{ind}}}\n";

			case ExternBlockFunctionSyntax exbf:
				var exbfParms = string.Join(", ", exbf.Parameters.Select(Print)) + (exbf.IsVariadic ? ", ..." : "");
				var exbfVisibility = exbf.SyntacticVisibility is null ? "" : $"{exbf.Visibility.ToString().ToLowerInvariant()} ";
				return $"{ind}{exbfVisibility}{exbf.ReturnType} {exbf.Name}({exbfParms});\n";

			case ExposeExternBlockSyntax expeb:
				var expebFns = string.Join("", expeb.Functions.Select(fn => Print(fn, indent + 1)));
				return $"{PrintAttributes(expeb.Attributes, indent)}\n{ind}expose extern {(expeb.CallingConvention != null ? $"{expeb.CallingConvention} " : "")}{{\n{expebFns}{ind}}}\n";

			case AsmOperandSyntax aop:
				return $"[{aop.Constraint}]({Print(aop.Expression)})";

			case AsmExpressionSyntax asm:
				var asmFlags = new List<string>();
				if (asm.Options.HasFlag(AsmOptions.Volatile)) asmFlags.Add("volatile");
				if (asm.Options.HasFlag(AsmOptions.AlignStack)) asmFlags.Add("alignstack");
				if (asm.Options.HasFlag(AsmOptions.Intel)) asmFlags.Add("intel");
				var asmFlagsStr = asmFlags.Count > 0 ? string.Join(" ", asmFlags) + " " : "";
				return $"asm {asmFlagsStr}\"{asm.Template}\"{(asm.Operands.Count > 0 ? $" {string.Join(" ", asm.Operands.Select(Print))}" : "")}";

			default:
				return node?.ToString() ?? "";
		}
	}

	private static string PrintAttributes(IReadOnlyList<AttributeSyntax>? attributes, int indent)
	{
		if (attributes is null || attributes.Count == 0) return "";
		return string.Join("\n", attributes.Select(x => Print(x, indent)));
	}

	private static string Generics(IReadOnlyList<string>? parameters)
	{
		if (parameters is null || parameters.Count == 0) return "";
		return $"<{string.Join(", ", parameters)}>";
	}
}
