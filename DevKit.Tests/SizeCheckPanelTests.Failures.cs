using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What the Size Check panel says when TFS fails it: what to do about it, worded by TfsErrors.Describe, with
/// each failure logged once at Error. A requirement whose tasks could not be read is reported, not shown as
/// having no hours.
/// </summary>
public sealed partial class SizeCheckPanelTests
{
    private const string Refused = """{"message":"TF400813: The user is not authorized to access this resource."}""";
    private const string Broken = """{"message":"Internal error."}""";
    private const int SizeFields = 3;

    private static readonly string TokenAdvice =
        Web.Services.TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.Unauthorized));

    private static readonly string ServerAdvice =
        Web.Services.TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.InternalServerError));

    [Fact]
    public async Task A_size_tfs_refuses_says_what_to_do_keeps_the_editor_open_and_logs_each_field_tried()
    {
        var panel = await OpenSizeEditorAsync();
        _tfs.Respond = sent => sent.Method == "PATCH" ? (HttpStatusCode.Unauthorized, Refused) : AnswerSprint(sent);

        await TypeAsync(panel, "8");
        await PressAsync(panel, "Enter");

        Assert.Contains(await _harness.ElementsAsync(panel), e => e.Text.Contains("Update failed: " + TokenAdvice, StringComparison.Ordinal));
        Assert.Contains(await _harness.ElementsAsync(panel), IsSizeInput);
        Assert.Equal(SizeFields, Errors().Count);
    }

    [Fact]
    public async Task A_requirement_whose_tasks_cannot_be_read_is_reported_rather_than_shown_without_hours()
    {
        _tfs.Respond = sent => IsChildTaskQuery(sent) ? (HttpStatusCode.InternalServerError, Broken) : AnswerSprint(sent);

        var panel = await LoadSprintAsync();

        Assert.Contains(await _harness.ElementsAsync(panel), e =>
            e.Text.Contains("Tasks of 1 requirement(s) could not be read", StringComparison.Ordinal)
            && e.Text.Contains(ServerAdvice, StringComparison.Ordinal));
        Assert.Single(Errors());
    }

    [Fact]
    public async Task A_sprint_that_cannot_be_read_says_why_and_is_logged_once()
    {
        _tfs.Respond = _ => (HttpStatusCode.InternalServerError, Broken);

        var panel = await LoadSprintAsync();

        Assert.Contains(await _harness.ElementsAsync(panel), e => e.Text.Contains("Load failed: " + ServerAdvice, StringComparison.Ordinal));
        Assert.Single(Errors());
    }

    // The floating note is for size saves; a load reports where every Sprint Insights tab does, under the toolbar.
    [Fact]
    public async Task A_failed_load_is_an_alert_under_the_toolbar_not_the_note_for_size_saves()
    {
        _tfs.Respond = _ => (HttpStatusCode.InternalServerError, Broken);

        var panel = await LoadSprintAsync();

        var elements = await _harness.ElementsAsync(panel);
        Assert.Contains(elements, e => e.Attribute("role") == "alert" && e.Text.Contains("Load failed: " + ServerAdvice, StringComparison.Ordinal));
        Assert.DoesNotContain(elements, IsSaveNote);
    }

    [Fact]
    public async Task A_sprint_with_no_requirements_is_an_answer_not_a_failure()
    {
        _tfs.Respond = sent => sent.Method == "POST" ? (HttpStatusCode.OK, """{"workItems":[]}""") : AnswerSprint(sent);

        var panel = await LoadSprintAsync();

        var elements = await _harness.ElementsAsync(panel);
        Assert.DoesNotContain(elements, e => e.Attribute("role") == "alert");
        Assert.DoesNotContain(elements, IsSaveNote);
        Assert.Contains(elements, e => e.Attribute("role") == "status"
            && e.Text.Contains("no requirements or change requests", StringComparison.Ordinal));
        Assert.Empty(Errors());
    }

    private static bool IsSaveNote(ComponentHarness.Element e) =>
        e.Attribute("class")?.Contains("sc-toast", StringComparison.Ordinal) == true;

    private static bool IsChildTaskQuery(RecordingTfsHandler.Request sent) =>
        sent.Method == "POST" && sent.Body?.Contains("WorkItemLinks", StringComparison.Ordinal) == true;

    private List<Exception?> Errors()
    {
        lock (_log.Entries)
        {
            return _log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Exception).ToList();
        }
    }
}
