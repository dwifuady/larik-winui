using System.Runtime.InteropServices;
using System.Text;

namespace Larik.Services;

/// <summary>
/// Provider-declared password fields live in Windows Credential Manager, protected by the current
/// user's profile. The SQLite session store only ever receives non-secret settings.
/// </summary>
public static class SecretStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>Stable, app-scoped target name for one connection setting.</summary>
    public static string Target(string spaceId, string settingKey) =>
        $"Larik/Connection/{spaceId}/{settingKey}";

    public static void Save(string target, string userName, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                Comment = "Larik SQL Studio connection secret",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = string.IsNullOrWhiteSpace(userName) ? "Larik SQL Studio" : userName,
            };

            CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, CredentialTypeGeneric, 0)
            && Marshal.GetLastWin32Error() != ErrorNotFound)
        {
            // Best effort: the connection can still be configured again.
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;

        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;

        public uint CredentialBlobSize;

        public IntPtr CredentialBlob;

        public uint Persist;

        public uint AttributeCount;

        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
