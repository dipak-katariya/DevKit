using System.Text.Json;
using DevKit.Web.Models;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What Update status writes to a work item, worked out from the person's settings and what the work item holds now:
/// tags are only ever added to the ones it has, and the field is set only to a value its type allows, in the type's
/// own spelling. Also how the settings are cleaned before they are kept or used.
/// </summary>
public sealed class StatusUpdatePlannerTests
{
    private const string StatusReference = "Casepoint.TFS.CustomFields.Status";
    private const string InPt = "In PT";
    private const string PtMerged = "PT Merged";

    private static readonly WorkItemTypeField Status = new(StatusReference, "Status", new[] { "In Dev", InPt, "Done" });

    private static StatusUpdateSettings Settings(bool addTags = true, bool setField = true, string value = InPt) => new()
    {
        AddTags = addTags,
        Tags = new List<string> { PtMerged },
        SetField = setField,
        Field = StatusReference,
        FieldName = "Status",
        Value = value
    };

    private static WorkItemForUpdate Item(string tags = "", string status = "In Dev", string type = "Requirement") =>
        new(1001, 7, "Contoso", type, "Login page", tags, status);

    [Fact]
    public void Tags_go_after_the_ones_the_work_item_has_and_none_of_those_is_dropped()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(setField: false), Item("Deliverable; ALM"), Array.Empty<WorkItemTypeField>());

        Assert.Equal(new[] { PtMerged }, plan.TagsToAdd);
        Assert.Equal("Deliverable; ALM; PT Merged", plan.NewTags);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void A_tag_the_work_item_has_in_any_case_is_not_added_again_and_the_tags_are_not_written()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(setField: false), Item("ALM;pt merged"), Array.Empty<WorkItemTypeField>());

        Assert.Empty(plan.TagsToAdd);
        Assert.Null(plan.NewTags);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void A_tag_listed_twice_in_the_settings_is_added_once()
    {
        var settings = Settings(setField: false);
        settings.Tags = new List<string> { PtMerged, "pt merged", "QA Done" };

        var plan = StatusUpdatePlanner.Plan(settings, Item(), Array.Empty<WorkItemTypeField>());

        Assert.Equal(new[] { PtMerged, "QA Done" }, plan.TagsToAdd);
        Assert.Equal("PT Merged; QA Done", plan.NewTags);
    }

    [Fact]
    public void Tags_are_left_alone_while_Add_tags_is_off()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(addTags: false), Item("ALM"), new[] { Status });

        Assert.Empty(plan.TagsToAdd);
        Assert.Null(plan.NewTags);
    }

    [Fact]
    public void The_field_is_set_in_the_spelling_its_type_allows()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(addTags: false, value: "in pt"), Item(), new[] { Status });

        var field = Assert.IsType<StatusFieldChange>(plan.Field);
        Assert.Equal(StatusReference, field.Reference);
        Assert.Equal("In Dev", field.Current);
        Assert.Equal(InPt, field.NewValue);
        Assert.Null(field.Note);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void A_field_already_at_the_value_is_not_written()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(addTags: false), Item(status: InPt), new[] { Status });

        var field = Assert.IsType<StatusFieldChange>(plan.Field);
        Assert.Null(field.NewValue);
        Assert.Null(field.Note);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void A_type_without_the_field_keeps_its_own_and_says_why_while_the_tags_are_still_added()
    {
        var other = new WorkItemTypeField("System.State", "State", new[] { "New", "Closed" });

        var plan = StatusUpdatePlanner.Plan(Settings(), Item(type: "Task"), new[] { other });

        var field = Assert.IsType<StatusFieldChange>(plan.Field);
        Assert.Null(field.NewValue);
        Assert.Equal("A Task has no Status field, so this one has none to set.", field.Note);
        Assert.Equal("PT Merged", plan.NewTags);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void A_value_the_type_does_not_allow_is_not_written_and_the_note_says_so()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(addTags: false, value: "Shipped"), Item(), new[] { Status });

        var field = Assert.IsType<StatusFieldChange>(plan.Field);
        Assert.Null(field.NewValue);
        Assert.Equal("Status cannot be \"Shipped\" on a Requirement, so it stays In Dev.", field.Note);
        Assert.False(plan.HasChanges);
    }

    [Theory]
    [InlineData(StatusReference, "Status")]
    [InlineData("System.Tags", "Tags")]
    [InlineData("System.Title", "Title")]
    public void A_field_without_a_fixed_list_on_the_type_is_never_written(string reference, string name)
    {
        var free = new WorkItemTypeField(reference, name, Array.Empty<string>());
        var settings = Settings(value: "Anything");
        settings.Field = reference;

        var plan = StatusUpdatePlanner.Plan(settings, Item(status: ""), new[] { free });

        var field = Assert.IsType<StatusFieldChange>(plan.Field);
        Assert.Null(field.NewValue);
        Assert.Equal($"{name} has no fixed list of values on a Requirement, so Update status does not set it.", field.Note);
        Assert.Equal("PT Merged", plan.NewTags);
    }

    [Fact]
    public void A_re_read_plan_stays_within_the_shown_one_when_only_other_things_changed()
    {
        var shown = StatusUpdatePlanner.Plan(Settings(), Item("ALM"), new[] { Status });
        var later = StatusUpdatePlanner.Plan(Settings(), Item("ALM; Hotfix") with { Rev = 8 }, new[] { Status });

        Assert.True(later.StaysWithin(shown));
    }

    [Theory]
    [InlineData("ALM", "In PT")]
    [InlineData("ALM; PT Merged", "In Dev")]
    [InlineData("ALM; PT Merged", "In PT")]
    public void A_re_read_plan_with_part_of_the_shown_change_already_made_by_someone_else_stays_within_it(string tags, string status)
    {
        var shown = StatusUpdatePlanner.Plan(Settings(), Item("ALM"), new[] { Status });
        var later = StatusUpdatePlanner.Plan(Settings(), Item(tags, status), new[] { Status });

        Assert.True(later.StaysWithin(shown));
    }

    [Fact]
    public void A_re_read_plan_with_one_of_two_configured_tags_added_meanwhile_stays_within_the_shown_one()
    {
        var settings = Settings(setField: false);
        settings.Tags = new List<string> { PtMerged, "QA Ready" };
        var shown = StatusUpdatePlanner.Plan(settings, Item("ALM"), Array.Empty<WorkItemTypeField>());
        var later = StatusUpdatePlanner.Plan(settings, Item("ALM; QA Ready"), Array.Empty<WorkItemTypeField>());

        Assert.True(later.StaysWithin(shown));
        Assert.Equal("ALM; QA Ready; PT Merged", later.NewTags);
    }

    [Fact]
    public void A_re_read_plan_is_another_change_when_the_field_now_starts_from_another_value()
    {
        var shown = StatusUpdatePlanner.Plan(Settings(), Item("ALM"), new[] { Status });
        var later = StatusUpdatePlanner.Plan(Settings(), Item("ALM", "Done"), new[] { Status });

        Assert.False(later.StaysWithin(shown));
    }

    [Fact]
    public void A_re_read_plan_is_another_change_when_it_adds_a_tag_the_shown_one_did_not()
    {
        var shown = StatusUpdatePlanner.Plan(Settings(), Item("ALM; PT Merged"), new[] { Status });
        var later = StatusUpdatePlanner.Plan(Settings(), Item("ALM"), new[] { Status });

        Assert.False(later.StaysWithin(shown));
    }

    [Fact]
    public void A_re_read_plan_is_another_change_when_it_sets_a_field_the_shown_one_left_alone()
    {
        var shown = StatusUpdatePlanner.Plan(Settings(), Item("ALM", InPt), new[] { Status });
        var later = StatusUpdatePlanner.Plan(Settings(), Item("ALM", "Done"), new[] { Status });

        Assert.False(later.StaysWithin(shown));
    }

    [Fact]
    public void A_field_left_alone_both_times_stays_within_whatever_its_value()
    {
        var shown = StatusUpdatePlanner.Plan(Settings(value: "Shipped"), Item(status: "In Dev"), new[] { Status });
        var later = StatusUpdatePlanner.Plan(Settings(value: "Shipped"), Item(status: "Done"), new[] { Status });

        Assert.True(later.StaysWithin(shown));
    }

    [Fact]
    public void A_field_change_says_what_happens_in_one_wording_for_the_dialog_and_the_summary()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(addTags: false), Item(status: "In Dev"), new[] { Status });

        Assert.Equal(StatusFieldOutcome.Set, plan.Field!.Outcome);
        Assert.Equal("In Dev → In PT", plan.Field.Describe());
        Assert.Equal("Status In Dev → In PT", plan.Summary());
        Assert.Equal("Already In PT", StatusFieldChange.AlreadySet(StatusReference, "Status", InPt).Describe());
        Assert.Equal("Why not", StatusFieldChange.Skipped(StatusReference, "Status", "", "Why not").Describe());
    }

    [Fact]
    public void The_tag_field_written_is_worked_out_from_the_tags_the_work_item_has()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(setField: false), Item(" ALM ;; Deliverable"), Array.Empty<WorkItemTypeField>());

        Assert.Equal("ALM; Deliverable; PT Merged", plan.NewTags);
    }

    [Fact]
    public void Normalizing_says_what_it_leaves_out_without_the_values()
    {
        var settings = new StatusUpdateSettings
        {
            Tags = new List<string> { PtMerged, "a;b" },
            SetField = true,
            Field = "Status",
            Value = new string('v', StatusUpdateSettings.MaxValueLength + 1)
        };
        var log = new RecordingLogger<StatusUpdatePlannerTests>();

        var normalized = settings.Normalized(log);

        Assert.Equal(new[] { PtMerged }, normalized.Tags);
        Assert.Equal(3, settings.NormalizingDrops().Count);
        Assert.Equal(3, log.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Empty(new StatusUpdateSettings { Tags = new List<string> { PtMerged } }.NormalizingDrops());
    }

    [Fact]
    public void No_field_is_planned_while_Set_a_field_is_off()
    {
        var plan = StatusUpdatePlanner.Plan(Settings(setField: false), Item(), new[] { Status });

        Assert.Null(plan.Field);
    }

    [Fact]
    public void The_summary_names_what_changed_and_shows_an_empty_value_as_empty()
    {
        var both = StatusUpdatePlanner.Plan(Settings(), Item(status: ""), new[] { Status });
        var none = StatusUpdatePlanner.Plan(Settings(), Item(PtMerged, InPt), new[] { Status });

        Assert.Equal("added PT Merged · Status (empty) → In PT", both.Summary());
        Assert.Equal("nothing to change", none.Summary());
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData(" ALM ;; Deliverable ;", new[] { "ALM", "Deliverable" })]
    [InlineData("PT Merged", new[] { "PT Merged" })]
    public void A_tag_field_splits_into_its_tags_without_blanks(string tags, string[] expected)
    {
        Assert.Equal(expected, WorkItem.SplitTags(tags));
    }

    [Fact]
    public void Normalizing_trims_tags_and_drops_blanks_repeats_and_ones_TFS_cannot_keep()
    {
        var settings = new StatusUpdateSettings
        {
            Tags = new List<string> { " PT Merged ", "", "pt merged", "a;b", null!, new string('x', StatusUpdateSettings.MaxTagLength + 1), "QA" }
        };

        Assert.Equal(new[] { PtMerged, "QA" }, settings.Normalized().Tags);
    }

    [Fact]
    public void Normalizing_keeps_at_most_the_first_twenty_tags()
    {
        var settings = new StatusUpdateSettings { Tags = Enumerable.Range(1, 25).Select(n => $"Tag {n}").ToList() };

        var tags = settings.Normalized().Tags;

        Assert.Equal(StatusUpdateSettings.MaxTags, tags.Count);
        Assert.Equal("Tag 20", tags[^1]);
    }

    [Fact]
    public void Normalizing_drops_a_field_that_is_not_a_reference_name_with_its_name_and_caps_the_value()
    {
        var settings = new StatusUpdateSettings
        {
            SetField = true,
            Field = "System.State/../../_apis",
            FieldName = "State",
            Value = new string('v', StatusUpdateSettings.MaxValueLength + 10)
        };

        var normalized = settings.Normalized();

        Assert.Equal("", normalized.Field);
        Assert.Equal("", normalized.FieldName);
        Assert.Equal(StatusUpdateSettings.MaxValueLength, normalized.Value.Length);
        Assert.False(normalized.HasFieldToSet());
    }

    [Theory]
    [InlineData("System.State", true)]
    [InlineData("Casepoint.TFS.CustomFields.Status", true)]
    [InlineData("Custom_Fields.Status_2", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Status", false)]
    [InlineData("System.", false)]
    [InlineData(".State", false)]
    [InlineData("System..State", false)]
    [InlineData("1System.State", false)]
    [InlineData("System.State/relations", false)]
    [InlineData("System.State?api-version=1.0", false)]
    [InlineData("System State.Value", false)]
    [InlineData("../System.State", false)]
    [InlineData("System.Ståte", false)]
    public void Only_dotted_words_of_letters_digits_and_underscores_are_field_references(string? reference, bool expected)
    {
        Assert.Equal(expected, StatusUpdateSettings.IsFieldReference(reference));
    }

    [Fact]
    public void A_field_reference_longer_than_any_TFS_has_is_refused()
    {
        Assert.False(StatusUpdateSettings.IsFieldReference("System." + new string('a', 125)));
    }

    [Theory]
    [InlineData("PT Merged", null)]
    [InlineData("  ", "Type a tag first.")]
    [InlineData("PT;Merged", "A tag cannot contain ';', which TFS puts between tags.")]
    public void A_tag_TFS_cannot_keep_is_explained(string tag, string? problem)
    {
        Assert.Equal(problem, StatusUpdateSettings.TagProblem(tag));
    }

    [Fact]
    public void A_tag_longer_than_the_limit_is_explained()
    {
        Assert.Equal($"A tag is at most {StatusUpdateSettings.MaxTagLength} characters.",
            StatusUpdateSettings.TagProblem(new string('x', StatusUpdateSettings.MaxTagLength + 1)));
    }

    [Theory]
    [InlineData(true, true, true, true, "adds PT Merged and sets Status to In PT")]
    [InlineData(true, true, false, true, "adds PT Merged")]
    [InlineData(false, true, true, true, "sets Status to In PT")]
    [InlineData(true, false, false, false, "does nothing yet")]
    [InlineData(false, true, false, false, "does nothing yet")]
    public void The_button_is_on_only_with_tags_to_add_or_a_field_and_value_to_set(
        bool addTags, bool withTags, bool setField, bool hasWork, string described)
    {
        var settings = Settings(addTags, setField);
        if (!withTags)
        {
            settings.Tags.Clear();
        }

        Assert.Equal(hasWork, settings.HasWork());
        Assert.Equal(described, settings.Describe());
    }

    [Fact]
    public void A_field_with_no_value_chosen_sets_nothing()
    {
        var settings = Settings(addTags: false, value: "");

        Assert.False(settings.HasFieldToSet());
        Assert.False(settings.HasWork());
    }

    [Fact]
    public void Before_TFS_names_the_field_its_reference_name_is_shown()
    {
        var settings = Settings(addTags: false);
        settings.FieldName = "";

        Assert.Equal($"sets {StatusReference} to In PT", settings.Describe());
    }

    [Fact]
    public void A_normalized_copy_shares_no_list_with_the_settings_it_came_from()
    {
        var settings = Settings();
        using var later = JsonDocument.Parse("""{ "Nested": [1, 2] }""");
        settings.UnknownFields = new Dictionary<string, JsonElement> { ["FromLater"] = later.RootElement.Clone() };

        var copy = settings.Normalized();
        copy.Tags.Add("QA Done");
        copy.UnknownFields!.Remove("FromLater");

        Assert.Equal(new[] { PtMerged }, settings.Tags);
        Assert.True(settings.UnknownFields.ContainsKey("FromLater"));
    }
}
