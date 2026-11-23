using Pim.Client.App;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class NativePlannerTrayEntryBehaviorTests
{
    [Fact]
    public void ExistingWindowIsActivatedWithoutCreatingAnother()
    {
        var existing = new object();
        var activated = 0;
        var created = 0;

        var reused = TrayIcon.ActivateOrCreateWindow(
            new[] { existing },
            _ => activated++,
            () => created++);

        Assert.True(reused);
        Assert.Equal(1, activated);
        Assert.Equal(0, created);
    }

    [Fact]
    public void MissingWindowIsCreatedWithoutActivation()
    {
        var activated = 0;
        var created = 0;

        var reused = TrayIcon.ActivateOrCreateWindow(
            Array.Empty<object>(),
            _ => activated++,
            () => created++);

        Assert.False(reused);
        Assert.Equal(0, activated);
        Assert.Equal(1, created);
    }
}
