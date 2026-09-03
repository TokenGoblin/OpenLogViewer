# Embedding an MCP server in a desktop app

*A portable recipe, written for an AI agent asked to give another application the same
level of MCP connectivity OpenEprom has. The concrete code is C#/WPF because that is what
this app is; the design rules are the part that transfers, and each one says why it
exists so you can re-derive it in another stack rather than cargo-cult it.*

The reference implementation lives in [`src/OpenEprom.App/Mcp/`](../src/OpenEprom.App/Mcp/)
and is documented for the operator in [`docs/mcp-server.md`](mcp-server.md). Read this
file for *how to build one*; read that one for *what a finished one looks like from the
outside*.

---

## 1. What "this level" actually means

Not "the app has some tools." Seven properties, and every one of them is a decision you
have to make deliberately — the default of every MCP quickstart gets several of them
wrong for a desktop app:

1. **The agent acts on the live application state**, not a second, disconnected copy of
   it. A tool call reads and mutates the exact objects the window is bound to, and its
   effects appear on screen.
2. **The server is off by default and never remembers being on.** One toggle arms it;
   every launch starts disarmed.
3. **Loopback only.** Never a wildcard bind, armed or not.
4. **The UI says, live, whether an agent is connected** — separately from whether the
   listener is up.
5. **Tool calls marshal onto the UI thread.** Reads included.
6. **Existing safety gates are not bypassed, weakened, or duplicated.** Tools call the
   same commands the buttons call.
7. **Anything a human must attest to is never exposed as a tool.** An agent cannot
   confirm a physical fact about the world.

A build that has tools but not 2–7 is a demo. The rest of this document is how to get
2–7.

---

## 2. Architecture

```
+- Application process ----------------------------------------------+
|                                                                    |
|  Main host (starts at launch)          MCP host (starts on arm)    |
|  +--------------------------+          +------------------------+  |
|  | DI container             |          | WebApplication         |  |
|  |  - Session               |----------+-> DI container         |  |
|  |  - ViewModels            | forward  |   (same instances,     |  |
|  |  - Domain services       |   by     |    registered by value)|  |
|  |  - IUiDispatcher         |  value   |  - activity middleware |  |
|  +--------------------------+          |  - MapMcp()            |  |
|            ^                           |  - tool classes        |  |
|            |  every tool call          +------------------------+  |
|            +-- marshals here ------------------+       |           |
|      UI thread                                         |           |
+--------------------------------------------------------+-----------+
                                                         |
                                   http://127.0.0.1:PORT | MCP client
```

### Why a *second*, independently-lifecycled host

The tempting move is to add MCP to the app's existing host. Don't, for two reasons that
apply to any framework:

- **The listen configuration is fixed at build time.** In ASP.NET Core, Kestrel's URLs
  are set when the `WebApplication` is built; you cannot re-point or add a listener to a
  running one. Most HTTP stacks are the same.
- **The app's own host starts unconditionally at launch.** That is incompatible with
  "off by default, one toggle to arm it."

So: build a fresh server object on every arm, tear it down on every disarm. This also
avoids depending on any undocumented "restart a stopped listener" behaviour.

```csharp
public interface IMcpServerHost : IAsyncDisposable
{
    bool IsArmed { get; }
    int? Port { get; }
    bool HasActiveClient { get; }
    Task ArmAsync(IServiceProvider appServices, int port);
    Task DisarmAsync();
}
```

Put it behind an interface so the toggle's logic can be unit-tested without binding a
real port. ([`McpServerHost.cs`](../src/OpenEprom.App/Mcp/McpServerHost.cs))

### Forwarding live state, by value

This is the single most important line of the whole design:

```csharp
builder.Services.AddSingleton(appServices.GetRequiredService<EpromSession>());
```

Register the **instance**, not the type. `AddSingleton<EpromSession>()` in the MCP
container would construct a *second* session — the tools would then read and edit an
object the window has never heard of, every call would appear to succeed, and nothing
would ever show up on screen. This failure is silent and it is the one that wastes a day.

Forward every service any tool method takes as a parameter:

```csharp
builder.Services.AddSingleton(appServices.GetRequiredService<EpromSession>());
builder.Services.AddSingleton(appServices.GetRequiredService<BurnViewModel>());
builder.Services.AddSingleton(appServices.GetRequiredService<TuneViewModel>());
// ... every view model, every domain service, and the dispatcher
builder.Services.AddSingleton(appServices.GetRequiredService<IUiDispatcher>());
```

**Nothing checks this list against what the tool classes actually need.** A missing
registration compiles fine, arms fine, and fails at *call* time with a DI resolution
error. Section 8 has the test that catches it; write that test before you need it.

### Thread marshaling

Tool methods run on the web server's thread-pool threads. UI object graphs — WPF's
`ObservableObject`, Qt widgets, anything with change notification that a binding layer
listens to — assume mutation happens on the UI thread. So route every tool call through a
dispatcher abstraction:

```csharp
public interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> action);
    Task InvokeAsync(Action action);
}
```

**Reads are not exempt.** A cheap read does not earn an exception to the rule: without
marshaling, a tool call can read an object mid-mutation from the UI thread and return a
torn view of it. Every tool in this app, including `get_undo_state`, goes through the
dispatcher.

Capture the dispatcher *synchronously on the UI thread* at composition time, not in a
lazy factory that might run elsewhere later:

```csharp
services.AddSingleton(Dispatcher.CurrentDispatcher);   // ConfigureServices runs on the UI thread
services.AddSingleton<IUiDispatcher, WpfDispatcher>();
```

One WPF detail worth copying: await `DispatcherOperation.Task`, not the operation itself,
so an exception thrown inside the delegate reaches the caller as itself rather than
wrapped in a `DispatcherOperationException`.

**Non-.NET equivalents:** Qt — `QMetaObject.invokeMethod` with `QueuedConnection`, or a
signal into the GUI thread. Electron — the tools live in the main process and reach the
renderer over IPC. Node with a web UI — a single-threaded event loop makes the marshaling
trivial, but the *atomicity* rule below still applies. Swing/JavaFX — `invokeAndWait` /
`Platform.runLater`.

---

## 3. Building it (.NET / WPF)

**Packages** — `ModelContextProtocol.AspNetCore` (the HTTP transport; it brings
`ModelContextProtocol` with it). To use `WebApplication` from a non-Web SDK project such
as WPF, add the framework reference:

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
<ItemGroup>
  <PackageReference Include="ModelContextProtocol.AspNetCore" />
</ItemGroup>
```

That is Microsoft's documented pattern for embedding ASP.NET Core in a desktop app rather
than the other way round. (If you also reference `Microsoft.Extensions.Hosting`
explicitly, expect NU1510 — the shared framework already carries it.)

**The arm path**, in full:

```csharp
public async Task ArmAsync(IServiceProvider appServices, int port)
{
    await _lifecycleLock.WaitAsync().ConfigureAwait(false);   // see "Serialize the lifecycle"
    try
    {
        if (_app is not null) return;                          // idempotent

        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();                      // one logging pipeline, not two
        builder.Host.UseSerilog(Log.Logger);

        // Forward, not rebuild.
        builder.Services.AddSingleton(appServices.GetRequiredService<EpromSession>());
        // ... the rest of the list

        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

        // Loopback only, never a wildcard.
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();

        // Ahead of MapMcp, so it wraps the long-lived event stream too.
        app.Use(async (context, next) =>
        {
            using (_activity.BeginRequest()) await next(context).ConfigureAwait(false);
        });

        app.MapMcp();

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            await app.DisposeAsync().ConfigureAwait(false);    // don't leave a half-started app
            throw;                                             // usually: port already in use
        }

        _app = app;
        Port = port;
    }
    finally { _lifecycleLock.Release(); }
}
```

**Serialize the lifecycle.** Without the semaphore, a disarm issued while an arm is still
awaiting `StartAsync` sees `_app` still null, no-ops, and the in-flight arm then completes
anyway — a listener left running past the point the caller believed it was stopped. Any
stack with an async start needs the same guard.

**Disarm** clears `_app` and `Port` *before* awaiting the stop, so `IsArmed` is false the
instant disarm begins, then `StopAsync(5s)` and `DisposeAsync()`.

**Disarm on shutdown unconditionally**, whether or not the operator remembered:

```csharp
protected override async void OnExit(ExitEventArgs e)
{
    await _host.Services.GetRequiredService<McpSettingsViewModel>().ShutdownAsync();
    // ... then stop the app's own host
}
```

### Porting the host

| Stack | Server | Transport |
|---|---|---|
| .NET desktop | second `WebApplication` per arm | `ModelContextProtocol.AspNetCore` |
| Electron / Node | `http.Server` in the **main** process, `listen(port, '127.0.0.1')` | `@modelcontextprotocol/sdk` streamable-HTTP transport |
| Python / Qt / Tk | `uvicorn.Server` on a background thread, started and stopped per arm | the `mcp` SDK's streamable-HTTP app |
| JVM desktop | embedded Jetty/Javalin bound to loopback | the Java/Kotlin MCP SDK |

The shape is identical everywhere: *a server object created on arm, holding references to
already-live application objects, bound to loopback, disposed on disarm.*

---

## 4. Writing tools

```csharp
[McpServerToolType]
public static class ImageTools
{
    [McpServerTool, Description(
        "Summary of the currently loaded EPROM image: name, size, CRC32, SHA-256, " +
        "whether it has unsaved edits, and its dump-library record id if it has one.")]
    public static Task<object> GetImageSummary(EpromSession session, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (session.Current is not { } image) return new { loaded = false };

            return new
            {
                loaded = true,
                name = image.SourceName,
                length = image.Length,
                crc32 = $"{image.Crc32:X8}",
                isModified = session.IsModified,
            };
        });
}
```

Points that generalise:

- **Static classes, static methods, DI parameters.** The SDK injects any parameter it
  does not recognise as a tool argument from the server's container. Parameters carrying
  `[Description]` become the tool's JSON-schema arguments; the rest are resolved as
  services. Keep service parameters last for readability.
- **Tool names are derived, not written.** `GetImageSummary` becomes
  `get_image_summary`. Don't hand-write names unless you need one that doesn't follow.
- **`Description` is the agent's only manual.** Say what the tool does, what must have
  happened first, what it refuses, and which other tool clears that refusal — by name.
  `read_chip`'s description names `save_image` and `revert_to_original`, because an agent
  that hits the refusal should not have to guess its way out.
- **Return anonymous objects, serialized to JSON.** Every response carries a boolean
  outcome field (`loaded`, `opened`, `read`, `selected`) so the agent can branch without
  parsing prose.
- **Group tools by domain into separate files.** This app has eight: image, tuning, edit,
  file, library, compare, annotation, hardware.

### Structured refusals, never exceptions

Every foreseeable "no" returns a normal result with a `reason`:

```csharp
if (session.Current is null)
    return new { opened = false, reason = "No image is loaded." };

if (tune.Definition is null)
    return new { opened = false, reason = "No tuning definition is loaded." };

if (tableDefinition is null)
    return new { opened = false, reason = $"No table with id '{tableId}' in the loaded definition." };
```

A thrown exception becomes an opaque protocol error. A `reason` string is something the
agent can *act on* — retry differently, or tell the operator exactly which control to
touch in the app. Preconditions the UI must satisfy (a definition open on a tab, a
baseline selected for comparison) are the main case: those are not errors, they are
"not yet."

Share refusal strings that appear at more than one call site, so the wording and the
underlying check cannot quietly drift apart:

```csharp
internal const string UnsavedEditsRefusal =
    "The working image has unsaved edits. Call save_image or revert_to_original first.";
```

### Never let a tool reach a modal dialog

A dialog raised by a tool call blocks that call on a human who may not be watching, and
holds the UI thread — which every other tool call is queued behind. Find every path from
a tool to a confirmation prompt and pre-empt it with a refusal:

```csharp
if (session.IsModified) return new { opened = false, reason = UnsavedEditsRefusal };
var opened = burn.LoadImage(path);      // now provably cannot prompt
```

The exception is a dialog that *is* the safety mechanism (Section 6) — that one is
supposed to block.

### Check-and-start atomically

When a tool starts a long-running command, decide whether it may run and start it in the
*same* dispatcher turn, so nothing else queued on the UI thread — another MCP call, a
button click — can interleave between the check and the start:

```csharp
var (reason, running) = await dispatcher.InvokeAsync(() =>
{
    if (burn.IsBusy)
        return ("Another transfer is already in progress. Try again once it finishes.", (Task?)null);

    var extra = refuse?.Invoke();                        // per-tool extra guard
    return extra is not null ? (extra, (Task?)null)
                             : ((string?)null, command.ExecuteAsync(null));
});

if (running is not null) await running.ConfigureAwait(false);
return reason;      // null == it ran to completion
```

Note the shape: **decide and start on the dispatcher, await from the pool.** Blocking the
dispatcher thread until an async command with UI-thread continuations finishes would
deadlock it against itself. This gets all three properties — atomic decision, the
command's own property changes still on the UI thread, and the tool call still not
returning until the work has actually finished.

The busy check is not paranoia. If the underlying command silently no-ops when one is
already running, the tool would otherwise report the *previous* run's leftover status as
this call's result.

### Make agent actions visible

`open_table` opens the table on the app's own Tune tab as well as returning its cells.
Nothing an agent inspects or changes happens invisibly. This costs one line per tool and
is what makes an armed server watchable rather than spooky.

### Cap what a tool can return

```csharp
[Description("Number of bytes to read. Capped at 4096 per call.")] int length
```

And bounds-check without overflowing:

```csharp
// offset > image.Length - length, NOT offset + length > image.Length:
// the latter wraps for an offset near int.MaxValue and silently passes.
if (offset < 0 || length < 0 || length > 4096 || offset > image.Length - length)
```

---

## 5. Arming, and the connection indicator

The toggle view model ([`McpSettingsViewModel.cs`](../src/OpenEprom.App/ViewModels/McpSettingsViewModel.cs))
holds `IsArmed`, `IsClientConnected`, `Port`, a `StatusLine`, and one `Toggle` command.

**Off by default, never persisted.** No setting to make it start armed. The reasoning: an
armed server accepts calls from anything able to reach that port on the machine, and
silently resuming that after an unrelated restart is a bigger surprise than one extra
click per session is worth avoiding.

**Armed and connected are different facts**, and the UI must say both. A listener being up
is worth seeing on its own; announcing "an AI is connected" when none is would make the
indicator useless.

```csharp
public string TitleBarText => (IsArmed, IsClientConnected) switch
{
    (false, _)    => "AI agent access: OFF",
    (true, true)  => $"AI CONNECTED OVER MCP · 127.0.0.1:{Port}",
    (true, false) => $"AI agent access ON, waiting · 127.0.0.1:{Port}",
};
```

Write the labels in operator language, not internal vocabulary. "Armed" is precise and
means nothing to someone who has not read the source.

**Detecting "connected" needs two signals**, because MCP clients differ:

```csharp
internal sealed class McpClientActivity
{
    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(45);

    private int _inFlight;
    private long _lastRequestTicks;

    public bool IsActive
    {
        get
        {
            if (Volatile.Read(ref _inFlight) > 0) return true;           // streaming client
            var ticks = Interlocked.Read(ref _lastRequestTicks);          // polling client
            return ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < IdleWindow;
        }
    }

    public IDisposable BeginRequest() { Interlocked.Increment(ref _inFlight); return new Scope(this); }
}
```

A streaming client holds a request open the whole time it is attached, so the in-flight
count answers for it. A client that posts a call and hangs up is attached in every sense
that matters but in flight for milliseconds, so recent traffic counts too. Keep the idle
window short — a stale "connected" light is worse than a brief gap.

Poll it (1 s is well below noticeable, and the work is a field read) rather than pushing:
the signal expires on a timer anyway, so something has to re-ask regardless.

Handle the arm failure the operator will actually hit:

```csharp
catch (IOException ex)
{
    StatusLine = $"Could not open AI agent access on port {Port}: {ex.Message}";  // usually: port in use
}
```

---

## 6. The safety boundary

This is the part that separates a responsible integration from a liability, and it is
three rules.

### Rule 1 — Tools call the same commands the buttons call

```csharp
public static async Task<object> Burn(BurnViewModel burn, IUiDispatcher dispatcher)
{
    var refusal = await RunCommandAsync(dispatcher, burn, burn.BurnCommand);
    ...
}
```

Not a re-implementation, not a "fast path", not a copy of the logic with the checks
removed. `burn.BurnCommand` is the identical object the Burn button is bound to, so the
pre-flight checklist and the confirmation dialog it gates run in full — which means an
MCP-triggered burn genuinely waits for a human to answer that dialog in the running app
before the tool call returns. That is correct behaviour, not a bug to work around.

The moment you find yourself writing a second code path "because the dialog is awkward
over MCP," you have started building the thing this rule exists to prevent.

### Rule 2 — Some controls are never exposed, by design

This app has three checkboxes the operator must tick before a burn: chip type,
orientation, adapter seating — the ones the UI itself labels *"nothing in software can see
the socket, these are yours to confirm."*

**No tool sets them. There will never be one.** An agent cannot attest to a physical
inspection it cannot perform. The direct consequence is that an MCP-triggered burn will
always require a human to have ticked them first, permanently.

Find your app's equivalent — the attestations, the irreversible-destination confirmations,
the "I have checked X in the real world" gates — and put them on this list explicitly, in
a comment, before you write the first tool. It is much harder to remove a tool later than
to never add it.

### Rule 3 — Tell the agent whose move it is

A refusal an agent cannot distinguish from one it could clear produces either a retry loop
or premature surrender. So `get_preflight_status` splits its findings:

- **`needsAcknowledgement`** — things an agent may still be able to resolve. "Blank check
  not performed" is cleared by *running one*, not by a tick.
- **`remainingForOperator`** — only what genuinely requires a person: a physical
  inspection, or a confirmation re-demanded at commit.
- **`handoff`** — one sentence saying whose move it is.

Deliberately *not* "every unacknowledged warning is the operator's problem" — most of
those name conditions an agent can go and resolve, and listing them as human-only tells it
to give up on work it could still do.

Also worth stating in the tool docs, and worth checking is still true of your build:

- **Append-only stores stay append-only.** No tool overwrites or deletes a record.
- **In-memory edits stay in memory** until an explicit save/archive tool, exactly as from
  the UI.
- **Writes into "free" space are validated against a real definition of free**, and an
  explicit-offset argument that is not inside a certified region is refused. A raw-poke
  tool may exist, but it should be *named* like one.

---

## 7. Client configuration

The app must already be running and armed; there is nothing to launch on the agent's
behalf.

```sh
claude mcp add --transport http openeprom http://127.0.0.1:7071/
```

```json
{ "mcpServers": { "openeprom": { "url": "http://127.0.0.1:7071/" } } }
```

If a client reports `ConnectionRefused`, the app is not running or not armed — that is the
design working, not a misconfiguration.

---

## 8. Testing

Unit-testing the logic each tool wraps is necessary and not sufficient. Two harness pieces
make end-to-end testing cheap:

```csharp
// Runs inline instead of marshaling to a real dispatcher.
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
```

...plus a `FakeMcpServerHost` so the toggle view model's logic is testable without binding
a port. (Test the *real* dispatcher separately, once, on its own.)

Then stand up the same shape of DI graph the composition root builds, with fakes for
hardware and dialogs, arm on a fixed loopback port, and drive it with the **real MCP client
SDK**:

```csharp
private static Task<McpClient> ConnectAsync() =>
    McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri($"http://127.0.0.1:{Port}/"),
    }));
```

Four tests earn their keep:

1. **Arming starts a listener**, disarming stops it.
2. **A real client can list the tools** — proves registration and transport.
3. **A tool call reflects the same live state the harness holds.** Assert against the
   exact `EpromSession` instance the test created. This is the proof it is not a
   disconnected second copy — the silent failure from Section 2.
4. **Every registered tool resolves its dependencies**, as a table-driven test with one
   row per tool:

```csharp
[Theory]
[InlineData("get_image_summary", null)]
[InlineData("read_bytes", """{"offset":0,"length":1}""")]
[InlineData("open_table", """{"tableId":"none"}""")]
[InlineData("burn", null)]
// ... every single tool
public async Task Every_registered_tool_resolves_its_dependencies_without_error(
    string toolName, string? argumentsJson)
{
    var result = await client.CallToolAsync(toolName, arguments, ...);
    Assert.True(result.IsError is not true, $"{toolName} failed: ...");
}
```

**This is the test that pays for the whole file.** A service a tool needs but the
forwarding list omits fails at *call* time — not at compile time, not at arm time. Exactly
that happened here to `get_checksum_status` and its `ChecksumEngine`. Calling every tool
once, even against a minimal fixture with nothing loaded, catches it: a missing
registration surfaces as a DI failure regardless of what the tool's business logic would
have done. Deliberately-invalid arguments are fine — you are asserting *"did not fail to
resolve"*, and structured refusals (Section 4) are not errors.

One gotcha: `CallToolResult.IsError` is `bool?`, where null means "ran fine, nothing
flagged". `Assert.False(bool?)` treats null as failing too. Assert `IsError is not true`.

---

## 9. Checklist

Ordered so each step is testable before the next.

**Host**
- [ ] Server object with `IsArmed` / `Port` / `HasActiveClient` / `ArmAsync` / `DisarmAsync`, behind an interface
- [ ] Built fresh per arm, disposed per disarm; arm is idempotent
- [ ] Lifecycle lock serializing arm against disarm
- [ ] Bound to `127.0.0.1` — grep the codebase for `0.0.0.0` and `::` and confirm zero hits
- [ ] Logging routed into the app's existing pipeline, not a second one
- [ ] Start failure (port in use) disposes the half-started server and surfaces a readable message
- [ ] Disarmed unconditionally on app exit

**Wiring**
- [ ] Every service the tools need forwarded **by instance**
- [ ] Dispatcher captured synchronously on the UI thread at composition time
- [ ] Activity middleware registered *before* the MCP endpoint mapping

**Tools**
- [ ] Grouped by domain, one file each
- [ ] Every method — reads included — goes through the dispatcher
- [ ] Every response has a boolean outcome field and, on refusal, a `reason`
- [ ] No tool can reach a modal dialog that is not itself the safety gate
- [ ] Check-and-start atomic for anything long-running; busy state refused, not misreported
- [ ] Range/size caps on anything that reads bulk data, with non-overflowing bounds checks
- [ ] Descriptions name the tools that clear each refusal
- [ ] Agent actions surface in the UI

**Safety**
- [ ] Action tools call the same commands the UI does — no second path
- [ ] The never-exposed list is written down, with its reasoning, before the first tool
- [ ] Status tools split "agent can clear this" from "only a person can"
- [ ] Append-only stores still append-only; nothing new can delete

**UI**
- [ ] Off by default, not persisted, no setting to change that
- [ ] One toggle, checkbox-style, with the status line as its tooltip
- [ ] Live indicator distinguishing OFF / armed-waiting / connected
- [ ] Connection detection covers streaming *and* polling clients

**Tests**
- [ ] Immediate dispatcher + fake host for logic tests; real dispatcher tested once on its own
- [ ] Real MCP client over a real socket
- [ ] A test asserting a tool call reflects the harness's own live instance
- [ ] One row per tool proving dependency resolution
