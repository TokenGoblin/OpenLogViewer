using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLogViewer.Oracle;

// olv-oracle [--rust <dir>] [--only <package>] [--synthetic | --corpus]
//
// Every IEmitter in this assembly runs, and each writes one file:
//   rust/oracle/expected/<package>/<set>.json          synthetic inputs, committed
//   rust/oracle/expected/corpus/<package>/<set>.json   real files, gitignored
// To add reference outputs for a package, add a class under Emitters/; nothing
// else needs to change.

string rust = Arg("--rust") ?? FindRust();
string? only = Arg("--only");
bool wantSynthetic = !args.Contains("--corpus");
bool wantCorpus = !args.Contains("--synthetic");

var context = new EmitContext(rust);
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
// No commit hash: the files are committed, and a hash would change them on every
// commit, which would make the drift check in CI fail for nothing.
const string generator = "tools/OpenLogViewer.Oracle";

var emitters = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => typeof(IEmitter).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
    .Select(t => (IEmitter)Activator.CreateInstance(t)!)
    .Where(e => only is null || e.Package == only)
    .OrderBy(e => e.Package).ThenBy(e => e.Set)
    .ToList();

int failures = 0;
foreach (IEmitter e in emitters)
{
    if (e.FromCorpus ? !wantCorpus : !wantSynthetic) continue;
    if (e.FromCorpus && !Directory.Exists(context.Corpus))
    {
        Console.WriteLine($"  skip  {e.Package}/{e.Set}: no corpus (run `cargo xtask corpus`)");
        continue;
    }

    try
    {
        IReadOnlyList<object> cases = e.Emit(context);
        string dir = e.FromCorpus
            ? Path.Combine(rust, "oracle", "expected", "corpus", e.Package)
            : Path.Combine(rust, "oracle", "expected", e.Package);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, e.Set + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { generator, cases }, options) + "\n");
        Console.WriteLine($"  wrote {e.Package}/{e.Set}: {cases.Count} cases");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"  FAIL  {e.Package}/{e.Set}: {ex.GetType().Name}: {ex.Message}");
    }
}
return failures == 0 ? 0 : 1;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRust()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "rust", "packages.toml"))) return Path.Combine(d.FullName, "rust");
    throw new InvalidOperationException("cannot find rust/packages.toml above the tool; pass --rust <dir>");
}
