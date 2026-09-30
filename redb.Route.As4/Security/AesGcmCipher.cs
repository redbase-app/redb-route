using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace redb.Route.As4.Security;

/// <summary>
/// AES-GCM as XML Encryption 1.1 lays it out: a 96-bit IV, then the ciphertext, then the 128-bit
/// authentication tag, as one octet sequence (XML Encryption 1.1 §5.2.4).
/// <para>
/// Streamed with BouncyCastle's <see cref="GcmBlockCipher"/>: .NET's <see cref="AesGcm"/> works on whole buffers
/// only, and a payload may be larger than memory. GCM authenticates only at the end, so decrypted output is not
/// trustworthy until <see cref="Decrypt"/> returns: callers write it to a spool they discard on failure and hand
/// it on only afterwards.
/// </para>
/// </summary>
internal static class AesGcmCipher
{
    /// <summary>IV length in bytes.</summary>
    public const int IvSize = 12;

    /// <summary>Tag length in bytes.</summary>
    public const int TagSize = 16;

    private const int BufferSize = 81920;

    /// <summary>
    /// Decrypts <c>IV || ciphertext || tag</c> from <paramref name="input"/> into <paramref name="output"/>. Throws
    /// <see cref="CryptographicException"/> on a wrong key or tampered data — after writing unauthenticated output,
    /// which the caller must then discard.
    /// </summary>
    public static void Decrypt(byte[] key, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var iv = new byte[IvSize];
        if (input.ReadAtLeast(iv, IvSize, throwOnEndOfStream: false) < IvSize)
            throw new CryptographicException("AES-GCM data is shorter than its IV and tag.");

        var gcm = Cipher(forEncryption: false, key, iv);
        try
        {
            Pump(gcm, input, output);
        }
        catch (InvalidCipherTextException e)
        {
            // Too short for a tag, or the tag does not match: one exception for both, no oracle.
            throw new CryptographicException("AES-GCM authentication failed.", e);
        }
    }

    /// <summary>Encrypts <paramref name="input"/> with <paramref name="key"/> and a fresh IV into <c>IV || ciphertext || tag</c>.</summary>
    public static void Encrypt(byte[] key, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var iv = RandomNumberGenerator.GetBytes(IvSize);
        output.Write(iv);
        Pump(Cipher(forEncryption: true, key, iv), input, output);
    }

    private static GcmBlockCipher Cipher(bool forEncryption, byte[] key, byte[] iv)
    {
        var gcm = new GcmBlockCipher(new AesEngine());
        gcm.Init(forEncryption, new AeadParameters(new KeyParameter(key), TagSize * 8, iv));
        return gcm;
    }

    private static void Pump(GcmBlockCipher gcm, Stream input, Stream output)
    {
        var buffer = new byte[BufferSize];
        // Room for what the cipher still holds from the previous call (under a block, plus the tag it withholds when decrypting).
        var result = new byte[BufferSize + 64];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            var written = gcm.ProcessBytes(buffer, 0, read, result, 0);
            output.Write(result, 0, written);
        }

        var final = new byte[gcm.GetOutputSize(0)];
        var finalLength = gcm.DoFinal(final, 0);
        output.Write(final, 0, finalLength);
    }
}
