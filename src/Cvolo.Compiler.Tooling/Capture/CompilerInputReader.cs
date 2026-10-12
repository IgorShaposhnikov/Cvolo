namespace Cvolo.Compiler.Tooling;

/// <summary>
/// The single file-access abstraction used by <see cref="CvoloWorkspace.CaptureUniverseV1"/> while it
/// materializes a frozen universe. Every semantic input the capture consumes is read through this
/// interface so a caller can observe exactly which bytes the universe was built from and can supply
/// editor overlays, deterministic fixtures, or a read-deny provider.
/// </summary>
public interface ICompilerInputReader
{
	/// <summary>
	/// Reads the full UTF-8 text of the file at <paramref name="absolutePath"/>.
	/// </summary>
	string ReadAllText(string absolutePath);

	/// <summary>
	/// Returns whether a file exists at <paramref name="absolutePath"/>.
	/// </summary>
	bool FileExists(string absolutePath);
}

/// <summary>
/// The production <see cref="ICompilerInputReader"/>: reads from the local file system.
/// </summary>
public sealed class FileSystemCompilerInputReader : ICompilerInputReader
{
	/// <summary>
	/// A shared, stateless instance.
	/// </summary>
	public static readonly FileSystemCompilerInputReader Instance = new();

	private FileSystemCompilerInputReader()
	{
	}

	public string ReadAllText(string absolutePath) => File.ReadAllText(absolutePath);

	public bool FileExists(string absolutePath) => File.Exists(absolutePath);
}

/// <summary>
/// Wraps another reader and records every path it reads. Used for instrumentation and tests that
/// assert the exact read-set a capture consumed.
/// </summary>
public sealed class RecordingCompilerInputReader(ICompilerInputReader inner) : ICompilerInputReader
{
	private readonly List<string> _reads = [];
	private readonly object _gate = new();

	/// <summary>
	/// The paths read so far, in the order they were first accessed.
	/// </summary>
	public IReadOnlyList<string> Reads
	{
		get
		{
			lock (_gate)
				return [.. _reads];
		}
	}

	public string ReadAllText(string absolutePath)
	{
		Record(absolutePath);
		return inner.ReadAllText(absolutePath);
	}

	public bool FileExists(string absolutePath)
	{
		Record(absolutePath);
		return inner.FileExists(absolutePath);
	}

	private void Record(string path)
	{
		lock (_gate)
			_reads.Add(path);
	}
}
