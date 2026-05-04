namespace SpawnWear.AppContracts
{
    /// <summary>
    /// Minimal logger interface. Today this just wraps Debug.WriteLine;
    /// Phase 3's Logger system service will add a ring buffer + USB-CDC
    /// sink + BLE notify sink so apps can log without losing entries
    /// when the debugger isn't attached.
    /// </summary>
    public interface ILogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }
}
