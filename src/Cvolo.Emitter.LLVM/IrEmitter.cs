using Cvolo.Core;

namespace Cvolo.Emitter.LLVM;

public sealed class IrEmitter
{
    private readonly StringWriter _writer = new();
    private int _labelCounter;
    private int _localCounter;
    private int _stringIndex;
    private readonly Dictionary<string, string> _locals = [];
    private readonly List<string> _stringDefs = [];

    public string Emit(CompilationUnitSyntax unit)
    {
        _writer.WriteLine("; ModuleID = 'cvolo_module'");
        _writer.WriteLine("source_filename = \"cvolo_module\"");
        _writer.WriteLine();

        foreach (var member in unit.Members)
            if (member is ExternDeclarationSyntax ext) EmitExtern(ext);

        var funcs = new List<FunctionDeclarationSyntax>();
        foreach (var member in unit.Members)
            if (member is FunctionDeclarationSyntax func) funcs.Add(func);

        foreach (var f in funcs)
        {
            // Collect strings into _stringDefs during emit, accumulate at end
            var funcWriter = new StringWriter();
            EmitFunction(f, funcWriter);
            _writer.Write(funcWriter.ToString());
        }

        if (_stringDefs.Count > 0)
        {
            _writer.WriteLine();
            foreach (var def in _stringDefs)
                _writer.WriteLine(def);
        }

        return _writer.ToString();
    }

    private void EmitExtern(ExternDeclarationSyntax ext)
    {
        var ret = Type(ext.ReturnType);
        var parms = ext.Parameters.Select(p => Type(p.Type)).ToList();
        if (ext.IsVariadic) parms.Add("...");
        _writer.WriteLine($"declare {ret} @{ext.Name}({string.Join(", ", parms)})");
        _writer.WriteLine();
    }

    private void EmitFunction(FunctionDeclarationSyntax func, StringWriter fw)
    {
        _labelCounter = 0;
        _localCounter = 0;
        _locals.Clear();

        var ret = Type(func.ReturnType);
        var parms = string.Join(", ", func.Parameters.Select(p => $"{Type(p.Type)} %{p.Name}"));
        fw.Write($"define {ret} @{func.Name}({parms})");
        fw.WriteLine(" {");
        fw.WriteLine("  entry:");

        foreach (var p in func.Parameters)
        {
            var ptr = NewLocal();
            _locals[p.Name] = ptr;
            fw.WriteLine($"    %{ptr} = alloca {Type(p.Type)}");
            fw.WriteLine($"    store {Type(p.Type)} %{p.Name}, ptr %{ptr}");
        }

        EmitBlock(func.Body, fw);

        if (func.ReturnType is "void" && !EndsWithReturn(func.Body))
            fw.WriteLine("    ret void");

        fw.WriteLine("}");
        fw.WriteLine();
    }

    private void EmitBlock(BlockStatementSyntax block, StringWriter fw)
    {
        foreach (var stmt in block.Statements) EmitStmt(stmt, fw);
    }

    private void EmitStmt(SyntaxNode stmt, StringWriter fw)
    {
        switch (stmt)
        {
            case BlockStatementSyntax b: EmitBlock(b, fw); break;
            case ReturnStatementSyntax r: EmitReturn(r, fw); break;
            case ExpressionStatementSyntax e: EmitExprStmt(e, fw); break;
            case VariableDeclarationSyntax v: EmitVar(v, fw); break;
            case IfStatementSyntax i: EmitIf(i, fw); break;
            case WhileStatementSyntax w: EmitWhile(w, fw); break;
            case ForStatementSyntax f: EmitFor(f, fw); break;
        }
    }

    private void EmitReturn(ReturnStatementSyntax r, StringWriter fw)
    {
        if (r.Expression is null)
            fw.WriteLine("    ret void");
        else
        {
            var (v, _) = Eval(r.Expression, fw);
            fw.WriteLine($"    ret {v}");
        }
    }

    private void EmitExprStmt(ExpressionStatementSyntax es, StringWriter fw)
    {
        switch (es.Expression)
        {
            case CallExpressionSyntax call: EmitCall(call, fw); break;
            case BinaryExpressionSyntax { Operator: "=" } assign: EmitStore(assign, fw); break;
            default: fw.WriteLine($"    {Eval(es.Expression, fw).val}"); break;
        }
    }

    private void EmitVar(VariableDeclarationSyntax v, StringWriter fw)
    {
        var ptr = NewLocal();
        _locals[v.Name] = ptr;
        var ty = Type(v.Type ?? "int");
        fw.WriteLine($"    %{ptr} = alloca {ty}");
        if (v.Initializer is not null)
        {
            var (val, _) = Eval(v.Initializer, fw);
            fw.WriteLine($"    store {val}, ptr %{ptr}");
        }
    }

    private void EmitIf(IfStatementSyntax node, StringWriter fw)
    {
        var (c, _) = Eval(node.Condition, fw);
        var t = NextLabel();
        var e = NextLabel();
        var d = NextLabel();
        fw.WriteLine($"    br {c}, label %{t}, label %{e}");
        fw.WriteLine($"  {t}:");
        EmitStmt(node.ThenStatement, fw);
        if (!EndsWithReturn(node.ThenStatement))
            fw.WriteLine($"    br label %{d}");
        fw.WriteLine($"  {e}:");
        if (node.ElseClause is not null)
        {
            EmitStmt(node.ElseClause.Body, fw);
            if (!EndsWithReturn(node.ElseClause.Body))
                fw.WriteLine($"    br label %{d}");
        }
        else
        {
            fw.WriteLine($"    br label %{d}");
        }
        fw.WriteLine($"  {d}:");
    }

    private void EmitWhile(WhileStatementSyntax node, StringWriter fw)
    {
        var c = NextLabel();
        var b = NextLabel();
        var d = NextLabel();
        fw.WriteLine($"    br label %{c}");
        fw.WriteLine($"  {c}:");
        var (cond, _) = Eval(node.Condition, fw);
        fw.WriteLine($"    br {cond}, label %{b}, label %{d}");
        fw.WriteLine($"  {b}:");
        EmitStmt(node.Body, fw);
        fw.WriteLine($"    br label %{c}");
        fw.WriteLine($"  {d}:");
    }

    private void EmitFor(ForStatementSyntax node, StringWriter fw)
    {
        EmitVar(node.Initializer, fw);
        var c = NextLabel();
        var b = NextLabel();
        var i = NextLabel();
        var d = NextLabel();
        fw.WriteLine($"    br label %{c}");
        fw.WriteLine($"  {c}:");
        var (cond, _) = Eval(node.Condition, fw);
        fw.WriteLine($"    br {cond}, label %{b}, label %{d}");
        fw.WriteLine($"  {b}:");
        EmitStmt(node.Body, fw);
        fw.WriteLine($"    br label %{i}");
        fw.WriteLine($"  {i}:");
        Eval(node.Increment, fw);
        fw.WriteLine($"    br label %{c}");
        fw.WriteLine($"  {d}:");
    }

    private (string val, string ty) Eval(ExpressionSyntax expr, StringWriter fw)
    {
        return expr switch
        {
            IntegerLiteralExpressionSyntax n => ($"i32 {n.Value}", "i32"),
            DoubleLiteralExpressionSyntax d => ($"double {d.Value}", "double"),
            BooleanLiteralExpressionSyntax b => ($"i1 {(b.Value ? "1" : "0")}", "i1"),
            StringLiteralExpressionSyntax s => (AddString(s.Value), "ptr"),
            IdentifierExpressionSyntax id => Load(id.Name, fw),
            CallExpressionSyntax call => EmitCallExpr(call, fw),
            BinaryExpressionSyntax { Operator: "=" } assign => EmitLoadStore(assign, fw),
            BinaryExpressionSyntax bin => EmitBin(bin, fw),
            UnaryExpressionSyntax u => EmitUnary(u, fw),
            _ => throw new InvalidOperationException($"Unknown expr: {expr.GetType()}"),
        };
    }

    private void EmitCall(CallExpressionSyntax call, StringWriter fw)
    {
        var args = string.Join(", ", call.Arguments.Select(a => Eval(a, fw).val));
        fw.WriteLine($"    call void @{call.FunctionName}({args})");
    }

    private (string val, string ty) EmitCallExpr(CallExpressionSyntax call, StringWriter fw)
    {
        // 1. Evaluate arguments first so their registers are generated and printed sequentially
        var args = string.Join(", ", call.Arguments.Select(a => Eval(a, fw).val));

        // 2. Allocate the return register only after arguments are fully resolved
        var r = NewLocal();

        fw.WriteLine($"    %{r} = call i32 @{call.FunctionName}({args})");
        return ($"i32 %{r}", "i32");
    }

    private (string val, string ty) EmitBin(BinaryExpressionSyntax bin, StringWriter fw)
    {
        var (l, _) = Eval(bin.Left, fw);
        var (r, _) = Eval(bin.Right, fw);
        var reg = NewLocal();
        var (op, resultTy) = bin.Operator switch
        {
            "+"  => ($"add i32 {V(l)}, {V(r)}", "i32"),
            "-"  => ($"sub i32 {V(l)}, {V(r)}", "i32"),
            "*"  => ($"mul i32 {V(l)}, {V(r)}", "i32"),
            "/"  => ($"sdiv i32 {V(l)}, {V(r)}", "i32"),
            "%"  => ($"srem i32 {V(l)}, {V(r)}", "i32"),
            "==" => ($"icmp eq i32 {V(l)}, {V(r)}", "i1"),
            "!=" => ($"icmp ne i32 {V(l)}, {V(r)}", "i1"),
            "<"  => ($"icmp slt i32 {V(l)}, {V(r)}", "i1"),
            ">"  => ($"icmp sgt i32 {V(l)}, {V(r)}", "i1"),
            "<=" => ($"icmp sle i32 {V(l)}, {V(r)}", "i1"),
            ">=" => ($"icmp sge i32 {V(l)}, {V(r)}", "i1"),
            _ => throw new InvalidOperationException($"Unknown binop '{bin.Operator}'"),
        };
        fw.WriteLine($"    %{reg} = {op}");
        return ($"{resultTy} %{reg}", resultTy);
    }

    private (string val, string ty) EmitUnary(UnaryExpressionSyntax u, StringWriter fw)
    {
        var (o, _) = Eval(u.Operand, fw);
        var r = NewLocal();
        var (op, resultTy) = u.Operator switch
        {
            "-" => ($"sub i32 0, {V(o)}", "i32"),
            "!" => ($"xor i1 1, {V(o)}", "i1"),
            _ => throw new InvalidOperationException($"Unknown unary op '{u.Operator}'"),
        };
        fw.WriteLine($"    %{r} = {op}");
        return ($"{resultTy} %{r}", resultTy);
    }

    private void EmitStore(BinaryExpressionSyntax assign, StringWriter fw)
    {
        if (assign.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
        {
            var (r, _) = Eval(assign.Right, fw);
            fw.WriteLine($"    store {r}, ptr %{ptr}");
        }
    }

    private (string val, string ty) EmitLoadStore(BinaryExpressionSyntax assign, StringWriter fw)
    {
        if (assign.Left is IdentifierExpressionSyntax id && _locals.TryGetValue(id.Name, out var ptr))
        {
            var (r, _) = Eval(assign.Right, fw);
            fw.WriteLine($"    store {r}, ptr %{ptr}");
            return (r, "i32");
        }
        throw new InvalidOperationException($"Cannot assign to non-variable");
    }

    private (string val, string ty) Load(string name, StringWriter fw)
    {
        if (!_locals.TryGetValue(name, out var ptr))
            throw new InvalidOperationException($"Undefined variable '{name}'");
        var reg = NewLocal();
        fw.WriteLine($"    %{reg} = load i32, ptr %{ptr}");
        return ($"i32 %{reg}", "i32");
    }

    private string AddString(string value)
    {
        var idx = _stringIndex++;
        var escaped = string.Concat(value.Select(c => c switch
        {
            '\n' => "\\0A",
            '\r' => "\\0D",
            '\t' => "\\09",
            '"' => "\\22",
            '\\' => "\\5C",
            _ when c < 32 || c > 126 => $"\\{c:X02}",
            _ => c.ToString(),
        }));
        _stringDefs.Add($"@str{idx} = private unnamed_addr constant [{value.Length + 1} x i8] c\"{escaped}\\00\"");
        return $"ptr @str{idx}";
    }

    private string NextLabel() => $"L{_labelCounter++}";
    private string NewLocal() => (_localCounter++).ToString();

    private static string Type(string t) => t switch
    {
        "void" => "void",
        "int" or "Int32" => "i32",
        "double" or "Double" => "double",
        "bool" or "Boolean" => "i1",
        "string" or "String" => "ptr",
        "char" or "Char" => "i8",
        _ => "i32",
    };

    private static string V(string typed) => typed.Split(' ')[^1];
    private static bool EndsWithReturn(SyntaxNode s) => s switch
    {
        BlockStatementSyntax b => b.Statements.Count > 0 && b.Statements[^1] is ReturnStatementSyntax,
        ReturnStatementSyntax => true,
        _ => false,
    };
}
