using System;
using System.Diagnostics;
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
    ///         (companion pubkey + room key + companion signature). Watch persists peer pubkey + room
    ///         key (RAM only at 7a; NVS persistence is a follow-up), then notifies a 64-byte response
    ///         signature.</item>
    /// </list>
    ///
    /// 7a uses STUB Ed25519: the keypair is random bytes from <see cref="System.Random"/> (NOT
    /// cryptographically secure), and the response signature is a deterministic non-Ed25519
    /// value. This lets the BLE plumbing be tested end-to-end — the Companion's PairingFlow
    /// will correctly reject the stub signature, but the round-trip itself works. 7a-follow-ups
    /// replace the stub with real Ed25519 (likely via an mbedtls-backed nanoCLR intrinsic when
    /// the libdatachannel landing makes mbedtls primitives available — see Plans/phase7-firmware-stub.md).
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
        const int CompanionToWatchLength = PubKeyLength + RoomKeyLength + SignatureLength; // 116

        // ATT protocol error codes returned to a write that the handler rejects.
        // 0x0D = Invalid Attribute Value Length, 0x80 = Application Error (custom).
        const byte AttErrorInvalidLength = 0x0D;

        GattLocalCharacteristic _pubKeyChar;
        GattLocalCharacteristic _handshakeChar;

        // Watch-side keypair. Generated once per boot in 7a (RAM-only). NVS-backed persistence
        // is a 7a-follow-up so the same pubkey survives reboot — without it, every re-boot
        // invalidates all prior pairings on the Companion side.
        readonly byte[] _ourPubKey = new byte[PubKeyLength];
        readonly byte[] _ourPrivKey = new byte[PubKeyLength];

        // Last-paired Companion. Overwritten on every successful handshake (a re-pairing
        // revokes the previous companion's trust). Null until first pair.
        byte[] _peerPubKey;
        byte[] _roomKey;

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
                Debug.WriteLine("[Pair] PubKey characteristic failed: " + pubResult.Error);
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
                Debug.WriteLine("[Pair] Handshake characteristic failed: " + hsResult.Error);
                return false;
            }
            _handshakeChar = hsResult.Characteristic;
            _handshakeChar.WriteRequested += OnHandshakeWrite;

            Debug.WriteLine("[Pair] Characteristics attached (STUB Ed25519 - signatures will not verify)");
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
            var length = (int)request.Value.Length;

            if (length != CompanionToWatchLength)
            {
                Debug.WriteLine("[Pair] Handshake bad length: " + length + " (expected " + CompanionToWatchLength + ")");
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

            // 7a stub: skip verification of the companion's signature. When real Ed25519
            // lands we'll verify against the (companionPub || roomKey) signed-domain bytes
            // here and RespondWithProtocolError on failure.

            _peerPubKey = companionPub;
            _roomKey = roomKey;
            Debug.WriteLine("[Pair] Persisted peer pubkey + room key (RAM only)");

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            // Build the 64-byte stub response. Real Ed25519 would sign
            // (companionPub || roomKey || ourPubKey) with our privkey.
            var watchSig = StubSignWatchToCompanion(companionPub, roomKey, _ourPubKey, _ourPrivKey);

            var notifyWriter = new DataWriter();
            notifyWriter.WriteBytes(watchSig);
            _handshakeChar.NotifyValue(notifyWriter.DetachBuffer());
        }

        /// <summary>Stub Ed25519 sign. Deterministic but not a valid Ed25519 signature —
        /// the Companion's PairingFlow correctly rejects this and aborts pairing. Replaced
        /// in a follow-up commit when real Ed25519 lands on the watch (likely an
        /// mbedtls-backed nanoCLR intrinsic). Layout chosen to be obviously a stub when
        /// inspected on a debugger: first 4 bytes are 'STUB', last byte is 0xFF.</summary>
        static byte[] StubSignWatchToCompanion(byte[] companionPub, byte[] roomKey, byte[] watchPub, byte[] watchPriv)
        {
            var sig = new byte[SignatureLength];
            sig[0] = (byte)'S'; sig[1] = (byte)'T'; sig[2] = (byte)'U'; sig[3] = (byte)'B';
            // Mix in a few bytes from each input so the response varies per pairing — useful
            // for spotting "the watch saw the right inputs" in BLE captures even before
            // real Ed25519 lands.
            sig[4] = companionPub[0]; sig[5] = roomKey[0]; sig[6] = watchPub[0]; sig[7] = watchPriv[0];
            sig[SignatureLength - 1] = 0xFF;
            return sig;
        }

        void EnsureKeyPair()
        {
            // 7a: System.Random is the only RNG nanoFramework's CoreLibrary ships. NOT
            // cryptographically secure — fine for a stub keypair, replaced when real
            // Ed25519 (and a real RNG) land on the watch.
            var rng = new Random();
            rng.NextBytes(_ourPubKey);
            rng.NextBytes(_ourPrivKey);
            Debug.WriteLine("[Pair] Generated stub keypair (NOT secure - 7a placeholder)");
        }
    }
}
