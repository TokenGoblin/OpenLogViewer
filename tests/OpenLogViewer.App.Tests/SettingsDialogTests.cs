using System.Linq;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Turning a firmware's description of a settings page into one that can be
/// drawn and edited.
/// </summary>
public class SettingsDialogTests
{
    private const string Ini = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           knk_option  = bits, U08, 2, [0:0], "Off", "On"
           knk_mode    = bits, U08, 2, [1:2], "Analogue", "Digital", "INVALID", "INVALID"
           dwell       = scalar, U08, 4, "ms", 0.1, 0, 0, 12, 1

        [UserDefined]
           dialog = knockInner, ""
              field = "Mode", knk_mode
              field = "Threshold", dwell, { knk_option }

           dialog = knock, "Knock Settings", yAxis
              field = "Knock detection", knk_option
              field = ""
              panel = knockInner, Center, { knk_option }

           dialog = engine, "Engine", yAxis
              field = "Cranking RPM", crankingRPM
              field = "Nothing behind this", noSuchConstant
              graphLine = someGraph

           dialog = loopA, ""
              panel = loopB
           dialog = loopB, ""
              panel = loopA
              field = "Still here", crankingRPM
        """;

    private static (SettingsDialog Dialog, TuneSettingsEdit Edit) Open(string name, params (int At, byte Value)[] bytes)
    {
        TuneLayout layout = TuneLayoutReader.Read(Ini);
        TuneInterface ui = TuneInterfaceReader.Read(Ini);

        var page = new byte[32];
        foreach ((int at, byte value) in bytes) page[at] = value;

        EcuTune tune = EcuTune.FromPages(layout, page);
        var edit = new TuneSettingsEdit(tune);

        SettingsDialog dialog = SettingsDialog.Build(name, ui, tune.Constant, edit)!;
        dialog.Refresh(n => edit.Value(n));

        return (dialog, edit);
    }

    // ----- building ---------------------------------------------------------

    [Fact]
    public void ADialogTakesItsTitleAndItsFields()
    {
        (SettingsDialog dialog, _) = Open("engine");

        Assert.Equal("Engine", dialog.Title);
        Assert.Contains(dialog.Visible, r => r.Label == "Cranking RPM");
    }

    [Fact]
    public void AFieldIsDrawnAsWhatItIs()
    {
        (SettingsDialog dialog, _) = Open("knock");

        SettingRow detection = dialog.Rows.First(r => r.Label == "Knock detection");
        SettingRow rpm = Open("engine").Dialog.Rows.First(r => r.Label == "Cranking RPM");

        // A bit field with names is a choice; a plain scalar is a number.
        Assert.Equal(SettingKind.Choice, detection.Kind);
        Assert.Equal(SettingKind.Number, rpm.Kind);
        Assert.Equal("rpm", rpm.Units);
    }

    [Fact]
    public void ThePaddingOnABitFieldIsNotOfferedAsAChoice()
    {
        // knk_mode is two bits, so the firmware declares four names to fill the
        // width out. Two of them are INVALID and are not things the ECU does.
        (SettingsDialog dialog, _) = Open("knockInner");

        SettingRow mode = dialog.Rows.First(r => r.Label == "Mode");

        Assert.Equal(["Analogue", "Digital"], mode.Options);
    }

    [Fact]
    public void AFieldWithNothingBehindItIsNotOfferedForEditing()
    {
        // Naming a constant this firmware does not declare. A box that cannot be
        // typed into would look broken; a caption reads as what it is.
        (SettingsDialog dialog, _) = Open("engine");

        SettingRow row = dialog.Rows.First(r => r.Label == "Nothing behind this");

        Assert.Equal(SettingKind.Caption, row.Kind);
        Assert.False(row.IsEditable);
    }

    [Fact]
    public void SomethingThisCannotDrawIsSaidRatherThanLeftOut()
    {
        (SettingsDialog dialog, _) = Open("engine");

        Assert.True(dialog.IsPartial);
        Assert.DoesNotContain(dialog.Visible, r => r.Kind == SettingKind.NotShown);
    }

    [Fact]
    public void APanelThatNamesItselfDoesNotHangTheProgram()
    {
        // A definition with a mistake in it should fail to draw a dialog, not
        // take the program down.
        (SettingsDialog dialog, _) = Open("loopA");

        Assert.Contains(dialog.Rows, r => r.Label == "Still here");
    }

    [Fact]
    public void AskingForADialogThatIsNotThereGivesNothing()
    {
        TuneLayout layout = TuneLayoutReader.Read(Ini);
        TuneInterface ui = TuneInterfaceReader.Read(Ini);
        EcuTune tune = EcuTune.FromPages(layout, new byte[32]);

        Assert.Null(SettingsDialog.Build("noSuchDialog", ui, tune.Constant, null));
    }

    // ----- conditions -------------------------------------------------------

    [Fact]
    public void AFieldIsHiddenWhenTheFirmwareSaysItDoesNotApply()
    {
        // Knock detection off, so its threshold is not a setting this tune has.
        (SettingsDialog dialog, _) = Open("knockInner", (2, 0));

        Assert.DoesNotContain(dialog.Visible, r => r.Label == "Threshold");
    }

    [Fact]
    public void TurningSomethingOnRevealsWhatItConfigures()
    {
        // The point of evaluating conditions against the edit rather than the
        // ECU: it would be strange if the fields appeared only after reopening
        // the page.
        (SettingsDialog dialog, TuneSettingsEdit edit) = Open("knockInner", (2, 0));

        Assert.DoesNotContain(dialog.Visible, r => r.Label == "Threshold");

        dialog.Rows.First(r => r.Label == "Mode").Changed += () => { };
        edit.Set("knk_option", 1);
        dialog.Refresh(n => edit.Value(n));

        Assert.Contains(dialog.Visible, r => r.Label == "Threshold");
    }

    [Fact]
    public void APanelsConditionGatesEverythingInsideIt()
    {
        (SettingsDialog off, TuneSettingsEdit edit) = Open("knock", (2, 0));

        Assert.DoesNotContain(off.Visible, r => r.Label == "Mode");

        edit.Set("knk_option", 1);
        off.Refresh(n => edit.Value(n));

        Assert.Contains(off.Visible, r => r.Label == "Mode");
    }

    [Fact]
    public void ARowShownOnlyBecauseNothingCouldBeDecidedSaysSo()
    {
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 8
               a = scalar, U08, 0, "", 1, 0, 0, 255, 0

            [UserDefined]
               dialog = d, "D"
                  field = "Mystery", a, { somethingUnknown }
            """;

        TuneLayout layout = TuneLayoutReader.Read(ini);
        EcuTune tune = EcuTune.FromPages(layout, new byte[8]);
        var edit = new TuneSettingsEdit(tune);

        SettingsDialog dialog = SettingsDialog.Build("d", TuneInterfaceReader.Read(ini), tune.Constant, edit)!;
        dialog.Refresh(n => edit.Value(n));

        SettingRow row = dialog.Rows.Single(r => r.Label == "Mystery");

        Assert.True(row.IsVisible);
        Assert.True(row.IsUncertain);
    }

    [Fact]
    public void GapsLeftByHiddenGroupsAreNotDrawnAsEmptyLines()
    {
        // "field = """ is a spacer between groups. With the group either side
        // hidden, a page of blank lines is what is left.
        (SettingsDialog dialog, _) = Open("knock", (2, 0));

        Assert.DoesNotContain(dialog.Visible, r => r.Kind == SettingKind.Caption && r.Label.Length == 0);
    }

    // ----- editing ----------------------------------------------------------

    [Fact]
    public void ANumberIsReadAndWrittenThroughTheRow()
    {
        (SettingsDialog dialog, TuneSettingsEdit edit) = Open("engine", (0, 1), (1, 44));

        SettingRow rpm = dialog.Rows.First(r => r.Label == "Cranking RPM");

        Assert.Equal("300", rpm.Value);
        Assert.False(rpm.IsChanged);

        rpm.Value = "450";

        Assert.Equal("450", rpm.Value);
        Assert.True(rpm.IsChanged);
        Assert.Equal(450, edit.Value("crankingRPM"));
    }

    [Fact]
    public void AChoiceIsReadAndWrittenByItsName()
    {
        (SettingsDialog dialog, TuneSettingsEdit edit) = Open("knockInner", (2, 0b000));

        SettingRow mode = dialog.Rows.First(r => r.Label == "Mode");

        Assert.Equal("Analogue", mode.Value);

        mode.Value = "Digital";

        Assert.Equal("Digital", mode.Value);
        Assert.Equal(1, edit.Value("knk_mode"));
    }

    [Fact]
    public void ChangingAChoiceLeavesItsNeighbourInTheSameByteAlone()
    {
        // knk_option is bit 0 and knk_mode bits 1..2, in one byte.
        (SettingsDialog dialog, TuneSettingsEdit edit) = Open("knockInner", (2, 0b001));

        dialog.Rows.First(r => r.Label == "Mode").Value = "Digital";

        Assert.Equal(1, edit.Value("knk_option"));
        Assert.Equal(1, edit.Value("knk_mode"));
    }

    [Fact]
    public void TextThatIsNotANumberIsRefusedRatherThanStored()
    {
        (SettingsDialog dialog, _) = Open("engine", (0, 1), (1, 44));

        SettingRow rpm = dialog.Rows.First(r => r.Label == "Cranking RPM");
        rpm.Value = "not a number";

        Assert.Equal("300", rpm.Value);
        Assert.False(rpm.IsChanged);
    }

    [Fact]
    public void AValueOutsideTheFirmwaresRangeIsRefused()
    {
        (SettingsDialog dialog, _) = Open("engine");

        SettingRow rpm = dialog.Rows.First(r => r.Label == "Cranking RPM");
        rpm.Value = "99999";

        Assert.False(rpm.IsChanged);
    }

    [Fact]
    public void ARowCanBePutBack()
    {
        (SettingsDialog dialog, TuneSettingsEdit edit) = Open("engine", (0, 1), (1, 44));

        SettingRow rpm = dialog.Rows.First(r => r.Label == "Cranking RPM");
        rpm.Value = "450";
        rpm.Revert();

        Assert.Equal("300", rpm.Value);
        Assert.False(rpm.IsChanged);
        Assert.False(edit.HasChanges);
    }

    [Fact]
    public void AReadOnlyFieldIsNotEditable()
    {
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 8
               a = scalar, U08, 0, "", 1, 0, 0, 255, 0

            [UserDefined]
               dialog = d, "D"
                  displayOnlyField = "Reading", a
            """;

        TuneLayout layout = TuneLayoutReader.Read(ini);
        EcuTune tune = EcuTune.FromPages(layout, new byte[8]);
        var edit = new TuneSettingsEdit(tune);

        SettingsDialog dialog = SettingsDialog.Build("d", TuneInterfaceReader.Read(ini), tune.Constant, edit)!;
        dialog.Refresh(n => edit.Value(n));

        SettingRow row = dialog.Rows.Single(r => r.Label == "Reading");

        Assert.Equal(SettingKind.ReadOnly, row.Kind);
        Assert.False(row.IsEditable);
    }

    // ----- an edit that will not go in ----------------------------------------

    [Fact]
    public void ARefusedEditPutsTheStoredValueBackOnScreen()
    {
        // Without this the box keeps what was typed and looks accepted: Send
        // writes nothing, and the tune does not hold the number the person is
        // reading off their own screen.
        SettingRow rpm = Open("engine").Dialog.Rows.First(r => r.Label == "Cranking RPM");

        string before = rpm.Value;
        var raised = new List<string>();
        rpm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        rpm.Value = "999999";

        Assert.Equal(before, rpm.Value);
        Assert.Contains("Value", raised);
    }

    [Fact]
    public void AndSaysWhyInTermsOfTheFirmwaresOwnLimits()
    {
        // "Invalid" tells a person nothing they can act on; the range tells them
        // exactly what to type instead.
        SettingRow rpm = Open("engine").Dialog.Rows.First(r => r.Label == "Cranking RPM");

        string? why = null;
        rpm.Refused += reason => why = reason;

        rpm.Value = "999999";

        Assert.NotNull(why);
        Assert.True(rpm.HasProblem);
        Assert.Contains("outside", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SomethingThatIsNotANumberSaysThatRatherThanARange()
    {
        SettingRow rpm = Open("engine").Dialog.Rows.First(r => r.Label == "Cranking RPM");

        string? why = null;
        rpm.Refused += reason => why = reason;

        rpm.Value = "quite fast";

        Assert.Equal("That is not a number.", why);
    }

    [Fact]
    public void AndTheComplaintClearsOnceSomethingValidIsTyped()
    {
        SettingRow rpm = Open("engine").Dialog.Rows.First(r => r.Label == "Cranking RPM");

        rpm.Value = "999999";
        Assert.True(rpm.HasProblem);

        rpm.Value = "400";

        Assert.False(rpm.HasProblem);
        Assert.Equal("400", rpm.Value);
    }
}
