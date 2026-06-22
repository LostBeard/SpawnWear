using System;
using System.Diagnostics;
using System.IO;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace SpawnWear
{
    /// <summary>
    /// Phase 7a — BLE pairing handshake on the watch side. Two characteristics
    /// attached to the primary GATT service alongside WiFi / WatchProfile / DebugConsole:
    ///
    /// <list type="bullet">
    ///   <item><c>PairingPubKeyUuid</c> — Read. Returns the watch's 32-byte raw Ed25519 public key.</item>
    ///   <item><c>PairingHandshakeUuid</c> — Write + Notify. Companion writes a 116-byte handshake
    ///         (companion pubkey + room key + companion signature). Watch verifies the companion
    ///         signature, persists peer pubkey + room key to internal flash (I:\spawnwear-pair.bin),
    ///         then notifies a 64-byte response signature.</item>
    /// </list>
    ///
    /// Real Ed25519 (RFC 8032 / SHA-512) via SpawnDev.Crypto over Monocypher, proven on
    /// hardware 2026-06-22. The watch identity is a 32-byte seed (filled from the ESP32
    /// hardware RNG) persisted to internal flash; the 32-byte public key and the 64-byte
    /// signing key are deterministically derived from the seed via KeyPairFromSeed. The
    /// handshake VERIFIES the companion's signature over (companionPub || roomKey) and
    /// SIGNS the response over (companionPub || roomKey || watchPub), so the Companion's
    /// PairingFlow (WebCrypto / Ed25519Managed, also RFC 8032) verifies it and pairing
    /// completes for real. All keys/signatures cross the wire as raw bytes (no SPKI/DER).
    ///
    /// Wire layout per Plans/phase7-webrtc-handoff.md and SpawnWear.Bridge.Pairing.PairingHandshakeWire:
    /// <code>
    /// Companion → Watch (write to PairingHandshakeUuid, 116 bytes):
    ///   offset 0  : companionPubKey  [32 bytes]
    ///   offset 32 : roomKey          [20 bytes]
    ///   offset 52 : signature        [64 bytes]   = sign(prev 52 bytes, companionPriv)
    ///
    /// Watch → Companion (notify on PairingHandshakeUuid, 64 bytes):
    ///   offset 0  : signature        [64 bytes]
    ///                                = sign(companionPub || roomKey || watchPub, watchPriv)
    /// </code>
    /// </summary>
    public class PairingService
    {
        const int PubKeyLength = 32;
        const int RoomKeyLength = 20;
        const int SignatureLength = 64;
        const int PairCodeLength = 6; // 6 ASCII digits, folded into the signed domains (Level 2)
        const int CompanionToWatchLength = PubKeyLength + RoomKeyLength + SignatureLength; // 116

        // ATT protocol error codes returned to a write that the handler rejects.
        // 0x0D = Invalid Attribute Value Length, 0x80 = Application Error (custom,
        // used here for a failed Ed25519 signature verification).
        const byte AttErrorInvalidLength = 0x0D;
        const byte AttErrorAuthFail = 0x80;
        const byte AttErrorWindowClosed = 0x81; // handshake arrived while pairing was not armed
        const int Ed25519SecretKeyLength = 64; // Monocypher ed25519 secret key (seed-derived)

        // Persistence file on the runtime's internal-flash volume (I:\). Survives
        // reboot + firmware redeploy unless the partition is mass-erased. The
        // runtime auto-mounts I:\ at boot - no driver setup needed here.
        // Layout (120 bytes total). The 4-byte magic makes the file self-identifying: a
        // file without it (a pre-real-crypto stub file, where the secret slot held weak
        // System.Random bytes) or any short/foreign file fails the check and is regenerated
        // from a fresh hardware-RNG seed - so the watch identity is never derived from a
        // non-crypto RNG.
        //   offset 0   : magic       [4]    ('S','W','K','1')
        //   offset 4   : ourPubKey   [32]   (derived from the seed; written for inspectability)
        //   offset 36  : ourSeed     [32]   (the SECRET - Ed25519 seed; pub + 64B signing key derive from it)
        //   offset 68  : peerPubKey  [32]   (all-zero = unpaired)
        //   offset 100 : roomKey     [20]   (all-zero = unpaired)
        const string PairingFilePath = "I:\\spawnwear-pair.bin";
        static readonly byte[] FileMagic = new byte[] { (byte)'S', (byte)'W', (byte)'K', (byte)'1' };
        const int FileMagicLength = 4;
        const int PersistedFileLength = FileMagicLength + PubKeyLength + PubKeyLength + PubKeyLength + RoomKeyLength;
        const int FileOffsetMagic   = 0;
        const int FileOffsetOurPub  = FileMagicLength;
        const int FileOffsetOurSeed = FileMagicLength + PubKeyLength;
        const int FileOffsetPeerPub = FileMagicLength + PubKeyLength + PubKeyLength;
        const int FileOffsetRoomKey = FileMagicLength + PubKeyLength + PubKeyLength + PubKeyLength;

        readonly DebugConsoleService _debug;

        GattLocalCharacteristic _pubKeyChar;
        GattLocalCharacteristic _handshakeChar;

        public PairingService(DebugConsoleService debug)
        {
            _debug = debug;
        }

        // DebugConsoleService.Log already does Debug.WriteLine internally AND
        // notifies the BLE log characteristic, so calling _debug.Log fans out
        // to both the COM9 USB-serial stream (nf-attach / VS Output) and the
        // Companion's Console tab. Fall back to plain Debug.WriteLine when
        // _debug isn't attached (early boot, or future no-debug builds).
        void Log(string message)
        {
            if (_debug != null) _debug.Log(message);
            else Debug.WriteLine(message);
        }

        // Watch-side Ed25519 identity. The 32-byte SEED is the persisted secret; the
        // 32-byte public key and the 64-byte Monocypher signing key are re-derived from
        // it at load (DeriveIdentityFromSeed). Loaded from PairingFilePath if a prior
        // boot persisted it; freshly generated + saved on first boot. Persists across
        // reboot via I:\ internal-flash so the Companion's saved pairing keeps verifying.
        readonly byte[] _ourPubKey = new byte[PubKeyLength];          // 32, derived
        readonly byte[] _ourSeed = new byte[PubKeyLength];            // 32, persisted secret
        readonly byte[] _ourPrivKey64 = new byte[Ed25519SecretKeyLength]; // 64, derived (for Sign)

        // Last-paired Companion. Overwritten on every successful handshake (a re-pairing
        // revokes the previous companion's trust). All-zero until first pair.
        byte[] _peerPubKey;
        byte[] _roomKey;

        // ----- Phase 2: pairing window (Settings > Companion) -----
        // Pairing is only accepted while the user is on the Companion page with the
        // toggle ON. BeginPairingWindow shows a 6-digit code the user types into the
        // Companion app; the code binds the key exchange to physical presence (Phase
        // 3 uses it to authenticate the Ed25519 handshake => MITM defense). The
        // window auto-closes when the user leaves the page (CompanionScreen.OnPause).
        bool _windowOpen;
        string _currentCode;

        /// <summary>True while the user has pairing armed on the Companion page.</summary>
        public bool IsPairingWindowOpen => _windowOpen;

        /// <summary>The 6-digit code currently displayed, or null when closed.</summary>
        public string CurrentCode => _currentCode;

        /// <summary>Arms pairing: generates a fresh 6-digit code (hardware RNG via
        /// the native crypto) and opens the window. Returns the code to display.</summary>
        public string BeginPairingWindow()
        {
            byte[] rnd = new byte[32];
            SpawnDev.Crypto.X25519.GeneratePrivateKey(rnd); // ESP32 HW RNG fills 32 bytes
            uint n = ((uint)rnd[0] << 16) | ((uint)rnd[1] << 8) | rnd[2];
            int value = (int)(n % 1000000);
            string code = value.ToString();
            while (code.Length < 6) code = "0" + code; // zero-pad to 6 digits
            _currentCode = code;
            _windowOpen = true;
            Log("[Pair] Pairing window OPEN, code=" + _currentCode);
            return _currentCode;
        }

        /// <summary>Disarms pairing and clears the code. Idempotent.</summary>
        public void EndPairingWindow()
        {
            if (!_windowOpen) return;
            _windowOpen = false;
            _currentCode = null;
            Log("[Pair] Pairing window CLOSED");
        }

        public bool Initialize(GattLocalService service)
        {
            EnsureKeyPair();

            // Pubkey — read only. The Companion reads this as step 1 of PairAsync.
            var pubParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                UserDescription = "Pairing PubKey",
            };
            var pubResult = service.CreateCharacteristic(BleUuids.PairingPubKeyUuid, pubParams);
            if (pubResult.Error != BluetoothError.Success)
            {
                Log("[Pair] PubKey characteristic failed: " + pubResult.Error);
                return false;
            }
            _pubKeyChar = pubResult.Characteristic;
            _pubKeyChar.ReadRequested += OnPubKeyRead;

            // Handshake — write + notify.
            var hsParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.Notify,
                UserDescription = "Pairing Handshake",
            };
            var hsResult = service.CreateCharacteristic(BleUuids.PairingHandshakeUuid, hsParams);
            if (hsResult.Error != BluetoothError.Success)
            {
                Log("[Pair] Handshake characteristic failed: " + hsResult.Error);
                return false;
            }
            _handshakeChar = hsResult.Characteristic;
            _handshakeChar.WriteRequested += OnHandshakeWrite;

            Log("[Pair] Characteristics attached (real Ed25519 - RFC 8032 / Monocypher)");
            return true;
        }

        void OnPubKeyRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
        {
            var request = args.GetRequest();
            var writer = new DataWriter();
            writer.WriteBytes(_ourPubKey);
            request.RespondWithValue(writer.DetachBuffer());
        }

        void OnHandshakeWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var request = args.GetRequest();

            // Level 1 physical-presence gate: only accept a handshake while the user has
            // pairing armed (Settings > Companion open => BeginPairingWindow). A closed
            // window means nobody physically approved this pairing, so reject it - this
            // kills silent/background pairing by any device in BLE range. (Level 2 adds
            // 6-digit-code binding into the signed domain for full MITM defense.)
            if (!_windowOpen)
            {
                Log("[Pair] Handshake rejected - pairing window not armed (open Settings > Companion)");
                request.RespondWithProtocolError(AttErrorWindowClosed);
                return;
            }

            var length = (int)request.Value.Length;

            if (length != CompanionToWatchLength)
            {
                Log("[Pair] Handshake bad length: " + length + " (expected " + CompanionToWatchLength + ")");
                request.RespondWithProtocolError(AttErrorInvalidLength);
                return;
            }

            var payload = new byte[length];
            var reader = DataReader.FromBuffer(request.Value);
            reader.ReadBytes(payload);

            var companionPub = new byte[PubKeyLength];
            var roomKey = new byte[RoomKeyLength];
            var companionSig = new byte[SignatureLength];
            Array.Copy(payload, 0, companionPub, 0, PubKeyLength);
            Array.Copy(payload, PubKeyLength, roomKey, 0, RoomKeyLength);
            Array.Copy(payload, PubKeyLength + RoomKeyLength, companionSig, 0, SignatureLength);

            // Level 2 MITM defense: verify the companion's Ed25519 signature over
            // (companionPub || roomKey || code), where code = the 6 digits THIS watch is
            // currently showing. The companion could only have signed the right code by the
            // user reading it off this screen and typing it in - so a verifying signature
            // proves physical presence, not just key possession. Reject on failure (wrong
            // code or wrong key): do not persist, do not notify.
            byte[] codeBytes = CurrentCodeBytes();
            if (codeBytes == null)
            {
                Log("[Pair] Handshake rejected - no active pairing code");
                request.RespondWithProtocolError(AttErrorWindowClosed);
                return;
            }
            var companionDomain = new byte[PubKeyLength + RoomKeyLength + PairCodeLength]; // 58
            Array.Copy(payload, 0, companionDomain, 0, PubKeyLength + RoomKeyLength);
            Array.Copy(codeBytes, 0, companionDomain, PubKeyLength + RoomKeyLength, PairCodeLength);
            if (!SpawnDev.Crypto.Ed25519.Verify(companionSig, companionPub, companionDomain))
            {
                Log("[Pair] Companion signature INVALID (wrong pairing code or key) - rejecting handshake");
                request.RespondWithProtocolError(AttErrorAuthFail);
                return;
            }

            _peerPubKey = companionPub;
            _roomKey = roomKey;
            SavePairingFile();

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            // Real Ed25519 response: sign (companionPub || roomKey || ourPubKey || code) with
            // our seed-derived 64-byte signing key. The Companion verifies this against the
            // watch public key it read from PairingPubKeyUuid plus the code the user typed.
            var watchSig = SignWatchToCompanion(companionPub, roomKey, codeBytes);

            var notifyWriter = new DataWriter();
            notifyWriter.WriteBytes(watchSig);
            _handshakeChar.NotifyValue(notifyWriter.DetachBuffer());
            Log("[Pair] Pairing complete - companion trusted");
        }

        /// <summary>Real Ed25519 watch-to-companion signature over the 90-byte domain
        /// (companionPub[32] || roomKey[20] || ourPubKey[32] || code[6]), signed with the
        /// watch's seed-derived 64-byte signing key. Matches PairingHandshakeWire
        /// .SignedDomainWatchToCompanion so the Companion's Verify succeeds.</summary>
        byte[] SignWatchToCompanion(byte[] companionPub, byte[] roomKey, byte[] codeBytes)
        {
            var domain = new byte[PubKeyLength + RoomKeyLength + PubKeyLength + PairCodeLength]; // 90
            int o = 0;
            Array.Copy(companionPub, 0, domain, o, PubKeyLength); o += PubKeyLength;
            Array.Copy(roomKey, 0, domain, o, RoomKeyLength); o += RoomKeyLength;
            Array.Copy(_ourPubKey, 0, domain, o, PubKeyLength); o += PubKeyLength;
            Array.Copy(codeBytes, 0, domain, o, PairCodeLength);

            var sig = new byte[SignatureLength];
            SpawnDev.Crypto.Ed25519.Sign(sig, _ourPrivKey64, domain);
            return sig;
        }

        /// <summary>Canonical bytes of the active pairing code (6 ASCII digits), or null when
        /// no code is armed. Must match SpawnWear.Bridge.Pairing.PairingHandshake.CodeToBytes
        /// byte-for-byte so the watch and companion sign/verify the same domain.</summary>
        byte[] CurrentCodeBytes()
        {
            string code = _currentCode;
            if (code == null || code.Length != PairCodeLength) return null;
            var b = new byte[PairCodeLength];
            for (int i = 0; i < PairCodeLength; i++)
            {
                char c = code[i];
                if (c < '0' || c > '9') return null;
                b[i] = (byte)c;
            }
            return b;
        }

        void EnsureKeyPair()
        {
            // First try to restore from I:\ - the runtime's internal-flash volume.
            if (TryLoadPairingFile())
            {
                // Re-derive the public key + 64-byte signing key from the loaded seed. WITHOUT
                // this the load path leaves _ourPubKey/_ourPrivKey64 zero-initialized, so the
                // watch advertises a zero pubkey and signs an unverifiable response on every
                // boot after the first (which took the regen path below and derived correctly).
                DeriveIdentityFromSeed();
                bool paired = !IsAllZero(_peerPubKey) && !IsAllZero(_roomKey);
                Log("[Pair] Loaded keypair from " + PairingFilePath + " (paired=" + (paired ? "yes" : "no") + ")");
                return;
            }

            // First boot: a fresh 32-byte Ed25519 seed from the ESP32 hardware RNG, then
            // derive the real keypair. X25519.GeneratePrivateKey is esp_fill_random under
            // the hood (32 HW-random bytes) - the only crypto-grade RNG on the watch.
            SpawnDev.Crypto.X25519.GeneratePrivateKey(_ourSeed);
            DeriveIdentityFromSeed();
            _peerPubKey = new byte[PubKeyLength]; // all zero = unpaired
            _roomKey = new byte[RoomKeyLength];   // all zero = unpaired
            Log("[Pair] Generated real Ed25519 keypair (HW-RNG seed)");
            SavePairingFile();
        }

        /// <summary>Re-derive _ourPubKey (32) and _ourPrivKey64 (64) from the persisted
        /// 32-byte _ourSeed via Monocypher KeyPairFromSeed. RFC 8032 derives the public
        /// key from the seed (clamp(SHA-512(seed)) * B), so the derived pub always matches
        /// what Sign produces - the persisted pub slot is authoritative-by-derivation.</summary>
        void DeriveIdentityFromSeed()
        {
            SpawnDev.Crypto.Ed25519.KeyPairFromSeed(_ourSeed, _ourPubKey, _ourPrivKey64);
        }

        bool TryLoadPairingFile()
        {
            try
            {
                if (!File.Exists(PairingFilePath)) return false;
                var bytes = File.ReadAllBytes(PairingFilePath);
                if (bytes == null || bytes.Length != PersistedFileLength)
                {
                    Log("[Pair] " + PairingFilePath + " unexpected length " + (bytes == null ? -1 : bytes.Length) + " (want " + PersistedFileLength + "), regenerating");
                    return false;
                }
                for (int i = 0; i < FileMagicLength; i++)
                {
                    if (bytes[FileOffsetMagic + i] != FileMagic[i])
                    {
                        Log("[Pair] " + PairingFilePath + " magic mismatch (pre-real-crypto or foreign file), regenerating with a fresh HW-RNG seed");
                        return false;
                    }
                }
                // Secret = the 32-byte Ed25519 seed; the public key + 64-byte signing key
                // are re-derived from it by DeriveIdentityFromSeed, so the stored pub slot
                // is overwritten by that derivation and is not read back here.
                Array.Copy(bytes, FileOffsetOurSeed, _ourSeed, 0, PubKeyLength);
                _peerPubKey = new byte[PubKeyLength];
                _roomKey    = new byte[RoomKeyLength];
                Array.Copy(bytes, FileOffsetPeerPub, _peerPubKey, 0, PubKeyLength);
                Array.Copy(bytes, FileOffsetRoomKey, _roomKey,    0, RoomKeyLength);
                return true;
            }
            catch (Exception ex)
            {
                Log("[Pair] Load from " + PairingFilePath + " EX: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        void SavePairingFile()
        {
            try
            {
                var buf = new byte[PersistedFileLength];
                Array.Copy(FileMagic,  0, buf, FileOffsetMagic,   FileMagicLength);
                Array.Copy(_ourPubKey, 0, buf, FileOffsetOurPub,  PubKeyLength);
                Array.Copy(_ourSeed,   0, buf, FileOffsetOurSeed, PubKeyLength);
                if (_peerPubKey != null) Array.Copy(_peerPubKey, 0, buf, FileOffsetPeerPub, PubKeyLength);
                if (_roomKey    != null) Array.Copy(_roomKey,    0, buf, FileOffsetRoomKey, RoomKeyLength);
                File.WriteAllBytes(PairingFilePath, buf);
                Log("[Pair] Saved " + PersistedFileLength + " bytes to " + PairingFilePath);
            }
            catch (Exception ex)
            {
                Log("[Pair] Save to " + PairingFilePath + " EX: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static bool IsAllZero(byte[] data)
        {
            if (data == null) return true;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0) return false;
            }
            return true;
        }
    }
}
