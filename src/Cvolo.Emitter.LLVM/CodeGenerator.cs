using System.Runtime.InteropServices;
using Cvolo.Core;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

public sealed class CodeGenerator
{
    private readonly LLVMModuleRef _module;
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMContextRef _context;
    private readonly Dictionary<string, LLVMValueRef> _globals = [];
    private readonly Dictionary<string, LLVMValueRef> _locals = [];
    private readonly Dictionary<string, LLVMTypeRef> _functionTypes = [];

    public CodeGenerator(string moduleName)
    {
        _context = LLVMContextRef.Global;
        _module = _context.CreateModuleWithName(moduleName);
        _builder = _context.CreateBuilder();
    }

    public LLVMModuleRef Module => _module;

    public bool Emit(CompilationUnitSyntax unit)
    {
        foreach (var member in unit.Members)
        {
            switch (member)
            {
                case ExternDeclarationSyntax ext:
                    DeclareExternFunction(ext);
                    break;
                case FunctionDeclarationSyntax func:
                    DeclareFunction(func);
                    break;
            }
        }

        foreach (var member in unit.Members)
        {
            if (member is FunctionDeclarationSyntax func)
                EmitFunctionBody(func);
        }

        return true;
    }

    public bool WriteObjectFile(string path)
    {
        var triple = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "x86_64-pc-windows-msvc"
                   : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "x86_64-apple-darwin"
                   : "x86_64-pc-linux-gnu";
        _module.Target = triple;

        var target = LLVMTargetRef.GetTargetFromTriple(triple);
        var targetMachine = target.CreateTargetMachine(triple, "", "",
            LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            LLVMRelocMode.LLVMRelocDefault,
            LLVMCodeModel.LLVMCodeModelDefault);

        targetMachine.EmitToFile(_module, path, LLVMCodeGenFileType.LLVMObjectFile);
        return true;
    }

    public string PrintIR()
    {
        return _module.PrintToString();
    }

    private void DeclareExternFunction(ExternDeclarationSyntax ext)
    {
        var returnType = GetLLVMType(ext.ReturnType);
        var paramTypes = new List<LLVMTypeRef>();

        foreach (var param in ext.Parameters)
            paramTypes.Add(GetLLVMType(param.Type));

        var funcType = ext.IsVariadic
            ? LLVMTypeRef.CreateFunction(returnType, [.. paramTypes], IsVarArg: true)
            : LLVMTypeRef.CreateFunction(returnType, [.. paramTypes]);

        var func = _module.AddFunction(ext.Name, funcType);
        _globals[ext.Name] = func;
        _functionTypes[ext.Name] = funcType;
    }

    private void DeclareFunction(FunctionDeclarationSyntax func)
    {
        var returnType = GetLLVMType(func.ReturnType);
        var paramTypes = new List<LLVMTypeRef>();
        foreach (var param in func.Parameters)
            paramTypes.Add(GetLLVMType(param.Type));

        var funcType = LLVMTypeRef.CreateFunction(returnType, [.. paramTypes]);
        var llvmFunc = _module.AddFunction(func.Name, funcType);
        _globals[func.Name] = llvmFunc;
        _functionTypes[func.Name] = funcType;
    }

    private void EmitFunctionBody(FunctionDeclarationSyntax func)
    {
        if (!_globals.TryGetValue(func.Name, out var llvmFunc))
            return;

        var entry = llvmFunc.AppendBasicBlock("entry");
        _builder.PositionAtEnd(entry);

        _locals.Clear();

        for (int i = 0; i < func.Parameters.Count; i++)
        {
            var param = llvmFunc.GetParam((uint)i);
            param.Name = func.Parameters[i].Name;
            _locals[func.Parameters[i].Name] = param;
        }

        EmitBlock(func.Body);

        if (func.ReturnType == "void")
        {
            _builder.BuildRetVoid();
        }
    }

    private void EmitBlock(BlockStatementSyntax block)
    {
        foreach (var stmt in block.Statements)
            EmitStatement(stmt);
    }

    private void EmitStatement(SyntaxNode stmt)
    {
        switch (stmt)
        {
            case ReturnStatementSyntax ret:
                EmitReturnStatement(ret);
                break;
            case ExpressionStatementSyntax exprStmt:
                EmitExpression(exprStmt.Expression);
                break;
            case VariableDeclarationSyntax varDecl:
                EmitVariableDeclaration(varDecl);
                break;
            case BlockStatementSyntax block:
                EmitBlock(block);
                break;
            case IfStatementSyntax ifStmt:
                EmitIfStatement(ifStmt);
                break;
            case WhileStatementSyntax whileStmt:
                EmitWhileStatement(whileStmt);
                break;
            case ForStatementSyntax forStmt:
                EmitForStatement(forStmt);
                break;
        }
    }

    private void EmitReturnStatement(ReturnStatementSyntax ret)
    {
        if (ret.Expression is not null)
        {
            var value = EmitExpression(ret.Expression);
            _builder.BuildRet(value);
        }
        else
        {
            _builder.BuildRetVoid();
        }
    }

    private LLVMValueRef EmitExpression(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case IntegerLiteralExpressionSyntax intLit:
                return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)intLit.Value);
            case DoubleLiteralExpressionSyntax dblLit:
                return LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, dblLit.Value);
            case BooleanLiteralExpressionSyntax boolLit:
                return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolLit.Value ? 1UL : 0UL);
            case StringLiteralExpressionSyntax strLit:
                return EmitStringLiteral(strLit.Value);
            case IdentifierExpressionSyntax id:
                if (_locals.TryGetValue(id.Name, out var local))
                    return local;
                if (_globals.TryGetValue(id.Name, out var global))
                    return global;
                throw new InvalidOperationException($"Undefined identifier '{id.Name}'");
            case CallExpressionSyntax call:
                return EmitCallExpression(call);
            case BinaryExpressionSyntax bin:
                return EmitBinaryExpression(bin);
            case UnaryExpressionSyntax unary:
                return EmitUnaryExpression(unary);
            default:
                throw new InvalidOperationException($"Unknown expression type: {expr.GetType()}");
        }
    }

    private LLVMValueRef EmitStringLiteral(string value)
    {
        var strValue = value + "\0";
        return _builder.BuildGlobalStringPtr(strValue, "str");
    }

    private LLVMValueRef EmitCallExpression(CallExpressionSyntax call)
    {
        if (!_globals.TryGetValue(call.FunctionName, out var func))
            throw new InvalidOperationException($"Undefined function '{call.FunctionName}'");

        var funcType = _functionTypes[call.FunctionName];
        var args = new List<LLVMValueRef>();
        foreach (var arg in call.Arguments)
        {
            var argValue = EmitExpression(arg);
            args.Add(argValue);
        }

        return _builder.BuildCall2(funcType, func, [.. args]);
    }

    private LLVMValueRef EmitBinaryExpression(BinaryExpressionSyntax bin)
    {
        var left = EmitExpression(bin.Left);
        var right = EmitExpression(bin.Right);

        return bin.Operator switch
        {
            "+" => _builder.BuildAdd(left, right),
            "-" => _builder.BuildSub(left, right),
            "*" => _builder.BuildMul(left, right),
            "/" => _builder.BuildSDiv(left, right),
            "%" => _builder.BuildSRem(left, right),
            "==" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right),
            "!=" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right),
            "<" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right),
            ">" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right),
            "<=" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right),
            ">=" => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right),
            "=" => _builder.BuildStore(right, left),
            _ => throw new InvalidOperationException($"Unknown binary operator '{bin.Operator}'"),
        };
    }

    private LLVMValueRef EmitUnaryExpression(UnaryExpressionSyntax unary)
    {
        var operand = EmitExpression(unary.Operand);
        return unary.Operator switch
        {
            "-" => _builder.BuildNeg(operand),
            "!" => _builder.BuildNot(operand),
            _ => throw new InvalidOperationException($"Unknown unary operator '{unary.Operator}'"),
        };
    }

    private void EmitVariableDeclaration(VariableDeclarationSyntax varDecl)
    {
        var llvmType = GetLLVMType(varDecl.Type ?? "int");
        var alloca = _builder.BuildAlloca(llvmType, varDecl.Name);
        _locals[varDecl.Name] = alloca;

        if (varDecl.Initializer is not null)
        {
            var value = EmitExpression(varDecl.Initializer);
            _builder.BuildStore(value, alloca);
        }
    }

    private void EmitIfStatement(IfStatementSyntax ifStmt)
    {
        var condition = EmitExpression(ifStmt.Condition);

        var currentFunc = _builder.InsertBlock.Parent;
        var thenBlock = currentFunc.AppendBasicBlock("then");
        var elseBlock = ifStmt.ElseClause is not null ? currentFunc.AppendBasicBlock("else") : currentFunc.AppendBasicBlock("else");
        var mergeBlock = currentFunc.AppendBasicBlock("ifend");

        _builder.BuildCondBr(condition, thenBlock, elseBlock);

        _builder.PositionAtEnd(thenBlock);
        EmitStatement(ifStmt.ThenStatement);
        _builder.BuildBr(mergeBlock);

        _builder.PositionAtEnd(elseBlock);
        if (ifStmt.ElseClause is not null)
            EmitStatement(ifStmt.ElseClause.Body);
        _builder.BuildBr(mergeBlock);

        _builder.PositionAtEnd(mergeBlock);
    }

    private void EmitWhileStatement(WhileStatementSyntax whileStmt)
    {
        var currentFunc = _builder.InsertBlock.Parent;
        var condBlock = currentFunc.AppendBasicBlock("whilecond");
        var bodyBlock = currentFunc.AppendBasicBlock("whilebody");
        var endBlock = currentFunc.AppendBasicBlock("whileend");

        _builder.BuildBr(condBlock);

        _builder.PositionAtEnd(condBlock);
        var condition = EmitExpression(whileStmt.Condition);
        _builder.BuildCondBr(condition, bodyBlock, endBlock);

        _builder.PositionAtEnd(bodyBlock);
        EmitStatement(whileStmt.Body);
        _builder.BuildBr(condBlock);

        _builder.PositionAtEnd(endBlock);
    }

    private void EmitForStatement(ForStatementSyntax forStmt)
    {
        EmitVariableDeclaration(forStmt.Initializer);

        var currentFunc = _builder.InsertBlock.Parent;
        var condBlock = currentFunc.AppendBasicBlock("forcond");
        var bodyBlock = currentFunc.AppendBasicBlock("forbody");
        var incBlock = currentFunc.AppendBasicBlock("forinc");
        var endBlock = currentFunc.AppendBasicBlock("forend");

        _builder.BuildBr(condBlock);

        _builder.PositionAtEnd(condBlock);
        var condition = EmitExpression(forStmt.Condition);
        _builder.BuildCondBr(condition, bodyBlock, endBlock);

        _builder.PositionAtEnd(bodyBlock);
        EmitStatement(forStmt.Body);
        _builder.BuildBr(incBlock);

        _builder.PositionAtEnd(incBlock);
        EmitExpression(forStmt.Increment);
        _builder.BuildBr(condBlock);

        _builder.PositionAtEnd(endBlock);
    }

    private static LLVMTypeRef GetLLVMType(string typeName)
    {
        return typeName switch
        {
            "void" => LLVMTypeRef.Void,
            "int" => LLVMTypeRef.Int32,
            "double" => LLVMTypeRef.Double,
            "bool" => LLVMTypeRef.Int1,
            "string" => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
            "char" => LLVMTypeRef.Int8,
            _ => LLVMTypeRef.Int32,
        };
    }

    public void Dispose()
    {
        _builder.Dispose();
        _module.Dispose();
    }
}
