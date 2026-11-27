using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.Notes;
using Xunit;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class NoteTaskSuggestionCalendarUiTests(WpfApplicationFixture wpf)
{
    private const string EventCalendarId = "11111111-1111-1111-1111-111111111111";
    private const string TaskCalendarId = "22222222-2222-2222-2222-222222222222";
    private const string ReadOnlyCalendarId = "33333333-3333-3333-3333-333333333333";
    private const string OutlookCalendarId = "44444444-4444-4444-4444-444444444444";
    private const string OutlookBindingId = "55555555-5555-5555-5555-555555555555";
    private const string TaskBookId = "66666666-6666-6666-6666-666666666666";

    [Fact]
    public void EventCalendarPickerDisablesWhenOnlyIneligibleCalendarsRemain()
    {
        wpf.Run(() =>
        {
            using var window = new SuggestionWindow();
            window.Apply(
                Suggestion(NoteTaskSuggestionKindDto.Event, OutlookCalendarId),
                [
                    Calendar(TaskCalendarId, "任务", "task"),
                    Calendar(ReadOnlyCalendarId, "只读", "calendar", canEdit: false),
                    Calendar(
                        OutlookCalendarId,
                        "Outlook",
                        "calendar",
                        outlookBindingId: OutlookBindingId),
                ]);

            var choice = Assert.Single(window.CalendarChoices());
            Assert.True(choice.UsesServerDefault);
            Assert.Null(choice.Id);
            Assert.Equal(NoteTaskSuggestionCalendarPolicy.EventDefaultLabel, choice.Label);
            Assert.Same(choice, window.CalendarBox.SelectedItem);
            Assert.False(window.CalendarBox.IsEnabled);
            Assert.Contains(
                "没有可写的非 Outlook 日程日历",
                window.CalendarHint.Text,
                StringComparison.Ordinal);
            Assert.Contains(
                "服务器默认日程日历",
                window.CalendarHint.Text,
                StringComparison.Ordinal);
            Assert.Equal("目标日程日历", window.CalendarLabel.Text);
            Assert.Null(window.BuildDraft().CalendarId);
        });
    }

    [Fact]
    public void EventCalendarPickerKeepsEligibleSelectionAndFallsBackWhenPreferredIsIneligible()
    {
        wpf.Run(() =>
        {
            using var window = new SuggestionWindow();
            CalendarResponse[] calendars =
            [
                Calendar(EventCalendarId, "PIM 日程", "calendar"),
                Calendar(TaskCalendarId, "任务", "task"),
            ];

            window.Apply(
                Suggestion(NoteTaskSuggestionKindDto.Event, EventCalendarId),
                calendars);

            var choices = window.CalendarChoices();
            Assert.Equal(
                new string?[] { null, EventCalendarId },
                choices.Select(item => item.Id).ToArray());
            Assert.Equal(
                EventCalendarId,
                Assert.IsType<NoteTaskSuggestionCalendarChoice>(window.CalendarBox.SelectedItem).Id);
            Assert.True(window.CalendarBox.IsEnabled);
            Assert.Equal(EventCalendarId, window.BuildDraft().CalendarId);

            window.Apply(
                Suggestion(NoteTaskSuggestionKindDto.Event, TaskCalendarId),
                calendars);

            choices = window.CalendarChoices();
            Assert.Equal(
                new string?[] { null, EventCalendarId },
                choices.Select(item => item.Id).ToArray());
            Assert.True(choices[0].UsesServerDefault);
            Assert.Same(choices[0], window.CalendarBox.SelectedItem);
            Assert.Contains(
                "原建议日历不可用于日程",
                window.CalendarHint.Text,
                StringComparison.Ordinal);
            Assert.Null(window.BuildDraft().CalendarId);
        });
    }

    [Fact]
    public void TaskCalendarPickerKeepsOriginalAllCalendarBehavior()
    {
        wpf.Run(() =>
        {
            using var window = new SuggestionWindow();
            window.Apply(
                Suggestion(NoteTaskSuggestionKindDto.Task, OutlookCalendarId),
                [
                    Calendar(EventCalendarId, "PIM 日程", "calendar"),
                    Calendar(TaskCalendarId, "任务", "task"),
                    Calendar(ReadOnlyCalendarId, "只读", "calendar", canEdit: false),
                    Calendar(
                        OutlookCalendarId,
                        "Outlook",
                        "calendar",
                        outlookBindingId: OutlookBindingId),
                ]);

            var choices = window.CalendarChoices();
            Assert.Equal(
                new string?[]
                {
                    null,
                    EventCalendarId,
                    TaskCalendarId,
                    ReadOnlyCalendarId,
                    OutlookCalendarId,
                },
                choices.Select(item => item.Id).ToArray());
            Assert.Equal(
                OutlookCalendarId,
                Assert.IsType<NoteTaskSuggestionCalendarChoice>(window.CalendarBox.SelectedItem).Id);
            Assert.True(window.CalendarBox.IsEnabled);
            Assert.Equal("目标日历（任务可选）", window.CalendarLabel.Text);
            Assert.Equal(OutlookCalendarId, window.BuildDraft().CalendarId);
        });
    }

    [Fact]
    public async Task EventDefaultCalendarAndClearedTimeZoneSurviveDraftReloadAndUpdateRequest()
    {
        await wpf.RunAsync(async () =>
        {
            var handler = new UpdateRecordingHandler();
            using var window = new SuggestionWindow(handler);
            var suggestion = Suggestion(NoteTaskSuggestionKindDto.Event, EventCalendarId);
            handler.Response = suggestion;
            CalendarResponse[] calendars =
            [
                Calendar(EventCalendarId, "PIM 日程", "calendar"),
            ];

            window.Apply(suggestion, calendars);
            window.CalendarBox.SelectedItem = Assert.Single(
                window.CalendarChoices(),
                choice => choice.UsesServerDefault);
            window.TimeZoneBox.Clear();
            var cleared = window.BuildDraft();

            Assert.Null(cleared.CalendarId);
            Assert.True(cleared.CalendarIdSpecified);
            Assert.Null(cleared.TimeZoneId);
            Assert.True(cleared.TimeZoneIdSpecified);

            await window.Service.SaveLocalDraftAsync(Guid.Parse(suggestion.Id), cleared);
            var reloaded = await window.Service.LoadDraftAsync(Guid.Parse(suggestion.Id));
            Assert.NotNull(reloaded);
            window.Apply(suggestion, calendars, reloaded);

            var selected = Assert.IsType<NoteTaskSuggestionCalendarChoice>(
                window.CalendarBox.SelectedItem);
            Assert.True(selected.UsesServerDefault);
            Assert.Equal(string.Empty, window.TimeZoneBox.Text);
            var restored = window.BuildDraft();
            Assert.Null(restored.CalendarId);
            Assert.Null(restored.TimeZoneId);

            await window.Service.SaveEditAsync(suggestion, restored);
            Assert.NotNull(window.LastUpdateBody);
            using var body = JsonDocument.Parse(window.LastUpdateBody);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("calendarId").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("timeZoneId").ValueKind);
        });
    }

    [Fact]
    public async Task TaskExplicitClearsSurviveDraftReloadAndUpdateRequest()
    {
        await wpf.RunAsync(async () =>
        {
            var handler = new UpdateRecordingHandler();
            using var window = new SuggestionWindow(handler);
            var suggestion = Suggestion(NoteTaskSuggestionKindDto.Task, TaskCalendarId);
            suggestion.Description = "旧说明";
            suggestion.Due = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
            suggestion.TaskBookId = TaskBookId;
            handler.Response = suggestion;
            window.SetTaskBookChoices(
                (null, "不指定任务本"),
                (TaskBookId, "旧任务本"));
            CalendarResponse[] calendars =
            [
                Calendar(TaskCalendarId, "任务", "task"),
            ];

            window.Apply(suggestion, calendars);
            window.DescriptionBox.Clear();
            window.DueDatePicker.SelectedDate = null;
            window.TaskBookBox.SelectedIndex = 0;
            window.CalendarBox.SelectedItem = Assert.Single(
                window.CalendarChoices(),
                choice => choice.UsesServerDefault);
            var cleared = window.BuildDraft();

            Assert.Null(cleared.Description);
            Assert.True(cleared.DescriptionSpecified);
            Assert.Null(cleared.Due);
            Assert.True(cleared.DueSpecified);
            Assert.Null(cleared.TaskBookId);
            Assert.True(cleared.TaskBookIdSpecified);
            Assert.Null(cleared.CalendarId);
            Assert.True(cleared.CalendarIdSpecified);

            await window.Service.SaveLocalDraftAsync(Guid.Parse(suggestion.Id), cleared);
            var reloaded = await window.Service.LoadDraftAsync(Guid.Parse(suggestion.Id));
            Assert.NotNull(reloaded);
            window.Apply(suggestion, calendars, reloaded);

            Assert.Equal(string.Empty, window.DescriptionBox.Text);
            Assert.Null(window.DueDatePicker.SelectedDate);
            Assert.Null(window.SelectedTaskBookId());
            Assert.True(Assert.IsType<NoteTaskSuggestionCalendarChoice>(
                window.CalendarBox.SelectedItem).UsesServerDefault);
            var restored = window.BuildDraft();
            Assert.Null(restored.Description);
            Assert.Null(restored.Due);
            Assert.Null(restored.TaskBookId);
            Assert.Null(restored.CalendarId);

            await window.Service.SaveEditAsync(suggestion, restored);
            Assert.NotNull(window.LastUpdateBody);
            using var body = JsonDocument.Parse(window.LastUpdateBody);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("due").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("taskBookId").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("calendarId").ValueKind);
        });
    }

    private static NoteTaskSuggestionDto Suggestion(
        NoteTaskSuggestionKindDto kind,
        string? calendarId)
        => new()
        {
            ArtifactId = Guid.NewGuid().ToString("D"),
            CalendarId = calendarId,
            CreatedAt = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero),
            End = kind == NoteTaskSuggestionKindDto.Event
                ? new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(8))
                : null,
            ExtractionId = Guid.NewGuid().ToString("D"),
            Id = Guid.NewGuid().ToString("D"),
            Kind = kind,
            NoteId = Guid.NewGuid().ToString("D"),
            Ordinal = 1,
            Priority = 3,
            Revision = Guid.NewGuid().ToString("D"),
            SourceContentSha256 = new string('a', 64),
            SourceInputSha256 = new string('b', 64),
            SourceManifestSha256 = new string('c', 64),
            Start = kind == NoteTaskSuggestionKindDto.Event
                ? new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.FromHours(8))
                : null,
            Status = NoteTaskSuggestionStatusDto.Draft,
            TimeZoneId = kind == NoteTaskSuggestionKindDto.Event ? "Asia/Shanghai" : null,
            Title = "测试建议",
            UpdatedAt = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero),
            VersionId = Guid.NewGuid().ToString("D"),
        };

    private static CalendarResponse Calendar(
        string id,
        string name,
        string kind,
        bool canEdit = true,
        string? outlookBindingId = null)
        => new()
        {
            Id = id,
            Name = name,
            Color = "#336699",
            Kind = kind,
            IsDefault = false,
            EventCount = 0,
            CanEdit = canEdit,
            OutlookCalendarBindingId = outlookBindingId,
        };

    private sealed class SuggestionWindow : IDisposable
    {
        private static readonly MethodInfo ApplyMethod = typeof(NoteTaskSuggestionsWindow)
            .GetMethod(
                "ApplySuggestionToEditor",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo BuildDraftMethod = typeof(NoteTaskSuggestionsWindow)
            .GetMethod("BuildDraft", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo CalendarsField = typeof(NoteTaskSuggestionsWindow)
            .GetField("_calendars", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo SelectedSuggestionField = typeof(NoteTaskSuggestionsWindow)
            .GetField("_selectedSuggestion", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo AllowCloseField = typeof(NoteTaskSuggestionsWindow)
            .GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly PropertyInfo ServicesProperty = typeof(App)
            .GetProperty(nameof(App.Services), BindingFlags.Public | BindingFlags.Static)!;

        private readonly HttpMessageHandler _handler;
        private readonly IDisposable _provider;
        private readonly IServiceProvider? _previousServices;
        private readonly string _root;

        public SuggestionWindow(HttpMessageHandler? handler = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "pim-note-suggestion-calendar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _handler = handler ?? new NoNetworkHandler();
            var api = new ApiClient(() => _handler);
            var notes = new LocalNoteStore(_root);
            var account = new CloudNoteAccountScope(
                "https://example.test/api/v1",
                Guid.NewGuid().ToString("D"));
            Service = new NoteTaskSuggestionService(api, notes, () => account);
            _provider = new ServiceCollection()
                .AddSingleton(api)
                .AddSingleton(Service)
                .AddSingleton(new CloudNoteMaterialService(api, notes, () => account))
                .BuildServiceProvider();
            _previousServices = (IServiceProvider?)ServicesProperty.GetValue(null);
            ServicesProperty.SetValue(null, _provider);

            var noteId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var sourceVersionId = Guid.NewGuid().ToString("D");
            Window = new NoteTaskSuggestionsWindow(
                noteId,
                new NoteDerivedArtifactDto
                {
                    ArtifactId = artifactId.ToString("D"),
                    Kind = NoteDerivedArtifactKind.CompleteRecord,
                    NoteId = noteId.ToString("D"),
                    Revision = "1",
                    Versions = [],
                },
                new NoteDerivedVersionSummaryDto
                {
                    Author = NoteDerivedVersionAuthor.Model,
                    ContentSha256 = new string('a', 64),
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    HasConflict = false,
                    IsHead = true,
                    SourceVersionId = sourceVersionId,
                    VersionId = versionId.ToString("D"),
                },
                new NoteDerivedVersionDto
                {
                    ArtifactId = artifactId.ToString("D"),
                    Author = NoteDerivedVersionAuthor.Model,
                    ContentMarkdown = "测试正文",
                    ContentSha256 = new string('a', 64),
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    HasConflict = false,
                    InputSha256 = new string('b', 64),
                    IsHead = true,
                    Kind = NoteDerivedArtifactKind.CompleteRecord,
                    ManifestSha256 = new string('c', 64),
                    PlanVersion = "1",
                    PromptVersion = "1",
                    SourceVersionId = sourceVersionId,
                    VersionId = versionId.ToString("D"),
                },
                new NoteTaskSuggestionOperationFence(account, 1));
        }

        public NoteTaskSuggestionsWindow Window { get; }

        public NoteTaskSuggestionService Service { get; }

        public string? LastUpdateBody => (_handler as UpdateRecordingHandler)?.LastUpdateBody;

        public ComboBox CalendarBox => (ComboBox)Window.FindName("CalendarBox");

        public ComboBox TaskBookBox => (ComboBox)Window.FindName("TaskBookBox");

        public TextBox DescriptionBox => (TextBox)Window.FindName("DescriptionBox");

        public DatePicker DueDatePicker => (DatePicker)Window.FindName("DueDatePicker");

        public TextBox TimeZoneBox => (TextBox)Window.FindName("TimeZoneBox");

        public TextBlock CalendarHint => (TextBlock)Window.FindName("CalendarHintText");

        public TextBlock CalendarLabel => (TextBlock)Window.FindName("CalendarFieldLabelText");

        public void Apply(
            NoteTaskSuggestionDto suggestion,
            IReadOnlyList<CalendarResponse> calendars,
            NoteTaskSuggestionEditDraft? draft = null)
        {
            CalendarsField.SetValue(Window, calendars);
            SelectedSuggestionField.SetValue(Window, suggestion);
            ApplyMethod.Invoke(Window, [suggestion, draft]);
        }

        public IReadOnlyList<NoteTaskSuggestionCalendarChoice> CalendarChoices()
            => Assert.IsAssignableFrom<IEnumerable<NoteTaskSuggestionCalendarChoice>>(
                    CalendarBox.ItemsSource)
                .ToArray();

        public NoteTaskSuggestionEditDraft BuildDraft()
            => Assert.IsType<NoteTaskSuggestionEditDraft>(
                BuildDraftMethod.Invoke(Window, null));

        public void SetTaskBookChoices(params (string? Id, string Label)[] choices)
        {
            var choiceType = typeof(NoteTaskSuggestionsWindow)
                .GetNestedType("ChoiceRow", BindingFlags.NonPublic)!;
            var constructor = choiceType
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single();
            var items = Array.CreateInstance(choiceType, choices.Length);
            for (var index = 0; index < choices.Length; index++)
            {
                items.SetValue(
                    constructor.Invoke([choices[index].Id, choices[index].Label]),
                    index);
            }

            TaskBookBox.ItemsSource = items;
        }

        public string? SelectedTaskBookId()
        {
            var selected = TaskBookBox.SelectedItem;
            return selected is null
                ? null
                : (string?)selected.GetType().GetProperty("Id")!.GetValue(selected);
        }

        public void Dispose()
        {
            AllowCloseField.SetValue(Window, true);
            Window.Close();
            ServicesProperty.SetValue(null, _previousServices);
            _provider.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class UpdateRecordingHandler : HttpMessageHandler
    {
        public NoteTaskSuggestionDto? Response { get; set; }

        public string? LastUpdateBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put
                && request.RequestUri!.AbsolutePath.Contains(
                    "/task-suggestions/",
                    StringComparison.Ordinal))
            {
                LastUpdateBody = await request.Content!
                    .ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new NoteTaskSuggestionDtoApiResponse
                            {
                                Code = 0,
                                Data = Response,
                                Message = "ok",
                                Timestamp = DateTimeOffset.UtcNow,
                            },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
