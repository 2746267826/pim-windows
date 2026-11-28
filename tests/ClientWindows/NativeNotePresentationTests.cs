using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NativeNotePresentationTests
{
    [Fact]
    public void LibraryStateDistinguishesLoadingEmptySearchAndRetry()
    {
        var loading = NativeNotePresentation.Library(
            isLoading: true,
            hasError: false,
            noteCount: 0,
            query: null);
        Assert.Equal(NativeNoteLibraryState.Loading, loading.State);
        Assert.True(loading.ShowProgress);
        Assert.False(loading.ShowCreateAction);

        var emptyLibrary = NativeNotePresentation.Library(
            isLoading: false,
            hasError: false,
            noteCount: 0,
            query: string.Empty);
        Assert.Equal(NativeNoteLibraryState.EmptyLibrary, emptyLibrary.State);
        Assert.True(emptyLibrary.ShowCreateAction);
        Assert.Equal("从一页空白开始", emptyLibrary.Title);

        var emptySearch = NativeNotePresentation.Library(
            isLoading: false,
            hasError: false,
            noteCount: 0,
            query: "不存在");
        Assert.Equal(NativeNoteLibraryState.EmptySearch, emptySearch.State);
        Assert.True(emptySearch.ShowClearSearchAction);
        Assert.False(emptySearch.ShowCreateAction);

        var error = NativeNotePresentation.Library(
            isLoading: false,
            hasError: true,
            noteCount: 3,
            query: null);
        Assert.Equal(NativeNoteLibraryState.Error, error.State);
        Assert.True(error.ShowRetryAction);
        Assert.Contains("没有被改动", error.Detail);
    }

    [Theory]
    [InlineData("  本学期记录  ", "本学期记录 · 笔记")]
    [InlineData("", "无标题笔记")]
    public void WindowTitleUsesTheActiveNoteInsteadOfAConstantBrand(
        string noteTitle,
        string expected)
    {
        Assert.Equal(expected, NativeNotePresentation.WindowTitle(noteTitle));
    }

    [Fact]
    public void FinishedMessageStatesLocalOnlyAndNoAutomaticUpload()
    {
        var message = NativeNotePresentation.SessionFinished(
            Guid.Parse("d4e5f607-1829-4a3b-8c5d-6e7f8091a2b3"));

        Assert.Contains("d4e5f607", message);
        Assert.Contains("本机固定版本", message);
        Assert.Contains("尚未自动上传", message);
    }
}
