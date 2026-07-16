using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace GeminiDesk;

internal sealed class WindowsCredentialStore(string targetName)
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobSize = 5 * 512;

    private readonly string _targetName = targetName;

    public bool TryRead(out string secret)
    {
        secret = string.Empty;

        if (!CredRead(
                _targetName,
                CredentialTypeGeneric,
                0,
                out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return false;
            }

            throw new Win32Exception(error);
        }

        byte[]? secretBytes = null;

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return false;
            }

            if (credential.CredentialBlobSize > MaxCredentialBlobSize ||
                credential.CredentialBlobSize % sizeof(char) != 0)
            {
                throw new InvalidDataException("저장된 API 키 데이터가 올바르지 않습니다.");
            }

            secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(
                credential.CredentialBlob,
                secretBytes,
                0,
                secretBytes.Length);
            secret = Encoding.Unicode.GetString(secretBytes);
            return !string.IsNullOrEmpty(secret);
        }
        finally
        {
            if (secretBytes is not null)
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }

            CredFree(credentialPointer);
        }
    }

    public void Write(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaxCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "API 키가 Windows 자격 증명 저장 한도를 초과했습니다.");
        }

        var secretPointer = Marshal.AllocHGlobal(secretBytes.Length);

        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = _targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = "GeminiDesk"
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.Copy(new byte[secretBytes.Length], 0, secretPointer, secretBytes.Length);
            Marshal.FreeHGlobal(secretPointer);
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public void Delete()
    {
        if (CredDelete(_targetName, CredentialTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public FILETIME LastWritten;
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
}
