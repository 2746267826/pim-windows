namespace Pim.Client.Core.Services.Notes;

public enum NativeNoteLayoutMode
{
    Narrow,
    Split,
    Wide,
}

public sealed record NativeNoteLayout(
    NativeNoteLayoutMode Mode,
    bool LibraryVisible,
    bool LibraryOverlay,
    bool CloudVisible,
    bool CloudOverlay,
    bool SingleColumn);

/// <summary>
/// Resolves the adaptive notes workspace. Wide layouts dock both panels when
/// requested; split layouts dock the library and overlay the cloud drawer;
/// collapsing the library lets the cloud drawer dock on its own; narrowing to
/// a single column auto-collapses both before the editor is squeezed below
/// its usable width.
/// </summary>
public sealed class NativeNoteLayoutState
{
    public const double NarrowWidth = 1000;
    public const double WideWidth = 1320;
    public const double MinimumEditorWidth = 560;
    public const double LibraryWidth = 296;
    public const double LibraryGap = 16;
    public const double CloudWidth = 360;
    public const double CloudGap = 16;

    private double _width;
    private bool _libraryRequested = true;
    private bool _cloudRequested;

    public NativeNoteLayoutState(double width)
    {
        Resize(width);
    }

    public NativeNoteLayout Current => Calculate();

    public void Resize(double width)
    {
        var normalized = double.IsFinite(width) ? Math.Max(0, width) : 0;
        var previousMode = GetMode(_width);
        var nextMode = GetMode(normalized);
        _width = normalized;
        if (nextMode == NativeNoteLayoutMode.Narrow
            && previousMode != NativeNoteLayoutMode.Narrow)
        {
            _libraryRequested = false;
            _cloudRequested = false;
        }
    }

    public void ToggleLibrary()
    {
        var currentlyVisible = Current.LibraryVisible;
        _libraryRequested = !currentlyVisible;
        if (_libraryRequested && Current.LibraryOverlay)
        {
            _cloudRequested = false;
        }
    }

    public void SetLibraryExpanded(bool expanded)
    {
        _libraryRequested = expanded;
        if (expanded && Current.LibraryOverlay)
        {
            _cloudRequested = false;
        }
    }

    public void ToggleCloud(bool noteAvailable)
    {
        if (!noteAvailable)
        {
            _cloudRequested = false;
            return;
        }

        var currentlyVisible = Current.CloudVisible;
        _cloudRequested = !currentlyVisible;
        if (_cloudRequested
            && Current.Mode == NativeNoteLayoutMode.Narrow)
        {
            _libraryRequested = false;
        }
    }

    public void CollapseCloud()
    {
        _cloudRequested = false;
    }

    public void CollapseAll()
    {
        _libraryRequested = false;
        _cloudRequested = false;
    }

    public static NativeNoteLayoutMode GetMode(double width)
        => width < NarrowWidth
            ? NativeNoteLayoutMode.Narrow
            : width < WideWidth
                ? NativeNoteLayoutMode.Split
                : NativeNoteLayoutMode.Wide;

    private NativeNoteLayout Calculate()
    {
        var mode = GetMode(_width);
        if (mode == NativeNoteLayoutMode.Narrow)
        {
            return new NativeNoteLayout(
                mode,
                LibraryVisible: _libraryRequested,
                LibraryOverlay: _libraryRequested,
                CloudVisible: !_libraryRequested && _cloudRequested,
                CloudOverlay: !_libraryRequested && _cloudRequested,
                SingleColumn: true);
        }

        var libraryDocked = _libraryRequested
            && _width >= LibraryWidth + LibraryGap + MinimumEditorWidth;
        var bothDocked = mode == NativeNoteLayoutMode.Wide
            && libraryDocked
            && _cloudRequested
            && _width >= LibraryWidth
                + LibraryGap
                + CloudWidth
                + CloudGap
                + MinimumEditorWidth;
        var cloudDockedAlone = _cloudRequested
            && !libraryDocked
            && _width >= CloudWidth + CloudGap + MinimumEditorWidth;
        var cloudVisible = _cloudRequested;
        return new NativeNoteLayout(
            mode,
            LibraryVisible: libraryDocked,
            LibraryOverlay: false,
            CloudVisible: cloudVisible,
            CloudOverlay: cloudVisible && !bothDocked && !cloudDockedAlone,
            SingleColumn: !libraryDocked && !bothDocked && !cloudDockedAlone);
    }
}
