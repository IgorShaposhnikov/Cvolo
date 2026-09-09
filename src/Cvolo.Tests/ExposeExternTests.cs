using System.Runtime.InteropServices;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ExposeExternTests : CompilerTestBase
{
	private const string Category = "ExposeExtern";

	[Theory]
	[InlineData("ExposeRender", "127\n")]
	[InlineData("ExposeLinkage", "127\n16\n")]
	public void ExposeExtern_CompileAndRun(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("ExposeMonomorphicFail", "CVL1801")]
	[InlineData("ExposeNameMisplacedFail", "CVL1803")]
	[InlineData("ExposeDuplicateFail", "CVL1802")]
	public void ExposeExtern_BadInputRejected(string caseName, string expectedId)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
	}

	[Fact]
	public void ExposeExtern_LinkageGeneration_ProducesExportAlias()
	{
		var fileName = $"{Category}/ExposeLinkage.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var assemblyDir = Path.GetDirectoryName(typeof(ExposeExternTests).Assembly.Location)!;
		var llPath = Path.Combine(assemblyDir, "TestCases", "_isolated", Category, "ExposeLinkage", "obj", "Debug", "ExposeLinkage.ll");

		Assert.True(File.Exists(llPath), $"Expected generated LLVM IR file at '{llPath}' but it was missing.");
		var irContent = File.ReadAllText(llPath);

		Assert.Contains("@native_render_frame =", irContent);
		Assert.Contains("alias i32 (float), ptr @", irContent);
		Assert.Contains("RenderFrame_float", irContent);
		Assert.Contains("@RenderTick =", irContent);
		Assert.Contains("alias i32 (i32), ptr @", irContent);

		if (OperatingSystem.IsWindows())
			Assert.Contains("dllexport alias", irContent);
		else
			Assert.Contains("protected alias", irContent);
	}

	[DllImport("GraphicsCore.dll", EntryPoint = "native_render_frame", CallingConvention = CallingConvention.Cdecl)]
	private static extern int NativeRenderFrame(IntPtr windowHandle, float clearColor);

	[Fact]
	public void ExposeExtern_DllImportInvokesCvoloExport()
	{
		var fileName = $"InteropCSharp/GraphicsCore.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "--shared");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var assemblyDir = Path.GetDirectoryName(typeof(ExposeExternTests).Assembly.Location)!;
		var dllPath = Path.Combine(assemblyDir, "TestCases", "_isolated", "InteropCSharp", "GraphicsCore", "bin", "Debug", "GraphicsCore.dll");
		Assert.True(File.Exists(dllPath), $"Expected shared library at '{dllPath}' but it was missing.");

		if (!OperatingSystem.IsWindows())
			return;

		// Copy the freshly built library to the AppBase so DllImport resolves it.
		var copiedPath = Path.Combine(assemblyDir, "GraphicsCore.dll");
		File.Copy(dllPath, copiedPath, overwrite: true);
		try
		{
			Assert.Equal(127, NativeRenderFrame(IntPtr.Zero, 0.5f));
		}
		finally
		{
			try { File.Delete(copiedPath); } catch { }
		}
	}
}
