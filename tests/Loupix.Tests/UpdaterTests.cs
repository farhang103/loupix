using Loupix.Services;

namespace Loupix.Tests;

public class UpdaterTests
{
    /// <summary>Loupix starts the updater on every launch, so a failure here stops the app from opening.</summary>
    [Fact]
    public void The_updater_can_start() => Sta.Run(() => new Updater(() => false, () => { }).Start());
}
