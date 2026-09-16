namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Opaque, workspace-session-local identifier for a project.
/// Instances are created only by a <see cref="CvoloWorkspace"/>; the underlying value and
/// session token make IDs across workspaces structurally distinct.
/// </summary>
public readonly record struct ProjectId
{
	internal int Value { get; }
	internal Guid WorkspaceSession { get; }

	internal ProjectId(Guid workspaceSession, int value)
	{
		WorkspaceSession = workspaceSession;
		Value = value;
	}
}
