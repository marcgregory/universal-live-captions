using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UniversalCaptions.App.Settings;

/// <summary>
/// Production <see cref="ICredentialStore"/> backed by the Windows Credential Manager
/// (advapi32 <c>CredWriteW</c> / <c>CredReadW</c> / <c>CredDeleteW</c>). Credentials are stored
/// per-user under <see cref="CRED_TYPE_GENERIC"/> with <see cref="CRED_PERSIST_LOCAL_MACHINE"/>
/// persistence (roams with the user profile; not visible to other users on the machine).
///
/// This class never logs credential values, never includes them in exception messages, and never
/// surfaces them back through the UI. All methods swallow <see cref="Win32Exception"/> /
/// <see cref="DllNotFoundException"/> and return the documented sentinel (false / null / true-no-op)
/// on failure — matching the tolerant policy enforced by <see cref="SettingsStore"/> so callers
/// never have to wrap calls in try/catch.
/// </summary>
internal sealed class WindowsCredentialStore : ICredentialStore
{
    // Windows Credential Manager constants (see
    // https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw).
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int reservedFlag);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr credentialPtr);

    /// <inheritdoc />
    public bool HasCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        // CredRead + CredFree is the cheapest way to check existence; the alternative — a separate
        // enumeration API — would require more P/Invoke surface and offers no benefit here.
        try
        {
            if (CredRead(key, CRED_TYPE_GENERIC, 0, out IntPtr credentialPtr))
            {
                CredFree(credentialPtr);
                return true;
            }
            return false;
        }
        catch (DllNotFoundException)
        {
            // advapi32 unavailable (should not happen on Windows 10 build 17763+ but defensive).
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? TryGetCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        try
        {
            if (!CredRead(key, CRED_TYPE_GENERIC, 0, out IntPtr credentialPtr))
            {
                int error = Marshal.GetLastWin32Error();
                return error == ERROR_NOT_FOUND ? null : null;
            }
            try
            {
                CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return string.Empty;
                }
                byte[] blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool SetCredential(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || value is null)
        {
            return false;
        }
        try
        {
            byte[] blob = Encoding.UTF8.GetBytes(value);
            IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                CREDENTIAL credential = new()
                {
                    Flags = 0,
                    Type = CRED_TYPE_GENERIC,
                    TargetName = Marshal.StringToCoTaskMemUni(key),
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = Marshal.StringToCoTaskMemUni(Environment.UserName),
                };
                try
                {
                    return CredWrite(ref credential, 0);
                }
                finally
                {
                    if (credential.TargetName != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(credential.TargetName);
                    }
                    if (credential.UserName != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(credential.UserName);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(blobPtr);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool RemoveCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        try
        {
            if (CredDelete(key, CRED_TYPE_GENERIC, 0))
            {
                return true;
            }
            int error = Marshal.GetLastWin32Error();
            // ERROR_NOT_FOUND is treated as success — the desired post-state is "no credential".
            return error == ERROR_NOT_FOUND;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
