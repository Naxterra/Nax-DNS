using BetterDns.Gui.Services;
using Xunit;

namespace BetterDns.Gui.Tests;

public sealed class GuiInstanceCoordinatorTests
{
    [Theory]
    [InlineData(GuiInstanceCoordinator.ActivateCommand)]
    [InlineData(GuiInstanceCoordinator.ExitForUpdateCommand)]
    public async Task Second_instance_sends_command_to_the_single_owner(string command)
    {
        var name = "BetterDNS.Gui.Test." + Guid.NewGuid().ToString("N");
        using var owner = new GuiInstanceCoordinator(name);
        using var second = new GuiInstanceCoordinator(name);
        Assert.True(owner.TryOwnInstance());
        Assert.False(second.TryOwnInstance());
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StartListening(value => received.TrySetResult(value));
        Assert.True(await second.SendAsync(command));
        Assert.Equal(command, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Instance_can_be_acquired_after_the_owner_exits()
    {
        var name = "BetterDNS.Gui.Test." + Guid.NewGuid().ToString("N");
        using (var owner = new GuiInstanceCoordinator(name)) Assert.True(owner.TryOwnInstance());
        using var replacement = new GuiInstanceCoordinator(name);
        Assert.True(replacement.TryOwnInstance());
    }
}
