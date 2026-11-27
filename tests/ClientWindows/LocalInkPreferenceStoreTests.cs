using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class LocalInkPreferenceStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "pim-ink-preferences-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void MissingInvalidOrUnsupportedPreferencesFallBackToTheDefaultBrush()
    {
        var store = new LocalInkPreferenceStore(_path);
        Assert.Equal(LocalInkStyle.Default, store.Load());

        File.WriteAllText(_path, "{not-json");
        Assert.Equal(LocalInkStyle.Default, store.Load());

        File.WriteAllText(_path, """{"color":"#123456","width":3.4}""");
        Assert.Equal(LocalInkStyle.Default, store.Load());

        File.WriteAllText(_path, """{"color":"#b8373c","width":2.5}""");
        Assert.Equal(LocalInkStyle.Default, store.Load());
    }

    [Fact]
    public void SavedPaletteChoiceIsRestoredForTheNextWindow()
    {
        var store = new LocalInkPreferenceStore(_path);
        var selected = new LocalInkStyle(
            LocalInkPalette.GreenColor,
            LocalInkPalette.ThickWidth);

        store.Save(selected);

        Assert.Equal(selected, new LocalInkPreferenceStore(_path).Load());
        Assert.True(File.Exists(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
