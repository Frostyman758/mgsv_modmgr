using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MgsvModMgr.Core;

/// <summary>
/// Encrypt / decrypt small secrets (right now: the Nexus API key)
/// before they hit disk. Output is opaque base64 with a scheme prefix
/// so the load path knows how to reverse it.
///
/// <para>Schemes:</para>
/// <list type="bullet">
/// <item><c>dpapi:</c> — Windows Data Protection API, scope
/// <see cref="DataProtectionScope.CurrentUser"/>. The encryption key
/// is derived from the user's Windows login credentials, so:
/// <list type="bullet">
/// <item>Only the SAME user on the SAME machine can decrypt — even an
/// admin can't read it without compromising the user's password.</item>
/// <item>Copying the manager.xml off the machine gives the attacker a
/// useless ciphertext blob.</item>
/// <item>Moving the file to another user on the same machine, or to a
/// fresh OS install, makes the key unrecoverable — that's the
/// intended security boundary, not a bug.</item>
/// </list></item>
/// <item><c>plain:</c> — explicit-plaintext fallback. Used on
/// non-Windows platforms where we don't have an equivalent of DPAPI
/// wired yet. The prefix is there so the load path knows the value
/// is already decrypted; long-term we want libsecret (Linux) /
/// Keychain (macOS) here.</item>
/// <item>(no prefix) — legacy plaintext from the pre-vault
/// <c>state.txt</c> or <c>manager.xml</c> writes. Treated as
/// already-decrypted on read; will be re-encrypted on the next save.</item>
/// </list>
/// </summary>
public static class SecretVault
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    /// <summary>
    /// Encrypt for storage. Empty / null input returns empty (no
    /// prefix) so a cleared field round-trips as a cleared field.
    /// </summary>
    public static string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        if (OperatingSystem.IsWindows())
            return DpapiPrefix + EncryptDpapi(plaintext);

        // TODO: Keychain (macOS) and libsecret (Linux) wrappers.
        // Until then, mark the value explicitly so the load path
        // doesn't get confused into trying to base64-decode it.
        return PlainPrefix + plaintext;
    }

    /// <summary>
    /// Decrypt a stored value. Unrecognised / legacy values are
    /// returned as-is (treated as plaintext) so the user doesn't lose
    /// their API key just because we couldn't reverse it. Returns
    /// empty string on decryption failure (different user, corrupted
    /// blob, etc.).
    /// </summary>
    public static string Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";

        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) return "";
            try   { return DecryptDpapi(stored[DpapiPrefix.Length..]); }
            catch { return ""; }
        }

        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return stored[PlainPrefix.Length..];

        // Legacy plaintext from the pre-vault era. Hand it back so
        // the UI shows the saved key; on the next Save we'll wrap
        // it in the correct scheme.
        return stored;
    }

    // ─── DPAPI helpers ────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static string EncryptDpapi(string plaintext)
    {
        var bytes     = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null,
                                              DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    [SupportedOSPlatform("windows")]
    private static string DecryptDpapi(string base64)
    {
        var encrypted = Convert.FromBase64String(base64);
        var bytes     = ProtectedData.Unprotect(encrypted, optionalEntropy: null,
                                                DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
