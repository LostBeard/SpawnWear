using System.Diagnostics;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace SpawnWear
{
    /// <summary>
    /// Debug log + command characteristics on the same primary GATT service as WiFi
    /// (single <see cref="GattServiceProvider"/> — required for nanoFramework / ESP32).
    /// </summary>
    public class DebugConsoleService
    {
        GattLocalCharacteristic _logOutputChar;
        GattLocalCharacteristic _commandInputChar;
        bool _hasSubscribers;
        bool _initialized;

        public event CommandReceivedHandler CommandReceived;
        public delegate void CommandReceivedHandler(string command);

        /// <summary>
        /// Attaches debug characteristics to an existing primary service (the WiFi service).
        /// </summary>
        public bool Initialize(GattLocalService service)
        {
            var logParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                UserDescription = "Debug Log Output"
            };
            var logResult = service.CreateCharacteristic(BleUuids.DebugLogOutputUuid, logParams);
            if (logResult.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[DebugConsole] Failed to create log output characteristic: " + logResult.Error);
                return false;
            }
            _logOutputChar = logResult.Characteristic;
            _logOutputChar.SubscribedClientsChanged += (sender, args) =>
            {
                _hasSubscribers = sender.SubscribedClients.Length > 0;
            };

            var cmdParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                UserDescription = "Command Input"
            };
            var cmdResult = service.CreateCharacteristic(BleUuids.DebugCommandInputUuid, cmdParams);
            if (cmdResult.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[DebugConsole] Failed to create command input characteristic: " + cmdResult.Error);
                return false;
            }
            _commandInputChar = cmdResult.Characteristic;
            _commandInputChar.WriteRequested += OnCommandWriteRequested;

            _initialized = true;
            Debug.WriteLine("[DebugConsole] Characteristics attached to primary service");
            return true;
        }

        public void Log(string message)
        {
            Debug.WriteLine(message);
            if (!_initialized || !_hasSubscribers) return;

            // Conservative cap; characteristic notify size is bounded by ATT MTU minus headers.
            if (message.Length > 500)
            {
                message = message.Substring(0, 497) + "...";
            }

            var writer = new DataWriter();
            writer.WriteString(message);
            _logOutputChar.NotifyValue(writer.DetachBuffer());
        }

        void OnCommandWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var request = args.GetRequest();
            var reader = DataReader.FromBuffer(request.Value);
            var command = reader.ReadString(request.Value.Length);

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            Debug.WriteLine("[DebugConsole] Command received: " + command);
            CommandReceived?.Invoke(command);
        }
    }
}
