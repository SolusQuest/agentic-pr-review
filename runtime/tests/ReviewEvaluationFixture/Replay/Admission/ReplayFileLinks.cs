using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

// Existing Runtime open/type primitives do not expose link counts. This narrow helper adds only that check.
internal static class ReplayFileLinks
{
    internal static bool IsSingleLink(SafeFileHandle handle) => OperatingSystem.IsWindows()
        ? GetFileInformationByHandle(handle, out var windows) && windows.Links == 1
        : OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            FStat(checked((int)handle.DangerousGetHandle()), out var linux) == 0 && linux.Links == 1;

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsInformation { [FieldOffset(40)] internal uint Links; }

    // Linux x64 stat layout, matching the existing Runtime's LinuxFileInformation.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxInformation { [FieldOffset(16)] internal ulong Links; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsInformation information);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, out LinuxInformation information);
}
