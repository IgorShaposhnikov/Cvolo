using Cvolo.Analysis;
using Cvolo.Emitter.LLVM;
using Cvolo.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: cvolo <file.cv> [options]");
    Console.Error.WriteLine("  Options:");
    Console.Error.WriteLine("    --llvm         generate .ll LLVM IR only (no linking)");
    Console.Error.WriteLine("    --shared       build a shared library (.dll/.so)");
    Console.Error.WriteLine("    --emit-ir      print IR to stdout");
    Console.Error.WriteLine("    --emit-native  use native LLVM library (requires libLLVM)");
    return 1;
}

var filePath = args[0];
var emitIr = args.Contains("--emit-ir");
var useNative = args.Contains("--emit-native");
var emitLlvmOnly = args.Contains("--llvm");
var isShared = args.Contains("--shared");

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"Error: file '{filePath}' not found");
    return 1;
}

var sourceCode = File.ReadAllText(filePath);

var parser = new SyntaxParser();
var ast = parser.Parse(sourceCode);

if (parser.Diagnostics.HasErrors)
{
    Console.Error.WriteLine("Parse errors:");
    foreach (var diag in parser.Diagnostics.Diagnostics)
        Console.Error.WriteLine($"  {diag}");
    return 1;
}

var binder = new Binder();
binder.Bind(ast!);

if (binder.Diagnostics.HasErrors)
{
    Console.Error.WriteLine("Analysis errors:");
    foreach (var diag in binder.Diagnostics.Diagnostics)
        Console.Error.WriteLine($"  {diag}");
    return 1;
}

var llPath = Path.ChangeExtension(filePath, ".ll");

if (useNative)
{
    var codeGen = new CodeGenerator("cvolo_module");
    codeGen.Emit(ast!);

    if (emitIr)
        Console.WriteLine(codeGen.PrintIR());

    var outputPath = Path.ChangeExtension(filePath, ".o");
    codeGen.WriteObjectFile(outputPath);

    if (emitLlvmOnly)
    {
        if (emitIr)
            Console.WriteLine(codeGen.PrintIR());
        Console.WriteLine($"Generated: {outputPath}");
        return 0;
    }

    var binaryExt = isShared
        ? (OperatingSystem.IsWindows() ? ".dll" : ".so")
        : (OperatingSystem.IsWindows() ? ".exe" : "");
    var binaryPath = Path.ChangeExtension(filePath, null) + binaryExt;

    var typeFlag = isShared ? " -shared" : "";
    var linkerArgs = $"-o {binaryPath} {outputPath}{typeFlag}";
    var linkResult = System.Diagnostics.Process.Start("clang", linkerArgs);
    linkResult?.WaitForExit();

    if (linkResult?.ExitCode != 0)
    {
        Console.Error.WriteLine($"Linking failed with exit code {linkResult?.ExitCode}");
        return 1;
    }

    Console.WriteLine($"Built: {binaryPath}");
}
else
{
    var emitter = new IrEmitter();
    var ir = emitter.Emit(ast!);

    File.WriteAllText(llPath, ir);

    if (emitIr)
        Console.WriteLine(ir);

    if (emitLlvmOnly)
    {
        Console.WriteLine($"Generated: {llPath}");
        return 0;
    }

    // Try to link with clang
    var clangPath = FindTool("clang");
    if (clangPath is not null)
    {
        var binaryExt = isShared
            ? (OperatingSystem.IsWindows() ? ".dll" : ".so")
            : (OperatingSystem.IsWindows() ? ".exe" : "");
        var binaryPath = Path.ChangeExtension(filePath, null) + binaryExt;

        var typeFlag = isShared ? " -shared" : "";
        var subsystemFlag = OperatingSystem.IsWindows() && !isShared ? " -Xlinker /subsystem:console" : "";
        var linkResult = System.Diagnostics.Process.Start(clangPath, $"-o {binaryPath} {llPath}{typeFlag}{subsystemFlag}");
        linkResult?.WaitForExit();

        if (linkResult?.ExitCode == 0)
        {
            Console.WriteLine($"Built: {binaryPath}");
            return 0;
        }

        Console.Error.WriteLine($"Linking failed (exit code {linkResult?.ExitCode})");
        return 1;
    }

    Console.Error.WriteLine("Error: clang not found. Install LLVM tools to compile.");
    return 1;
}

return 0;

static string? FindTool(string name)
{
    var which = OperatingSystem.IsWindows() ? "where" : "which";
    try
    {
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = which,
            Arguments = name,
            RedirectStandardOutput = true,
        });
        if (proc is null) return null;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return proc.ExitCode == 0 && output.Length > 0 ? output : null;
    }
    catch
    {
        return null;
    }
}
