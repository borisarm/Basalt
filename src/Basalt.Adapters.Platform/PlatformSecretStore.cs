using System.Runtime.InteropServices;
using System.Text;
using Basalt.Application.Ports.Security;
using Basalt.Domain.Common;

#pragma warning disable CA2101 // Apple Keychain legacy APIs take UTF-8 C strings

namespace Basalt.Adapters.Platform;

/// <summary>
/// OS-protected token storage (spec 03 §1.5): Windows Credential Manager, Apple Keychain, and an explicit failure on
/// platforms without a supported secure store.
/// </summary>
internal sealed class PlatformSecretStore : ISecretStore
{
    private readonly ISecretStore _inner =
        OperatingSystem.IsWindows() ? new WindowsCredentialStore()
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? new AppleKeychainStore()
        : new UnsupportedSecretStore();

    public Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct) =>
        _inner.SetAsync(key, secret, ct);

    public Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct) => _inner.GetAsync(key, ct);

    public Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct) => _inner.DeleteAsync(key, ct);

    private sealed class UnsupportedSecretStore : ISecretStore
    {
        public Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct) =>
            Task.FromResult(Result<Unit>.Failure(Unsupported()));

        public Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult(Result<byte[]?>.Failure(Unsupported()));

        public Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct) =>
            Task.FromResult(Result<Unit>.Failure(Unsupported()));

        private static Error Unsupported() =>
            new(SecretErrors.Unsupported, "This platform has no secure secret store.");
    }

    private sealed class WindowsCredentialStore : ISecretStore
    {
        private const uint GenericCredential = 1;
        private const uint LocalMachinePersistence = 2;
        private const int ErrorNotFound = 1168;

        public Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var blob = secret.IsEmpty ? IntPtr.Zero : Marshal.AllocHGlobal(secret.Length);
            try
            {
                if (!secret.IsEmpty)
                {
                    Marshal.Copy(secret.ToArray(), 0, blob, secret.Length);
                }

                var credential = new Credential
                {
                    Type = GenericCredential,
                    TargetName = key,
                    CredentialBlobSize = (uint)secret.Length,
                    CredentialBlob = blob,
                    Persist = LocalMachinePersistence,
                    UserName = Environment.UserName,
                };
                return Task.FromResult(CredWrite(ref credential, 0)
                    ? Result<Unit>.Success(Unit.Value)
                    : Failure(SecretErrors.StoreFailed, "CredWrite failed."));
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return Task.FromResult(Result<Unit>.Failure(
                    new Error(SecretErrors.Unsupported, exception.Message)));
            }
            finally
            {
                if (blob != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(blob);
                }
            }
        }

        public Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!CredRead(key, GenericCredential, 0, out var handle))
            {
                var error = Marshal.GetLastWin32Error();
                return Task.FromResult(error == ErrorNotFound
                    ? Result<byte[]?>.Success(null)
                    : Result<byte[]?>.Failure(Failure(SecretErrors.ReadFailed, "CredRead failed.").Error));
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(handle);
                var secret = new byte[credential.CredentialBlobSize];
                if (secret.Length > 0)
                {
                    Marshal.Copy(credential.CredentialBlob, secret, 0, secret.Length);
                }

                return Task.FromResult(Result<byte[]?>.Success(secret));
            }
            finally
            {
                CredFree(handle);
            }
        }

        public Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (CredDelete(key, GenericCredential, 0))
            {
                return Task.FromResult(Result<Unit>.Success(Unit.Value));
            }

            return Task.FromResult(Marshal.GetLastWin32Error() == ErrorNotFound
                ? Result<Unit>.Success(Unit.Value)
                : Failure(SecretErrors.DeleteFailed, "CredDelete failed."));
        }

        private static Result<Unit> Failure(string code, string message) =>
            new Error(code, message + " Win32 error " + Marshal.GetLastWin32Error());

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);
    }

    private sealed class AppleKeychainStore : ISecretStore
    {
        private const string Service = "Basalt";
        private const int Success = 0;
        private const int ItemNotFound = -25300;
        private const int DuplicateItem = -25299;

        public Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var data = secret.ToArray();
            var find = SecKeychainFindGenericPassword(
                IntPtr.Zero, Length(Service), Service, Length(key), key, out _, out _, out var item);
            if (find == DuplicateItem || find == Success && item != IntPtr.Zero)
            {
                var modify = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data);
                Release(item);
                return Task.FromResult(modify == Success
                    ? Result<Unit>.Success(Unit.Value)
                    : Failure(SecretErrors.StoreFailed, "SecKeychainItemModifyAttributesAndData", modify));
            }

            var add = SecKeychainAddGenericPassword(
                IntPtr.Zero, Length(Service), Service, Length(key), key, (uint)data.Length, data, out item);
            if (item != IntPtr.Zero)
            {
                Release(item);
            }

            return Task.FromResult(add is Success or DuplicateItem
                ? Result<Unit>.Success(Unit.Value)
                : Failure(SecretErrors.StoreFailed, "SecKeychainAddGenericPassword", add));
        }

        public Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, Length(Service), Service, Length(key), key,
                out var length, out var password, out var item);
            if (status == ItemNotFound)
            {
                return Task.FromResult(Result<byte[]?>.Success(null));
            }

            if (status != Success)
            {
                return Task.FromResult(Result<byte[]?>.Failure(Failure(SecretErrors.ReadFailed, "find", status).Error));
            }

            try
            {
                var secret = new byte[length];
                if (length > 0)
                {
                    Marshal.Copy(password, secret, 0, (int)length);
                }

                return Task.FromResult(Result<byte[]?>.Success(secret));
            }
            finally
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, password);
                if (item != IntPtr.Zero)
                {
                    Release(item);
                }
            }
        }

        public Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, Length(Service), Service, Length(key), key, out _, out var password, out var item);
            if (status == ItemNotFound)
            {
                return Task.FromResult(Result<Unit>.Success(Unit.Value));
            }

            if (status != Success)
            {
                return Task.FromResult(Result<Unit>.Failure(Failure(SecretErrors.DeleteFailed, "find", status).Error));
            }

            try
            {
                var delete = SecKeychainItemDelete(item);
                return Task.FromResult(delete == Success
                    ? Result<Unit>.Success(Unit.Value)
                    : Failure(SecretErrors.DeleteFailed, "SecKeychainItemDelete", delete));
            }
            finally
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, password);
                if (item != IntPtr.Zero)
                {
                    Release(item);
                }
            }
        }

        private static uint Length(string value) => (uint)Encoding.UTF8.GetByteCount(value);

        private static void Release(IntPtr item) => _ = CFRelease(item);

        private static Result<Unit> Failure(string code, string operation, int status) =>
            new Error(code, $"{operation} failed with OSStatus {status}.");

        [DllImport("/System/Library/Frameworks/Security.framework/Security", CharSet = CharSet.Ansi)]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            [MarshalAs(UnmanagedType.LPStr)] string serviceName,
            uint accountNameLength,
            [MarshalAs(UnmanagedType.LPStr)] string accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [DllImport("/System/Library/Frameworks/Security.framework/Security", CharSet = CharSet.Ansi)]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            [MarshalAs(UnmanagedType.LPStr)] string serviceName,
            uint accountNameLength,
            [MarshalAs(UnmanagedType.LPStr)] string accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport("/System/Library/Frameworks/Security.framework/Security", CharSet = CharSet.Ansi)]
        private static extern int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attrList,
            uint length,
            byte[] data);

        [DllImport("/System/Library/Frameworks/Security.framework/Security", CharSet = CharSet.Ansi)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport("/System/Library/Frameworks/Security.framework/Security", CharSet = CharSet.Ansi)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern IntPtr CFRelease(IntPtr value);
    }
}
