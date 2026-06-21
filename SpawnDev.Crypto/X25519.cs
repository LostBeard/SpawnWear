using System.Runtime.CompilerServices;

namespace SpawnDev.Crypto
{
    /// <summary>
    /// X25519 Elliptic-Curve Diffie-Hellman (Curve25519) backed by Monocypher.
    /// Used to derive a shared session secret during pairing (then hashed into an
    /// HMAC key for per-request HTTP auth).
    ///
    /// Output buffers are caller-allocated and filled in place; all keys are 32
    /// bytes. Monocypher clamps secret keys internally.
    /// </summary>
    public static class X25519
    {
        /// <summary>Fill <paramref name="privateKey"/> (32 bytes) with fresh
        /// hardware-random bytes.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void GeneratePrivateKey(byte[] privateKey);

        /// <summary>Derive the 32-byte <paramref name="publicKey"/> from the
        /// 32-byte <paramref name="privateKey"/>.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void GetPublicKey(byte[] publicKey, byte[] privateKey);

        /// <summary>Compute the 32-byte <paramref name="sharedSecret"/> from our
        /// <paramref name="privateKey"/> and the peer's
        /// <paramref name="theirPublicKey"/>. Hash before use as a key.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void SharedSecret(byte[] sharedSecret, byte[] privateKey, byte[] theirPublicKey);
    }
}
