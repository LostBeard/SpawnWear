namespace HelloWorldApp
{
    /// <summary>
    /// Minimal managed-only assembly used to validate that
    /// Assembly.Load(byte[]) works on the SpawnWear watch firmware.
    ///
    /// References only mscorlib so flags = 0 + nativeMethodsChecksum = 0
    /// in the .pe header (verified via tools/check-pe-header.cs).
    ///
    /// Phase 8 (SD-card-loadable apps) verification harness loads the
    /// produced HelloWorldApp.pe at runtime, finds this type via
    /// reflection, and invokes Greet() to prove the load + execute path
    /// works end-to-end.
    /// </summary>
    public class HelloWorldPayload
    {
        public static string Greet()
        {
            return "Hello from SD-card-loadable app, watch is at " + System.DateTime.UtcNow.ToString();
        }

        public static int Add(int a, int b)
        {
            return a + b;
        }
    }
}
