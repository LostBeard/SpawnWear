using System.Reflection;

// SpawnWear app/OS contract surface. Both the firmware and every loadable app
// bind to THIS assembly's identity, and the runtime resolves an app's
// ISpawnApp reference against the deployed copy by name + version. Keep this
// version FIXED across firmware builds so apps survive OS updates without a
// rebuild - bump it ONLY when the contract itself changes (which legitimately
// requires apps to recompile). See Notes / the resume memory for the rule.
[assembly: AssemblyTitle("SpawnWear.AppContracts")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
