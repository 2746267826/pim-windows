using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

/// <summary>
/// Locks the review-confirmed wiring of the native note editor. These are
/// deliberately source-level assertions for WPF event handlers: the project
/// runs on Linux, while the handlers themselves need a Windows WPF toolchain.
/// </summary>
public sealed class NativeNoteReviewGapTests
{
    [Fact]
    public void BodyRendersOneEditableFlowInsteadOfMarkdownSourceAndPreview()
    {
        var xaml = Read("src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml");

        Assert.Contains("x:Name=\"DocumentFlowPanel\"", xaml);
        Assert.DoesNotContain("x:Name=\"MarkdownBox\"", xaml);
        Assert.DoesNotContain("连续文档预览", xaml);
        Assert.DoesNotContain("Markdown 源文", xaml);
        Assert.Contains("文字可直接编辑", xaml);
    }

    [Fact]
    public void HandwritingEntryPointsStayInsideTheBody()
    {
        var code = Read(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs");
        var xaml = Read("src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml");

        Assert.DoesNotContain("new HandwritingWindow", code);
        Assert.Contains("new InlineHandwritingEditor", code);
        Assert.Contains("FindInlineHandwritingHost", code);
        Assert.Contains("RunInlineHandwritingOperationAsync", code);
        Assert.Contains("OnAddHandwriting", xaml);
        Assert.Contains("OnEditHandwriting", xaml);
        Assert.Contains("在正文中编辑笔迹", code);
    }

    [Fact]
    public void RejectedInlineHandwritingSaveKeepsTheEditorOpen()
    {
        var code = Read(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs");
        var replace = MethodBody(code, "ReplaceFlowBlockWithEditor");
        var save = MethodBody(code, "SaveInlineHandwritingAsync");

        Assert.Contains("if (await SaveInlineHandwritingAsync", replace);
        Assert.Contains("host.Content = previousContent", replace);
        Assert.Contains("InlineHandwritingNotSavedMessage", replace);
        Assert.Contains("return started && saved", save);
    }

    [Fact]
    public void FinishCannotDiscardAnOpenInlineHandwritingEditor()
    {
        var code = Read(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs");
        var finish = MethodBody(code, "FinishCurrentNoteAsync");

        Assert.Contains("HasOpenInlineHandwritingEditor", finish);
        Assert.Contains("请先保存或取消", finish);
    }

    [Fact]
    public void CloudResultSaveAndSelectionUseTheFinishBarrier()
    {
        var code = Read(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs");
        var save = MethodBody(code, "OnSaveCloudRevision");
        var select = MethodBody(code, "OnSelectCloudResultVersion");

        Assert.Contains("TryRunNoteOperationAsync", save);
        Assert.Contains("SaveManualRevisionAsync", save);
        Assert.Contains("TryRunNoteOperationAsync", select);
        Assert.Contains("SelectDerivedVersionAsync", select);
        Assert.True(
            save.IndexOf("TryRunNoteOperationAsync", StringComparison.Ordinal)
                < save.IndexOf("SaveManualRevisionAsync", StringComparison.Ordinal),
            "The save request must start inside the note operation barrier.");
        Assert.True(
            select.IndexOf("TryRunNoteOperationAsync", StringComparison.Ordinal)
                < select.IndexOf("SelectDerivedVersionAsync", StringComparison.Ordinal),
            "The select request must start inside the note operation barrier.");
    }

    [Fact]
    public void FailureNoticesKeepConcreteDetailsRetryAndLogging()
    {
        var code = Read(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs");
        var xaml = Read("src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml");
        var showFailure = MethodBody(code, "ShowFailure");

        Assert.Contains("NativeNoteFailure.Describe", showFailure);
        Assert.Contains("NativeNoteFailure.Details", showFailure);
        Assert.Contains("Logger.Error", showFailure);
        Assert.Contains("retry", showFailure);
        Assert.Contains("NoticeDetailsExpander", xaml);
        Assert.Contains("错误详情", xaml);
        Assert.Contains("NotesStateCreateButton", xaml);
        Assert.Contains("NotesStateClearSearchButton", xaml);
        Assert.Contains("NotesStateRetryButton", xaml);
    }

    [Fact]
    public void FailureDescriptionIsStableAndTraceable()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var exception = new IOException("固定版本写入失败");

        var summary = NativeNoteFailure.Describe("结束记录", exception, noteId);
        var details = NativeNoteFailure.Details(
            "结束记录",
            exception,
            noteId,
            DateTimeOffset.Parse("2026-11-29T08:00:00+08:00"));

        Assert.Contains("结束记录", summary);
        Assert.Contains(nameof(IOException), summary);
        Assert.Contains(noteId.ToString("D"), summary);
        Assert.Contains("固定版本写入失败", summary);
        Assert.Contains("异常堆栈", details);
        Assert.Contains("2026-11-29 08:00:00 +08:00", details);
    }

    private static string Read(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }

    private static string MethodBody(string code, string methodName)
    {
        var marker = $" {methodName}(";
        var start = code.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method {methodName} was not found.");
        var bodyStart = code.IndexOf('{', start);
        Assert.True(bodyStart >= 0, $"Method {methodName} has no body.");

        var depth = 0;
        for (var index = bodyStart; index < code.Length; index++)
        {
            switch (code[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return code[bodyStart..(index + 1)];
                    }

                    break;
            }
        }

        throw new InvalidDataException($"Method {methodName} is not balanced.");
    }
}
