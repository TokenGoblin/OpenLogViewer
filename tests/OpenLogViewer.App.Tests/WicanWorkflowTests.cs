using System.IO;
using OpenLogViewer.App.Mcp;
using OpenLogViewer.Core;
using OpenLogViewer.Tests;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// A WiCAN Pro with the custom WCP firmware, through the view model and the MCP
/// tools: detected on an OBD2 connection, used alongside it, and gated where it
/// changes the unit or can reach the vehicle's bus.
///
/// <para>
/// Against <see cref="FakeWcpDevice"/> only. Nothing here has met a real unit.
/// </para>
/// </summary>
public class WicanWorkflowTests : IDisposable
{
    private readonly FakeWriteConfirmation _confirm = new();
    private readonly ImmediateUiDispatcher _ui = new();
    private readonly List<string> _temp = [];
    private MainViewModel? _vm;

    public void Dispose()
    {
        _vm?.Disconnect();

        foreach (string path in _temp)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        }
    }

    private MainViewModel NewViewModel()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"olv-wican-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temp.Add(directory);

        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.SetDataFolder(Path.Combine(directory, "workspace"));

        _vm = new MainViewModel(
            new PresetStore(Path.Combine(directory, "presets.json")),
            new FilterStore(Path.Combine(directory, "filters.json")),
            settings,
            new MathChannelStore(Path.Combine(directory, "math.json")),
            confirmation: _confirm);

        return _vm;
    }

    private MainViewModel Connected(out FakeWcpDevice device, bool stock = false)
    {
        MainViewModel vm = NewViewModel();
        device = new FakeWcpDevice(FakeWcpDevice.Car()) { Stock = stock };

        vm.ConnectObd2WicanPro(device, "COM8", TimeSpan.FromMilliseconds(300));
        return vm;
    }

    private static T Get<T>(object reply, string name) =>
        (T)reply.GetType().GetProperty(name)!.GetValue(reply)!;

    // ----- connecting --------------------------------------------------------

    [Fact]
    public void TheCustomFirmwareIsFoundAndObd2GoesOnWorking()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);

        Assert.True(vm.IsObd2Live);
        Assert.True(vm.IsWicanLive);
        Assert.Equal("v4.50p+wcp1", vm.Wican!.Identity!.Firmware);
        Assert.Contains("RPM", vm.Dashboard.Select(g => g.Title));
        Assert.Contains("custom WCP firmware", vm.Hint, StringComparison.Ordinal);

        // The session is polling the car on its own thread; WCP works beside it.
        int elmBefore = device.ElmLines.Count;
        for (int i = 0; i < 5; i++) Assert.Equal("12.6", vm.Wican.Client.Status()["volt"]);

        DateTime until = DateTime.UtcNow.AddSeconds(3);
        while (device.ElmLines.Count <= elmBefore + 2 && DateTime.UtcNow < until) Thread.Sleep(20);

        Assert.True(device.ElmLines.Count > elmBefore + 2, "The OBD2 session stopped polling.");
    }

    [Fact]
    public void AStockUnitIsJustObd2()
    {
        MainViewModel vm = Connected(out _, stock: true);

        Assert.True(vm.IsObd2Live);
        Assert.False(vm.IsWicanLive);
        Assert.Null(vm.Wican);
    }

    [Fact]
    public void DisconnectingDisarms()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        vm.Wican!.Client.CanStart();
        vm.WicanCanArm();
        Assert.True(device.Armed);

        vm.Disconnect();

        Assert.False(device.Armed);
        Assert.Null(vm.Wican);
    }

    // ----- the gates ----------------------------------------------------------

    [Fact]
    public void SavingSettingsListsTheChangesAndWritesNothingOnNo()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        vm.Wican!.Client.ConfigSet("sta_ssid", "Garage");
        vm.Wican.Client.ConfigSet("sta_pass", "hunter22");

        _confirm.Answer = false;
        WicanOutcome no = vm.WicanConfigSave();

        Assert.True(no.Declined);
        Assert.Equal("", device.Saved["sta_ssid"]);

        WriteRequest asked = _confirm.Last!;
        Assert.Equal(WriteKind.DeviceSettingsSave, asked.Kind);
        Assert.True(asked.Permanent);
        Assert.Contains("Save 2 changed settings", asked.Question, StringComparison.Ordinal);
        Assert.Contains("sta_ssid: (empty) → Garage", asked.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter22", asked.Detail, StringComparison.Ordinal);

        _confirm.Answer = true;
        Assert.True(vm.WicanConfigSave().Done);
        Assert.Equal("Garage", device.Saved["sta_ssid"]);
    }

    [Fact]
    public void NothingStagedIsNothingToAskAbout()
    {
        MainViewModel vm = Connected(out _);

        Assert.False(vm.WicanConfigSave().Done);
        Assert.Empty(_confirm.Asked);
    }

    [Fact]
    public void EnablingLuaTransmitIsCalledOutInTheSaveQuestion()
    {
        MainViewModel vm = Connected(out _);
        vm.Wican!.Client.ConfigSet("lua_can_tx", "enable");

        vm.WicanConfigSave();

        Assert.Contains("ENABLES CAN transmit from Lua", _confirm.Last!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ArmingIsAskedOnceAndSendsAreNotAskedAgain()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        vm.Wican!.Client.CanStart();

        Assert.True(vm.WicanCanArm().Done);

        WriteRequest asked = Assert.Single(_confirm.Asked);
        Assert.Equal(WriteKind.CanArm, asked.Kind);
        Assert.Contains("any identifier", asked.Question, StringComparison.Ordinal);
        Assert.Contains("Do not arm on a vehicle that is moving", asked.Detail, StringComparison.Ordinal);
        Assert.Contains("10 s", asked.Detail, StringComparison.Ordinal);

        for (int i = 0; i < 3; i++) Assert.True(vm.WicanCanSend(0x7E0, [0x02, 0x01, 0x0C]).Done);

        Assert.Single(_confirm.Asked);
        Assert.Equal(3, device.Transmitted.Count);
    }

    [Fact]
    public void ADeclinedArmLeavesSendsRefused()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        _confirm.Answer = false;

        Assert.True(vm.WicanCanArm().Declined);
        Assert.False(device.Armed);

        WicanOutcome send = vm.WicanCanSend(0x7E0, [1]);
        Assert.False(send.Done);
        Assert.Contains("not armed", send.Message, StringComparison.Ordinal);
        Assert.Empty(device.Transmitted);
    }

    [Fact]
    public void LuaQuestionsNameTheScriptItsSizeAndWhetherItCanTransmit()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);

        _confirm.Answer = false;
        Assert.True(vm.WicanLuaPut("speedo", "print('hi')"u8.ToArray()).Declined);
        Assert.Empty(device.LuaScripts);

        WriteRequest put = _confirm.Last!;
        Assert.Equal(WriteKind.LuaUpload, put.Kind);
        Assert.Contains("'speedo' (11 bytes)", put.Question, StringComparison.Ordinal);
        Assert.Contains("lua_can_tx is disabled", put.Detail, StringComparison.Ordinal);

        _confirm.Answer = true;
        Assert.True(vm.WicanLuaPut("speedo", "print('hi')"u8.ToArray()).Done);

        device.Saved["lua_can_tx"] = "enable";

        _confirm.Answer = false;
        Assert.True(vm.WicanLuaRun("speedo").Declined);
        Assert.False(device.LuaStates.ContainsKey("speedo"));
        Assert.Equal(WriteKind.LuaRun, _confirm.Last!.Kind);
        Assert.Contains("'speedo' (11 bytes)", _confirm.Last.Question, StringComparison.Ordinal);
        Assert.Contains("lua_can_tx is ENABLED", _confirm.Last.Detail, StringComparison.Ordinal);

        Assert.True(vm.WicanLuaEval("print(\"x\")").Declined);
        Assert.Contains("print(\"x\")", _confirm.Last.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("lua eval print", device.Commands);

        _confirm.Answer = true;
        Assert.True(vm.WicanLuaRun("speedo").Done);
        Assert.Equal(["x"], vm.WicanLuaEval("print(\"x\")").Output);
    }

    [Fact]
    public void RunningAScriptThatIsNotThereAsksNothing()
    {
        MainViewModel vm = Connected(out _);

        Assert.False(vm.WicanLuaRun("ghost").Done);
        Assert.Empty(_confirm.Asked);
    }

    // ----- the MCP tools --------------------------------------------------------

    [Fact]
    public async Task EveryToolRefusesWithoutACustomWican()
    {
        MainViewModel vm = Connected(out _, stock: true);

        object[] replies =
        [
            await WicanTools.WicanStatus(vm, _ui),
            await WicanTools.WicanConfigSave(vm, _ui),
            await WicanTools.WicanCanArm(vm, _ui),
            await WicanTools.WicanCanSend("7E0", "01", vm, _ui),
            await WicanTools.WicanLuaPut("a", "x", vm, _ui),
        ];

        Assert.All(replies, r => Assert.False(Get<bool>(r, "ok")));
        Assert.Empty(_confirm.Asked);
    }

    [Fact]
    public async Task TheReadToolsAnswer()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        device.LogFiles["drive.db"] = [1, 2, 3, 4, 5];

        Assert.True(Get<bool>(await WicanTools.WicanStatus(vm, _ui), "ok"));
        Assert.Equal("****", Get<Dictionary<string, string>>(await WicanTools.WicanConfigGet(vm, _ui), "settings")["ap_pass"]);
        Assert.True(Get<bool>(await WicanTools.WicanConfigSet("port", "35001", vm, _ui), "ok"));
        Assert.Single(Get<object[]>(await WicanTools.WicanConfigDiff(vm, _ui), "changes"));
        Assert.Contains("12.6V", Get<IReadOnlyList<string>>(await WicanTools.WicanConsole("system --voltage", vm, _ui), "output"));
        Assert.Equal("812", Get<string>(await WicanTools.WicanAutopid(vm, _ui, "ENGINE_RPM"), "value"));

        string target = Path.Combine(_temp[0], "drive.db");
        object read = await WicanTools.WicanLogRead("drive.db", vm, _ui, target);
        Assert.Equal(5L, Get<long>(read, "bytes"));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(target));

        object bad = await WicanTools.WicanAutopid(vm, _ui, "NOPE");
        Assert.False(Get<bool>(bad, "ok"));
        Assert.Equal("NOTFOUND", Get<string>(bad, "code"));
    }

    [Fact]
    public async Task AStreamToolRecordsAndLeavesAByteLogToOpen()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        string csv = Path.Combine(_temp[0], "can.csv");

        object started = await WicanTools.WicanCanStream("start", vm, _ui, csv, id: "2C0", mask: "7F0");
        Assert.True(Get<bool>(started, "ok"));

        for (int i = 0; i < 30; i++)
        {
            device.BusFrame(0x2C4, false, (byte)i, 0x55);
            device.BusFrame(0x400, false, 9);

            // Spread over half a second, so the byte log has rows to plot.
            Thread.Sleep(15);
        }

        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (vm.Wican!.Capture!.FrameCount < 30 && DateTime.UtcNow < until) Thread.Sleep(10);

        object stopped = await WicanTools.WicanCanStream("stop", vm, _ui);

        Assert.Equal(30L, Get<long>(stopped, "frames"));
        Assert.Equal(1, Get<int>(stopped, "identifiers"));

        string byteLog = Get<string>(stopped, "byteLog");
        Assert.True(File.Exists(byteLog));

        // Opened by the same reader the viewer's File > Open uses.
        LogDocument opened = LogReaderFactory.Load(byteLog);
        Assert.Contains(opened.Channels, c => c.Name == "2C4 b0");
    }

    [Fact]
    public async Task TheArmToolAsksAndTheSendToolDoesNot()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        vm.Wican!.Client.CanStart();

        object refused = await WicanTools.WicanCanSend("7E0", "0201", vm, _ui);
        Assert.False(Get<bool>(refused, "ok"));
        Assert.Empty(_confirm.Asked);

        _confirm.Answer = false;
        object no = await WicanTools.WicanCanArm(vm, _ui);
        Assert.True(Get<bool>(no, "declined"));

        _confirm.Answer = true;
        Assert.True(Get<bool>(await WicanTools.WicanCanArm(vm, _ui), "ok"));

        Assert.True(Get<bool>(await WicanTools.WicanCanSend("7E0", "02010C", vm, _ui), "ok"));
        Assert.True(Get<bool>(await WicanTools.WicanCanSend("18DAF110", "01", vm, _ui, extended: true), "ok"));
        Assert.Equal(2, _confirm.Asked.Count);
        Assert.Equal(2, device.Transmitted.Count);

        Assert.True(Get<bool>(await WicanTools.WicanCanDisarm(vm, _ui), "ok"));
        Assert.False(device.Armed);
    }

    [Fact]
    public async Task TheLuaToolsGoThroughTheDialog()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);

        Assert.True(Get<bool>(await WicanTools.WicanLuaPut("speedo", "print('a')", vm, _ui), "ok"));
        Assert.True(Get<bool>(await WicanTools.WicanLuaRun("speedo", vm, _ui), "ok"));
        Assert.True(Get<bool>(await WicanTools.WicanLuaEval("print(\"a\")", vm, _ui), "ok"));
        Assert.Equal(
            [WriteKind.LuaUpload, WriteKind.LuaRun, WriteKind.LuaRun],
            _confirm.Asked.Select(a => a.Kind));

        Assert.Equal("print('a')", Get<string>(await WicanTools.WicanLuaGet("speedo", vm, _ui), "source"));
        Assert.True(Get<bool>(await WicanTools.WicanLuaStatus(vm, _ui), "ok"));
        Assert.True(Get<bool>(await WicanTools.WicanLuaList(vm, _ui), "ok"));

        // Stopping never asks.
        Assert.True(Get<bool>(await WicanTools.WicanLuaStop(vm, _ui), "ok"));
        Assert.Equal(3, _confirm.Asked.Count);
        Assert.Equal("stopped", device.LuaStates["speedo"]);
    }

    // ----- after the review fixes of 2026-11-22 ---------------------------------

    [Fact]
    public async Task AFaultIsShownAndResumingItAsksFirst()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        vm.Wican!.Client.CanStart();
        Assert.True(vm.WicanCanArm().Done);

        device.FailNextSend = true;
        Assert.False(Get<bool>(await WicanTools.WicanCanSend("7E0", "01", vm, _ui), "ok"));

        DateTime until = DateTime.UtcNow.AddSeconds(3);
        while (vm.Wican.Client.TransmitFault is null && DateTime.UtcNow < until) Thread.Sleep(10);

        object status = await WicanTools.WicanCanStatus(vm, _ui);
        Assert.Equal("tx-timeout", Get<string>(status, "transmitFault"));
        Assert.Contains("TRANSMIT FAULT", Get<string>(status, "warning"), StringComparison.Ordinal);
        Assert.Equal("fault tx-timeout", Get<string>(status, "lastDisarm"));

        // Arming is refused here while latched, without a question.
        int asked = _confirm.Asked.Count;
        WicanOutcome arm = vm.WicanCanArm();
        Assert.False(arm.Done);
        Assert.Contains("wican_can_resume", arm.Message, StringComparison.Ordinal);
        Assert.Equal(asked, _confirm.Asked.Count);

        _confirm.Answer = false;
        Assert.True(Get<bool>(await WicanTools.WicanCanResume(vm, _ui), "declined"));
        Assert.Equal("tx-timeout", device.TxFault);
        Assert.Equal(WriteKind.CanResume, _confirm.Last!.Kind);
        Assert.Contains("tx-timeout", _confirm.Last.Detail, StringComparison.Ordinal);

        _confirm.Answer = true;
        Assert.True(Get<bool>(await WicanTools.WicanCanResume(vm, _ui), "ok"));
        Assert.Null(device.TxFault);
        Assert.Null(vm.Wican.Client.TransmitFault);
        Assert.False(device.Armed);

        Assert.True(vm.WicanCanArm().Done);
    }

    [Fact]
    public void ArmingOnASilentBusSaysWhy()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        device.BusChatter = false;
        vm.Wican!.Client.CanStart();

        WicanOutcome arm = vm.WicanCanArm();

        Assert.False(arm.Done);
        Assert.Contains("no CAN frames", arm.Message, StringComparison.Ordinal);
        Assert.False(device.Armed);
    }

    [Fact]
    public async Task LuaStatusShowsStackFree()
    {
        MainViewModel vm = Connected(out FakeWcpDevice device);
        device.LuaScripts["a"] = "x"u8.ToArray();
        vm.Wican!.Client.LuaRun("a");

        object status = await WicanTools.WicanLuaStatus(vm, _ui);
        object script = ((System.Collections.IEnumerable)Get<object>(status, "scripts")).Cast<object>().Single();

        Assert.Equal(31744L, Get<long?>(script, "stackFree"));
    }

    [Fact]
    public void AUnitThatWasRestartingIsTakenOverWhenItSaysBoot()
    {
        MainViewModel vm = NewViewModel();
        var device = new FakeWcpDevice(FakeWcpDevice.Car()) { Silent = true };
        vm.ConnectObd2WicanPro(device, "COM8", TimeSpan.FromMilliseconds(200));

        Assert.True(vm.IsObd2Live);
        Assert.False(vm.IsWicanLive);

        device.Silent = false;
        device.AnnounceBoot();

        DateTime until = DateTime.UtcNow.AddSeconds(3);
        while (!vm.IsWicanLive && DateTime.UtcNow < until) Thread.Sleep(20);

        Assert.True(vm.IsWicanLive);
        Assert.Equal("v4.50p+wcp1", vm.Wican!.Identity!.Firmware);
        Assert.Equal("COM8", vm.Wican.Port);
    }
}
