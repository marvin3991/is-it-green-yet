using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IsItGreenYet.Core.Auth;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace IsItGreenYet.App.Services;

/// <summary>
/// Windows 認證管理員（Credential Manager）的 <see cref="ICredentialStore"/> 實作（spec §13.3）。
/// 一般認證（CRED_TYPE_GENERIC）、只存在這台電腦（CRED_PERSIST_LOCAL_MACHINE，不漫遊）。
/// </summary>
/// <remarks>
/// <see cref="Read"/> 找不到（ERROR_NOT_FOUND）回 null，其他失敗丟 <see cref="Win32Exception"/>；
/// Core 的 PatTokenProvider 依這個約定把例外轉成給使用者看的錯誤文字。
/// 這裡丟出的例外訊息只有 Windows 的錯誤說明或長度，絕不帶 secret 的內容（spec §13.4）。
/// </remarks>
internal sealed class WindowsCredentialStore : ICredentialStore
{
    // 以下數值出自 Windows SDK 的 wincred.h 與 winerror.h。
    private const uint CredTypeGeneric = 1;                  // CRED_TYPE_GENERIC
    private const uint CredPersistLocalMachine = 2;          // CRED_PERSIST_LOCAL_MACHINE
    private const int CredMaxCredentialBlobSize = 5 * 512;   // CRED_MAX_CREDENTIAL_BLOB_SIZE = 2,560 bytes
    private const int ErrorNotFound = 1168;                  // ERROR_NOT_FOUND

    /// <summary>CredReadW／CredDeleteW 的 Flags 參數保留未用，必須是 0。</summary>
    private const uint NoFlags = 0;

    /// <summary>
    /// blob 用 UTF-16LE：和 Windows「認證管理員」與 cmdkey 存一般認證密碼的方式一致。
    /// 讀回時同樣用 UTF-16LE 解碼。
    /// </summary>
    private static readonly Encoding BlobEncoding = Encoding.Unicode;

    public string? Read(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!NativeMethods.CredRead(target, CredTypeGeneric, NoFlags, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                // 尚未設定（spec §13.3）。
                return null;
            }

            throw new Win32Exception(error);
        }

        // CredReadW 配置的緩衝區一律在 finally 釋放（spec §13.3）。
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            var size = checked((int)credential.CredentialBlobSize);
            if (size == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[size];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, size);
                return BlobEncoding.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    /// <exception cref="ArgumentException">secret 是空的，或編碼後超過 2,560 bytes。</exception>
    /// <exception cref="Win32Exception">CredWriteW 失敗。</exception>
    public void Write(string target, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var bytes = BlobEncoding.GetBytes(secret);
        var blob = IntPtr.Zero;
        var targetName = IntPtr.Zero;
        try
        {
            // 寫入前檢查長度（spec §13.3）；訊息只寫長度，不寫內容。
            if (bytes.Length > CredMaxCredentialBlobSize)
            {
                throw new ArgumentException(
                    $"要存的內容編碼後有 {bytes.Length} bytes，超過 Windows 認證管理員的上限 {CredMaxCredentialBlobSize} bytes。",
                    nameof(secret));
            }

            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            targetName = Marshal.StringToHGlobalUni(target);

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
            };

            if (!NativeMethods.CredWrite(ref credential, NoFlags))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            // secret 的明文副本用完立刻清掉，不等 GC。
            CryptographicOperations.ZeroMemory(bytes);
            if (blob != IntPtr.Zero)
            {
                ClearUnmanaged(blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }

            if (targetName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(targetName);
            }
        }
    }

    /// <summary>刪除；本來就不存在（ERROR_NOT_FOUND）也算成功，「清除 PAT」可以重複按。</summary>
    /// <exception cref="Win32Exception">CredDeleteW 因其他原因失敗。</exception>
    public void Delete(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (NativeMethods.CredDelete(target, CredTypeGeneric, NoFlags))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
    }

    private static void ClearUnmanaged(IntPtr buffer, int length)
    {
        for (var offset = 0; offset < length; offset++)
        {
            Marshal.WriteByte(buffer, offset, 0);
        }
    }

    /// <summary>
    /// CREDENTIALW（wincred.h）。字串欄位都用指標，由這裡自己配置與釋放，不交給封送處理器，
    /// 讀取時也不會把 CredReadW 緩衝區裡的字串複製成受控字串。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static class NativeMethods
    {
        private const string Advapi32 = "advapi32.dll";

        [DllImport(Advapi32, EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

        [DllImport(Advapi32, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport(Advapi32, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string targetName, uint type, uint flags);

        [DllImport(Advapi32, EntryPoint = "CredFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void CredFree(IntPtr buffer);
    }
}
