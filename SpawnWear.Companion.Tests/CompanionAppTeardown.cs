using System.Threading.Tasks;

namespace SpawnWear.Companion.Tests;

/// <summary>
/// Stops the shared Companion dev server when the whole run finishes. NUnit never disposes a
/// static field, so without this every run left its `dotnet run` (and the blazor-devserver child)
/// listening on :5290 until the next run's KillExistingPortOwnerAsync found it.
/// </summary>
[SetUpFixture]
public class CompanionAppTeardown
{
    [OneTimeTearDown]
    public async Task StopCompanion() => await TestBase.s_fixture.DisposeAsync();
}
