using System.Globalization;
using System.Text;

// A deliberately small imitation of bedrock_server.exe.
//
// The replies below are copied from the real 1.26.36.1 transcript captured by
// scripts/stdin-spike.ps1 (docs/design.md § 5.5), including the details that are easy
// to get wrong: lines that carry no [timestamp LEVEL] prefix, the save manifest
// arriving on the line after "Data saved", and `save resume` answering
// "Changes to the world are resumed" rather than "level".
//
// "Copied from the real transcript" is a claim about the replies, not about when
// each one is given. It was false in one place for weeks (`T174`): `save resume`
// answered the confirmation whether or not a hold was outstanding, where real BDS
// answers "A previous save has not been completed." if none is. Adding a reply here
// means asking what the real server does when the command makes no sense, because
// that is the half a transcript of the happy path does not record.
//
// Behaviour is steered by an optional fake-server.conf in the working directory
// so a test can ask for a crash, a hang, or a slow save. A file rather than
// environment variables because the child inherits the test host's environment,
// and xUnit runs classes in parallel — env-var steering would make one test's
// settings leak into another's server. Per-working-directory also matches how a
// real instance is configured.

Console.OutputEncoding = new UTF8Encoding(false);

var settings = LoadSettings();
var readyDelay = Setting("ready_delay_ms", 0);
var exitAfter = Setting("exit_after_ms", -1);
var exitCode = Setting("exit_code", 0);
var neverReady = Setting("never_ready", 0) == 1;
var ignoreStop = Setting("ignore_stop", 0) == 1;
var slowQueries = Setting("slow_query", 0);

Emit("NO LOG FILE! - setting up server logging...");
Log("INFO", "Starting Server");
Log("INFO", "Version: 1.26.36.1");
Log("INFO", "Level Name: Bedrock level");

// Unprefixed banner lines: real BDS emits these, and they are the reason the
// parser must treat a missing prefix as content rather than as a parse failure.
Emit("#####################################################");
Emit("#               LOADING VANILLA WORLD               #");
Emit("#####################################################");

if (exitAfter >= 0)
{
    // A crash before readiness, for exercising the watchdog against a real process.
    _ = Task.Run(async () =>
    {
        await Task.Delay(exitAfter);
        Console.Out.Flush();
        Environment.Exit(exitCode);
    });
}

if (readyDelay > 0)
{
    Thread.Sleep(readyDelay);
}

if (!neverReady)
{
    Log("INFO", "Server started.");
}

var queries = 0;

// Whether a `save hold` is outstanding. Real BDS answers `save resume` differently
// depending on this, and answering it unconditionally is what `T174` was.
var holding = false;

while (Console.ReadLine() is { } raw)
{
    var command = raw.Trim();

    switch (command)
    {
        case "list":
            Log("INFO", "There are 2/10 players online:");
            Emit("Steve, Aleš");   // non-ASCII on purpose: proves the UTF-8 round trip
            Emit(string.Empty);    // real BDS emits a genuinely empty line here
            break;

        case "save hold":
            queries = 0;
            holding = true;
            Log("INFO", "Saving...");
            break;

        case "save query":
            if (++queries <= slowQueries)
            {
                Log("INFO", "A previous save has not been completed.");
            }
            else
            {
                Log("INFO", "Data saved. Files are now ready to be copied.");
                // Relative to worlds/ and omitting it, exactly as BDS reports.
                Emit("Bedrock level/db/000005.ldb:509, Bedrock level/db/CURRENT:16, "
                     + "Bedrock level/level.dat:3042");
            }

            break;

        // `T174`. This answered the confirmation unconditionally, which is not what
        // BDS does: with no hold outstanding it replies "A previous save has not
        // been completed." — the same not-ready line `save query` gives. Verified on
        // a real 1.26.45.1 by typing it at an idle server.
        //
        // That gap mattered out of all proportion to its size. A refuter reasoned
        // from this double, found the orchestrator's comment about the real reply
        // unsupported, and concluded a CORRECT comment was unverified prose. A test
        // double that is wrong does not merely fail to catch a defect; it makes
        // right code look wrong.
        case "save resume":
            if (holding)
            {
                holding = false;
                Log("INFO", "Changes to the world are resumed.");
            }
            else
            {
                Log("INFO", "A previous save has not been completed.");
            }

            break;

        case "stop":
            if (ignoreStop)
            {
                Log("INFO", "Server stop requested.");
                break;   // deliberately never exits, so the drain budget can be tested
            }

            Log("INFO", "Server stop requested.");
            Log("INFO", "Stopping server...");
            Emit("Quit correctly");
            Console.Out.Flush();
            return exitCode;

        default:
            if (command.StartsWith("say ", StringComparison.Ordinal))
            {
                Log("INFO", $"[Server] {command[4..]}");
            }
            else if (command.Length > 0)
            {
                Log("ERROR", $"Unknown command: {command}");
            }

            break;
    }
}

return exitCode;

void Emit(string text)
{
    Console.Out.WriteLine(text);
    Console.Out.Flush();
}

void Log(string level, string message) =>
    Emit($"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff", CultureInfo.InvariantCulture)} {level}] {message}");

int Setting(string key, int fallback) =>
    settings.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

static Dictionary<string, string> LoadSettings()
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var path = Path.Combine(Directory.GetCurrentDirectory(), "fake-server.conf");
    if (!File.Exists(path))
    {
        return result;
    }

    foreach (var line in File.ReadAllLines(path))
    {
        var separator = line.IndexOf('=');
        if (separator > 0)
        {
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
    }

    return result;
}
