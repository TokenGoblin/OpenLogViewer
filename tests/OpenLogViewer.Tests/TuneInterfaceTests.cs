using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading the settings interface out of an INI.
///
/// Every awkward case here was found in a real definition file — MS2Extra, MS3,
/// rusEFI and Speeduino between them — and is reproduced in miniature rather
/// than by checking a copy of somebody's firmware into the repository. Each test
/// says which shape it is about, because the shapes are not guessable from the
/// documentation and the next person to touch this will want to know why the
/// parser is not simpler than it is.
/// </summary>
public class TuneInterfaceTests
{
    private static TuneInterface Read(string ini) => TuneInterfaceReader.Read(ini);

    // ----- menus ------------------------------------------------------------

    [Fact]
    public void MenusAndTheirEntriesAreRead()
    {
        TuneInterface ui = Read("""
            [Menu]
               menuDialog = main
                  menu = "Basic/Load Settings"
                    subMenu = base, "Engine and Sequential Settings"
                    subMenu = revlimiter2, "Rev Limiter"
                  menu = "Spark Settings"
                    subMenu = sparkSettings, "Spark Settings"
            """);

        Assert.Equal(2, ui.Menus.Count);
        Assert.Equal("Basic/Load Settings", ui.Menus[0].Title);
        Assert.Equal(2, ui.Menus[0].Entries.Count);
        Assert.Equal("base", ui.Menus[0].Entries[0].Dialog);
        Assert.Equal("Engine and Sequential Settings", ui.Menus[0].Entries[0].Title);
        Assert.Single(ui.Menus[1].Entries);
    }

    [Fact]
    public void ASeparatorIsAnEntryButNotAThingToOpen()
    {
        TuneInterface ui = Read("""
            [Menu]
               menu = "Settings"
                 subMenu = base, "Base"
                 subMenu = std_separator
                 subMenu = other, "Other"
            """);

        Assert.Equal(3, ui.Menus[0].Entries.Count);
        Assert.True(ui.Menus[0].Entries[1].IsSeparator);
        Assert.False(ui.Menus[0].Entries[0].IsSeparator);
    }

    [Fact]
    public void TheToolsOwnEditorsAreRecognisedAsSuch()
    {
        TuneInterface ui = Read("""
            [Menu]
               menu = "Tools"
                 subMenu = std_realtime, "&Realtime Display"
                 subMenu = std_ms2gentherm, "Thermistor tables"
                 subMenu = mine, "Mine"
            """);

        Assert.True(ui.Menus[0].Entries[0].IsBuiltIn);
        Assert.True(ui.Menus[0].Entries[1].IsBuiltIn);
        Assert.False(ui.Menus[0].Entries[2].IsBuiltIn);
    }

    [Fact]
    public void AMenuEntryCarriesTheConditionThatOffersIt()
    {
        // The page number sits between the title and the condition, so the
        // condition cannot be found by counting arguments.
        TuneInterface ui = Read("""
            [Menu]
               menu = "Settings"
                 subMenu = barometerCorr, "Barometric Correction", 0, {baroCorr}
            """);

        MenuEntry entry = ui.Menus[0].Entries[0];

        Assert.True(entry.HasCondition);
        Assert.Equal("baroCorr", entry.Condition);
        Assert.Equal("Barometric Correction", entry.Title);
    }

    [Fact]
    public void ATitleMayFollowTheNameWithNoCommaBetweenThem()
    {
        // rusEFI writes its menus this way. Left alone the name comes out with a
        // caption stuck to the end of it and matches no dialog at all.
        TuneInterface ui = Read("""
            [Menu]
               menu = "Controller"
                 subMenu = dcMotorActuatorHw			"DC motor actuator(s) hardware", { 1 }, { uiMode == 0 }
            """);

        MenuEntry entry = ui.Menus[0].Entries[0];

        Assert.Equal("dcMotorActuatorHw", entry.Dialog);
        Assert.Equal("DC motor actuator(s) hardware", entry.Title);
    }

    // ----- dialogs and fields -----------------------------------------------

    [Fact]
    public void ADialogCarriesItsTitleLayoutAndFields()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = base, "Engine Settings", xAxis
                  field = "Cranking RPM", crankingRPM
                  field = "Squirts Per Cycle", divider
            """);

        TuneDialog dialog = ui.Find("base")!;

        Assert.Equal("Engine Settings", dialog.Title);
        Assert.True(dialog.LaysOutAcross);
        Assert.Equal(2, dialog.Items.Count);
        Assert.Equal(DialogItemKind.Field, dialog.Items[0].Kind);
        Assert.Equal("crankingRPM", dialog.Items[0].Target);
    }

    [Fact]
    public void ADialogThatDoesNotSayLaysItsItemsDownwards()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = etc_set, ""
                  field = "A", a
            """);

        Assert.False(ui.Find("etc_set")!.LaysOutAcross);
    }

    [Fact]
    public void AFieldWithNoConstantIsACaptionOrASpacer()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "Per Gear Targets:"
                  field = ""
                  field = "Boost", boostTarget
            """);

        TuneDialog dialog = ui.Find("d")!;

        Assert.Equal(DialogItemKind.Label, dialog.Items[0].Kind);
        Assert.Equal("Per Gear Targets:", dialog.Items[0].Label);
        Assert.Equal(DialogItemKind.Label, dialog.Items[1].Kind);
        Assert.Equal("", dialog.Items[1].Label);
        Assert.Equal(DialogItemKind.Field, dialog.Items[2].Kind);
    }

    [Fact]
    public void AReadOnlyFieldIsNotOfferedForEditing()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  displayOnlyField = "Injector test", dummyfield, {(status8 & 0x03) == 1}
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal(DialogItemKind.ReadOnlyField, item.Kind);
        Assert.False(item.IsEditable);
        Assert.Equal("(status8 & 0x03) == 1", item.Condition);
    }

    [Fact]
    public void AConditionMayFollowTheConstantWithNoCommaBetweenThem()
    {
        // Speeduino writes fields this way, with the condition separated from
        // the constant by nothing but spaces.
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "Bypass output pin", ignBypassPin               { ignBypassEnable }
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal("ignBypassPin", item.Target);
        Assert.Equal("ignBypassEnable", item.Condition);
    }

    [Fact]
    public void EmptyBracesArePlaceholdersRatherThanConditions()
    {
        // Also Speeduino: {} stands in for an argument that is not being given,
        // and the real condition is further along the line.
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "!Warning: not enough channels", {}, {}, { injLayout == 3 }
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal(DialogItemKind.Label, item.Kind);
        Assert.Equal("injLayout == 3", item.Condition);
        Assert.Equal("", item.Target);
    }

    [Fact]
    public void AMarkedLabelIsNotSplitIntoANameAndAString()
    {
        // "!" prefixes a warning and "#" a note. Neither is an identifier, so
        // neither may be taken as the name of something.
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  displayOnlyField = !"No PWM Fan available on MCU", blankfield, {fanEnable == 2}
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal("blankfield", item.Target);
        Assert.Equal("fanEnable == 2", item.Condition);
    }

    [Fact]
    public void AnExpressionWhereTheConstantBelongsIsAComputedCaption()
    {
        // MS3 builds a label from a lookup rather than naming a constant. Read
        // as a condition it would hide the field whenever that text happened to
        // come out as zero.
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  displayOnlyField = "Injector A", { bitStringValue( portLabels, portusage_a[0] ) }
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal(DialogItemKind.Label, item.Kind);
        Assert.False(item.HasCondition);
    }

    [Fact]
    public void AFieldMayAddressOneElementOfAnArray()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "Enabled", psEnabled[2]
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal("psEnabled[2]", item.Target);
        Assert.Equal("psEnabled", item.TargetConstant);
        Assert.Equal(2, item.TargetIndex);
    }

    [Fact]
    public void AFieldOnTheWholeArrayHasNoIndex()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "Bins", rpmBins
            """);

        Assert.Equal(-1, ui.Find("d")!.Items[0].TargetIndex);
        Assert.Equal("rpmBins", ui.Find("d")!.Items[0].TargetConstant);
    }

    // ----- the other kinds of line ------------------------------------------

    [Fact]
    public void PanelsCarryAPositionAConditionBothOrNeither()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  panel = plain
                  panel = placed, Center
                  panel = gated, {opt_on}
                  panel = both, South, {als_opt_fc}
            """);

        var items = ui.Find("d")!.Items;

        Assert.Equal(("plain", "", ""), (items[0].Target, items[0].Position, items[0].Condition));
        Assert.Equal(("placed", "Center", ""), (items[1].Target, items[1].Position, items[1].Condition));
        Assert.Equal(("gated", "", "opt_on"), (items[2].Target, items[2].Position, items[2].Condition));
        Assert.Equal(("both", "South", "als_opt_fc"), (items[3].Target, items[3].Position, items[3].Condition));
    }

    [Fact]
    public void ButtonsGaugesSlidersAndProseAreEachRecognised()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  commandButton = "Reset ECU", cmdenginereset, {rpm == 0}
                  gauge = throttleGauge
                  slider = "Sensitivity", pitlim_sensitivity, horizontal, {pitlim_opt_on}
                  text = "For current documentation, click the Web Help button,"
            """);

        var items = ui.Find("d")!.Items;

        Assert.Equal(DialogItemKind.Command, items[0].Kind);
        Assert.Equal("cmdenginereset", items[0].Target);
        Assert.Equal("rpm == 0", items[0].Condition);

        Assert.Equal(DialogItemKind.Gauge, items[1].Kind);
        Assert.Equal("throttleGauge", items[1].Target);

        Assert.Equal(DialogItemKind.Slider, items[2].Kind);
        Assert.True(items[2].IsEditable);
        Assert.Equal("pitlim_sensitivity", items[2].Target);

        Assert.Equal(DialogItemKind.Text, items[3].Kind);
    }

    [Fact]
    public void WhatIsNotDrawnYetIsRecordedRatherThanDropped()
    {
        // So a dialog holding one can say it is not showing everything, instead
        // of presenting a partial page as the whole of it.
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "A", a
                  graphLine = someLine
                  indicator = { ind }, "off", "on", white, black
            """);

        var items = ui.Find("d")!.Items;

        Assert.Equal(3, items.Count);
        Assert.Equal(2, items.Count(i => i.Kind == DialogItemKind.Unsupported));
    }

    [Fact]
    public void ADialogsHelpLinkIsKept()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = etctest, "Throttle test", yAxis
                    topicHelp = "file://$getProjectsDirPath()/docs/ref.pdf#throttletest"
                  field = "A", a
            """);

        Assert.Contains("throttletest", ui.Find("etctest")!.Help, StringComparison.Ordinal);
    }

    // ----- reading the file at all ------------------------------------------

    [Fact]
    public void OnlyTheTwoSectionsThatMatterAreRead()
    {
        // A field-looking line in another section must not become a dialog item.
        TuneInterface ui = Read("""
            [Constants]
               field = "not a dialog", nope
               dialog = notADialog, "no"

            [UserDefined]
               dialog = real, "Real"
                  field = "A", a

            [Datalog]
               field = "also not", nope2
            """);

        Assert.Single(ui.Dialogs);
        Assert.NotNull(ui.Find("real"));
        Assert.Single(ui.Find("real")!.Items);
    }

    [Fact]
    public void ACommentIsIgnoredButASemicolonInALabelIsNot()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, ""
                  field = "Set the timing; carefully", timing   ; this part is a comment
            """);

        DialogItem item = ui.Find("d")!.Items[0];

        Assert.Equal("Set the timing; carefully", item.Label);
        Assert.Equal("timing", item.Target);
    }

    [Fact]
    public void LeadingWhitespaceIsIrrelevantHoweverItIsWritten()
    {
        // rusEFI indents with tabs, MegaSquirt with spaces, and both appear at
        // several depths.
        TuneInterface ui = Read("dialog = d, \"\"\n\t\t\tfield = \"A\", a\n    field = \"B\", b\n"
            .Insert(0, "[UserDefined]\n"));

        Assert.Equal(2, ui.Find("d")!.Items.Count);
    }

    [Fact]
    public void ARedeclaredDialogReplacesTheEarlierOne()
    {
        TuneInterface ui = Read("""
            [UserDefined]
               dialog = d, "First"
                  field = "A", a
               dialog = d, "Second"
                  field = "B", b
                  field = "C", c
            """);

        Assert.Single(ui.Dialogs);
        Assert.Equal("Second", ui.Find("d")!.Title);
        Assert.Equal(2, ui.Find("d")!.Items.Count);
    }

    [Fact]
    public void AFileWithNoInterfaceIsEmptyRatherThanAFailure()
    {
        Assert.True(Read("[Constants]\npage = 1\n").IsEmpty);
        Assert.True(Read("").IsEmpty);
    }

    [Fact]
    public void LookingUpADialogIsNotCaseSensitive()
    {
        TuneInterface ui = Read("[UserDefined]\ndialog = MyDialog, \"T\"\nfield = \"A\", a\n");

        Assert.NotNull(ui.Find("mydialog"));
        Assert.Null(ui.Find("nosuchdialog"));
    }
}
