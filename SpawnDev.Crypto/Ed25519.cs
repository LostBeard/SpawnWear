using System.Runtime.CompilerServices;

namespace SpawnDev.Crypto
{
    /// <summary>
    /// Ed25519 digital signatures (RFC 8032, SHA-512 variant) backed by Monocypher
    /// native code. Interoperable with WebCrypto Ed25519 and the SpawnDev P2P
    /// identity stack.
    ///
    /// nanoFramework interop cannot return or pass arrays by ref, so every output
    /// buffer is caller-allocated and filled in place by the native method. Buffer
    /// sizes are fixed (public key 32, private/secret key 64, signature 64, seed
    /// 32) and validated native-side.
    /// </summary>
    public static class Ed25519
    {
        /// <summary>Generate a fresh random key pair. <paramref name="publicKey"/>
        /// must be 32 bytes, <paramref name="privateKey"/> 64 bytes; both are
        /// filled. Uses the hardware RNG for the seed.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void GenerateKeyPair(byte[] publicKey, byte[] privateKey);

        /// <summary>Deterministically derive a key pair from a 32-byte
        /// <paramref name="seed"/>. <paramref name="publicKey"/> (32) and
        /// <paramref name="privateKey"/> (64) are filled.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void KeyPairFromSeed(byte[] seed, byte[] publicKey, byte[] privateKey);

        /// <summary>Sign <paramref name="message"/> with the 64-byte
        /// <paramref name="privateKey"/>; the 64-byte <paramref name="signature"/>
        /// buffer is filled.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void Sign(byte[] signature, byte[] privateKey, byte[] message);

        /// <summary>Verify a 64-byte <paramref name="signature"/> over
        /// <paramref name="message"/> against the 32-byte
        /// <paramref name="publicKey"/>. Returns true if valid.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool Verify(byte[] signature, byte[] publicKey, byte[] message);
    }
}
