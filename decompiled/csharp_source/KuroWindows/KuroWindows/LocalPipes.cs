using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KuroWindows;

internal static class LocalPipes
{
	public static NamedPipeServerStream CreateCurrentUserServer(string pipeName, PipeDirection direction)
	{
		PipeSecurity pipeSecurity = new PipeSecurity();
		using WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent();
		pipeSecurity.AddAccessRule(new PipeAccessRule(windowsIdentity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
		return NamedPipeServerStreamAcl.Create(pipeName, direction, -1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
	}
}
