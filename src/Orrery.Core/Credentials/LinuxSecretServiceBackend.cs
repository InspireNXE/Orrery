using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Orrery.Core.Credentials;

/// <summary>
/// The Secret Service on Linux (GNOME Keyring, KWallet) through libsecret. Secrets are stored as text under
/// the schema <c>org.orrery.Credential</c> with one attribute, <c>key</c>, in the default collection.
/// Uses the <c>…v_sync</c> calls, which take a GLib hash table rather than C varargs, so they're safe to
/// call from .NET on every architecture.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretServiceBackend : ISecretBackend
{
    const string LibSecret = "libsecret-1.so.0";
    const string LibGlib = "libglib-2.0.so.0";
    const string SchemaName = "org.orrery.Credential";
    const string KeyAttribute = "key";

    readonly IntPtr _schema;
    readonly IntPtr _strHash, _strEqual;

    LinuxSecretServiceBackend(IntPtr glib)
    {
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
        _schema = BuildSchema();
    }

    public string Name => "the Secret Service keyring";

    public int? MaxSecretSize => null;

    /// <summary>
    /// The backend, or null when libsecret isn't installed or no Secret Service answers (a headless server,
    /// a minimal window manager); the vault file is used then.
    /// </summary>
    public static LinuxSecretServiceBackend? TryCreate()
    {
        if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(LibGlib, out var glib))
        {
            return null;
        }

        try
        {
            var backend = new LinuxSecretServiceBackend(glib);
            backend.Read("orrery:probe"); // Throws when there's no Secret Service to talk to.
            return backend;
        }
        catch (Exception e) when (e is IOException or EntryPointNotFoundException or DllNotFoundException)
        {
            Trace.TraceWarning($"The Secret Service isn't usable, so the vault file is used: {e.Message}");
            return null;
        }
    }

    public void Write(string key, byte[] secret)
    {
        // libsecret takes the password as a NUL-terminated string; the secret is UTF-8 JSON.
        var password = new byte[secret.Length + 1];
        secret.CopyTo(password, 0);
        try
        {
            WithAttributes(key, attributes =>
            {
                int ok = secret_password_storev_sync(_schema, attributes, "default", $"Orrery: {key}", password, IntPtr.Zero, out var error);
                ThrowIfError(error, "save to");
                if (ok == 0) throw new IOException("Couldn't save to the Secret Service keyring.");
                return 0;
            });
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public byte[]? Read(string key) => WithAttributes(key, attributes =>
    {
        var text = secret_password_lookupv_sync(_schema, attributes, IntPtr.Zero, out var error);
        ThrowIfError(error, "read from");
        if (text == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int length = 0;
            while (Marshal.ReadByte(text, length) != 0) length++;
            var secret = new byte[length];
            Marshal.Copy(text, secret, 0, length);
            return secret;
        }
        finally
        {
            secret_password_free(text); // Clears the memory, then frees it.
        }
    });

    public void Delete(string key) => WithAttributes(key, attributes =>
    {
        secret_password_clearv_sync(_schema, attributes, IntPtr.Zero, out var error);
        ThrowIfError(error, "delete from");
        return 0;
    });

    /// <summary>Runs <paramref name="call"/> with a GHashTable holding { key: <paramref name="key"/> }.</summary>
    T WithAttributes<T>(string key, Func<IntPtr, T> call)
    {
        var name = Marshal.StringToCoTaskMemUTF8(KeyAttribute);
        var value = Marshal.StringToCoTaskMemUTF8(key);
        var table = g_hash_table_new(_strHash, _strEqual);
        try
        {
            g_hash_table_insert(table, name, value);
            return call(table);
        }
        finally
        {
            g_hash_table_unref(table); // The table doesn't own its strings.
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(value);
        }
    }

    static void ThrowIfError(IntPtr error, string action)
    {
        if (error == IntPtr.Zero)
        {
            return;
        }

        // GError: guint32 domain, gint code, gchar* message.
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
        g_error_free(error);
        throw new IOException($"Couldn't {action} the Secret Service keyring: {message}");
    }

    /// <summary>
    /// A SecretSchema in unmanaged memory, kept for the life of the process: name, flags (none), then 32
    /// attribute slots of { name, type } with only <c>key</c> (a string) used, then libsecret's reserved fields.
    /// </summary>
    static IntPtr BuildSchema()
    {
        int pointer = IntPtr.Size;
        int slot = 2 * pointer; // { gchar* name; enum type; } padded to pointer alignment.
        int attributesOffset = 2 * pointer;
        int size = attributesOffset + 32 * slot + 2 * pointer + 7 * pointer;
        var schema = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(schema, i, 0);

        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteInt32(schema, pointer, 0); // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(schema, attributesOffset, Marshal.StringToCoTaskMemUTF8(KeyAttribute));
        Marshal.WriteInt32(schema, attributesOffset + pointer, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        return schema;
    }

    [DllImport(LibSecret)]
    static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, string collection, string label, byte[] password,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    static extern void secret_password_free(IntPtr password);

    [DllImport(LibGlib)]
    static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);

    [DllImport(LibGlib)]
    static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGlib)]
    static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGlib)]
    static extern void g_error_free(IntPtr error);
}
