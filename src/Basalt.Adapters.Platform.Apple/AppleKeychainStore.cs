using System.Runtime.InteropServices;
using Basalt.Application.Ports.Security;
using Basalt.Domain.Common;

#pragma warning disable CA2101 // Security.framework takes UTF-8 C strings

namespace Basalt.Adapters.Platform.Apple;

/// <summary>
/// Apple Keychain via <c>SecItem*</c> (spec 03 §1.5), valid on macOS and iOS. The token is stored as a generic password
/// with <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c>, so background sync works on iOS and the item never leaves
/// the device.
/// </summary>
internal sealed class AppleKeychainStore : ISecretStore
{
    private const string Service = "Basalt";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;

    public Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var values = new AppleKeychainValues(key, secret);
        var status = SecItemAdd(values.AddDictionary(), out var added);
        if (added != IntPtr.Zero)
        {
            _ = CFRelease(added);
        }

        if (status == DuplicateItem)
        {
            status = SecItemUpdate(values.QueryDictionary(), values.DataAttributes());
        }

        return Task.FromResult(status == Success
            ? Result<Unit>.Success(Unit.Value)
            : Result<Unit>.Failure(Failure(SecretErrors.StoreFailed, "storing the token", status)));
    }

    public Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var values = new AppleKeychainValues(key, null);
        var status = SecItemCopyMatching(values.ReadDictionary(), out var data);
        if (status == ItemNotFound)
        {
            return Task.FromResult(Result<byte[]?>.Success(null));
        }

        if (status != Success)
        {
            return Task.FromResult(Result<byte[]?>.Failure(Failure(SecretErrors.ReadFailed, "reading the token", status)));
        }

        try
        {
            var length = (int)CFDataGetLength(data);
            var bytes = new byte[length];
            if (length > 0)
            {
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            }

            return Task.FromResult(Result<byte[]?>.Success(bytes));
        }
        finally
        {
            _ = CFRelease(data);
        }
    }

    public Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var values = new AppleKeychainValues(key, null);
        var status = SecItemDelete(values.QueryDictionary());
        return Task.FromResult(status is Success or ItemNotFound
            ? Result<Unit>.Success(Unit.Value)
            : Result<Unit>.Failure(Failure(SecretErrors.DeleteFailed, "deleting the token", status)));
    }

    private static Error Failure(string code, string operation, int status) =>
        new(code, $"{operation} in the Keychain failed with OSStatus {status}.");

    /// <summary>Creates and owns the CFString/CFData values of one Keychain call.</summary>
    private sealed class AppleKeychainValues : IDisposable
    {
        private const uint Utf8 = 0x08000100;
        private readonly List<IntPtr> _values = [];
        private readonly IntPtr _service;
        private readonly IntPtr _account;
        private readonly IntPtr _data;
        private readonly IntPtr _class;
        private readonly IntPtr _classValue;
        private readonly IntPtr _accountAttribute;
        private readonly IntPtr _serviceAttribute;
        private readonly IntPtr _dataAttribute;
        private readonly IntPtr _accessibleAttribute;
        private readonly IntPtr _accessibleValue;
        private readonly IntPtr _returnData;
        private readonly IntPtr _matchLimit;
        private readonly IntPtr _matchLimitOne;
        private readonly IntPtr _true;

        public AppleKeychainValues(string key, ReadOnlyMemory<byte>? secret)
        {
            _class = String("class");
            _classValue = String("genp");
            _serviceAttribute = String("svce");
            _accountAttribute = String("acct");
            _dataAttribute = String("v_Data");
            _accessibleAttribute = String("pdmn");
            _accessibleValue = String("cku");
            _returnData = String("r_Data");
            _matchLimit = String("m_Limit");
            _matchLimitOne = String("m_LimitOne");
            _service = String(Service);
            _account = String(key);
            _true = Constant("kCFBooleanTrue");
            if (secret is { } bytes)
            {
                _data = CFDataCreate(IntPtr.Zero, bytes.ToArray(), bytes.Length);
                Track(_data);
            }
            else
            {
                _data = IntPtr.Zero;
            }
        }

        public IntPtr QueryDictionary() =>
            Dictionary((_class, _classValue), (_serviceAttribute, _service), (_accountAttribute, _account));

        public IntPtr DataAttributes() =>
            Dictionary((_dataAttribute, _data), (_accessibleAttribute, _accessibleValue));

        public IntPtr AddDictionary() =>
            Dictionary(
                (_class, _classValue),
                (_serviceAttribute, _service),
                (_accountAttribute, _account),
                (_dataAttribute, _data),
                (_accessibleAttribute, _accessibleValue));

        public IntPtr ReadDictionary() =>
            Dictionary(
                (_class, _classValue),
                (_serviceAttribute, _service),
                (_accountAttribute, _account),
                (_returnData, _true),
                (_matchLimit, _matchLimitOne));

        public void Dispose()
        {
            foreach (var value in _values)
            {
                _ = CFRelease(value);
            }
        }

        private IntPtr String(string value)
        {
            var pointer = CFStringCreateWithCString(IntPtr.Zero, value, Utf8);
            Track(pointer);
            return pointer;
        }

        private void Track(IntPtr pointer)
        {
            if (pointer != IntPtr.Zero)
            {
                _values.Add(pointer);
            }
        }

        private IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            var keys = new IntPtr[pairs.Length];
            var values = new IntPtr[pairs.Length];
            for (var index = 0; index < pairs.Length; index++)
            {
                keys[index] = pairs[index].Key;
                values[index] = pairs[index].Value;
            }

            var dictionary = CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, IntPtr.Zero, IntPtr.Zero);
            Track(dictionary);
            return dictionary;
        }
    }

    private static IntPtr Constant(string name)
    {
        var framework = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        var symbol = NativeLibrary.GetExport(framework, name);
        return Marshal.ReadIntPtr(symbol);
    }

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator,
        IntPtr[] keys,
        IntPtr[] values,
        nint count,
        IntPtr keyCallBacks,
        IntPtr valueCallBacks);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRelease(IntPtr value);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemAdd(IntPtr attributes, out IntPtr result);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecItemDelete(IntPtr query);
}
