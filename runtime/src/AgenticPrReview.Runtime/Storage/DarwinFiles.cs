using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime;

// Darwin's flags and 64-bit inode ABI differ from Linux, on both arm64 and x64.
// See Apple XNU sys/{fcntl,stat,dirent}.h. Keep descriptor-relative operations
// here rather than treating /dev/fd as Linux's /proc/self/fd directory anchor.
internal static partial class DarwinFiles
{
    internal const int NonBlocking = 4;
    internal const int NoFollow = 0x100;
    internal const int NoFollowAny = 0x20000000;
    internal const int CloseOnExec = 0x1000000;
    internal const int Directory = 0x100000;
    internal const int Create = 0x200;
    internal const int Exclusive = 0x800;
    internal const int RemoveDirectory = 0x80;
    internal const uint RenameExclusive = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct Information
    {
        [FieldOffset(0)] internal uint Device;
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(6)] internal ushort Links;
        [FieldOffset(8)] internal ulong Inode;
        [FieldOffset(16)] internal uint UserId;
        [FieldOffset(96)] internal long Size;
    }

    [LibraryImport("libc", EntryPoint = "fstat64", SetLastError = true)]
    internal static partial int Stat(int descriptor, out Information information);

    // Apple's openat wrapper forwards to this fixed-arity entry. On Darwin
    // arm64 a variadic mode travels on the stack; a fixed P/Invoke to openat
    // would instead create files with unrelated mode bits. See Apple's
    // libsyscall/wrappers/open-base.c and the SDK libsystem_kernel exports.
    [LibraryImport("libc", EntryPoint = "__openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle directory, string name, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "__openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenAtFixed(int directory, string name, int flags, uint mode);

    internal static SafeFileHandle OpenChild(SafeFileHandle directory, string name, bool isDirectory = false, bool create = false)
    {
        var descriptor = OpenChildDescriptor(directory, name, isDirectory, create);
        if (descriptor < 0) throw new IOException("The Darwin child could not be opened.");
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    internal static int OpenChildDescriptor(SafeFileHandle directory, string name, bool isDirectory = false, bool create = false)
    {
        RequireName(name);
        var flags = NoFollow | CloseOnExec | NonBlocking |
            (isDirectory ? Directory : 0) | (create ? 2 | Create | Exclusive : 0);
        return OpenAt(directory, name, flags, 0x180);
    }

    [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(SafeFileHandle sourceDirectory, string source, SafeFileHandle destinationDirectory, string destination, uint flags);

    internal static void MoveChild(SafeFileHandle directory, string source, string destination, bool overwrite = false)
    {
        RequireName(source);
        RequireName(destination);
        if (RenameAt(directory, source, directory, destination, overwrite ? 0 : RenameExclusive) != 0)
            throw new IOException("The Darwin child could not be committed without replacement.");
    }

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(SafeFileHandle directory, string name, int flags);

    internal static void DeleteChild(SafeFileHandle directory, string name, bool isDirectory = false)
    {
        RequireName(name);
        if (UnlinkAt(directory, name, isDirectory ? RemoveDirectory : 0) != 0 && Marshal.GetLastPInvokeError() != 2)
            throw new IOException("The Darwin child could not be deleted.");
    }

    internal static bool ChildExists(SafeFileHandle directory, string name)
    {
        RequireName(name);
        var descriptor = OpenAt(directory, name, NoFollow | CloseOnExec | NonBlocking, 0);
        if (descriptor < 0) return Marshal.GetLastPInvokeError() != 2;
        new SafeFileHandle((nint)descriptor, ownsHandle: true).Dispose();
        return true;
    }

    private static void RequireName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new IOException("Invalid Darwin child name.");
    }

    internal static IEnumerable<string> EnumerateNames(SafeFileHandle directory)
    {
        // openat(".") supplies an independent enumeration cursor, retaining the
        // same directory even after its pathname has been moved or replaced.
        var descriptor = OpenAt(directory, ".", Directory | CloseOnExec, 0);
        if (descriptor < 0) throw new IOException("The Darwin directory could not be enumerated.");
        var stream = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? OpenDirectoryStreamX64(descriptor) : OpenDirectoryStream(descriptor);
        if (stream == 0)
        {
            new SafeFileHandle((nint)descriptor, ownsHandle: true).Dispose();
            throw new IOException("The Darwin directory stream could not be opened.");
        }
        try
        {
            while (true)
            {
                var entry = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? ReadDirectoryX64(stream) : ReadDirectory(stream);
                if (entry == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                        throw new IOException("The Darwin directory stream failed.");
                    yield break;
                }
                var length = (ushort)Marshal.ReadInt16(entry, 18);
                var recordLength = (ushort)Marshal.ReadInt16(entry, 16);
                if (length is 0 or > 1023 || recordLength < 22 + length || Marshal.ReadByte(entry, 21 + length) != 0)
                    throw new IOException("Invalid Darwin directory entry.");
                var bytes = new byte[length];
                Marshal.Copy(entry + 21, bytes, 0, length);
                var name = StrictUtf8.GetString(bytes);
                if (name is "." or "..") continue;
                RequireName(name);
                yield return name;
            }
        }
        finally { _ = CloseDirectoryStream(stream); }
    }

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial nint OpenDirectoryStream(int descriptor);
    [LibraryImport("libc", EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    private static partial nint OpenDirectoryStreamX64(int descriptor);
    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial nint ReadDirectory(nint stream);
    [LibraryImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)]
    private static partial nint ReadDirectoryX64(nint stream);
    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static partial int CloseDirectoryStream(nint stream);

    internal static SafeFileHandle CreateChildDirectory(SafeFileHandle directory, string name, bool exclusive = false)
    {
        RequireName(name);
        if (MakeDirectoryAt(directory, name, 0x1c0) != 0 &&
            (exclusive || Marshal.GetLastPInvokeError() != 17))
            throw new IOException("The Darwin child directory could not be created.");
        return OpenChild(directory, name, isDirectory: true);
    }

    internal static void SetMode(SafeFileHandle handle, uint mode)
    {
        if (ChangeMode(handle, mode) != 0)
            throw new IOException("The Darwin retained entry mode could not be set.");
    }

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectoryAt(SafeFileHandle directory, string name, uint mode);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int ChangeMode(SafeFileHandle handle, uint mode);
}
