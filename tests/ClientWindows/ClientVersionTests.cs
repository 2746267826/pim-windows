using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public class ClientVersionTests
{
    [Fact]
    public void MachineVersionIsPureCalVerFromAssemblyAttribute()
    {
        var machine = ClientVersion.ReadMachineVersion(typeof(ClientVersionTests).Assembly);

        Assert.Equal("26.11.1234", machine);
        Assert.DoesNotContain("v", machine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(", machine);
        Assert.DoesNotContain(")", machine);
    }

    [Fact]
    public void DisplayVersionComesFromAssemblyMetadata()
    {
        var display = ClientVersion.ReadDisplayVersion(typeof(ClientVersionTests).Assembly);

        Assert.Equal("1.1.0", display);
    }

    [Fact]
    public void DisplayLabelUsesSingleVPrefixAndKeepsMachineVersionVisible()
    {
        var label = ClientVersion.FormatDisplayLabel("1.1.0", "26.11.1234");

        Assert.Equal("v1.1.0 (26.11.1234)", label);
        Assert.DoesNotContain("vv", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplayLabelNormalizesLeadingVButDoesNotDoubleIt()
    {
        var label = ClientVersion.FormatDisplayLabel("v1.1.0", "26.11.1234");

        Assert.Equal("v1.1.0 (26.11.1234)", label);
    }

    [Fact]
    public void FormattedDisplayLabelMustNotBeUsedForUpdateCheck()
    {
        var machine = ClientVersion.ReadMachineVersion(typeof(ClientVersionTests).Assembly)!;
        var display = ClientVersion.ReadDisplayVersion(typeof(ClientVersionTests).Assembly)!;
        var label = ClientVersion.FormatDisplayLabel(display, machine);

        Assert.True(UpdateChecker.IsNewer(machine, "26.11.1235"));
        Assert.False(UpdateChecker.IsNewer(label, "26.11.1235"));
    }
}
