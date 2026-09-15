using System.Net;
using System.Security.Claims;
using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Security;
using BedrockSentry.Host;
using BedrockSentry.Host.Components;
using BedrockSentry.Host.Middleware;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.DependencyInjection;
using BedrockSentry.Services.Diagnostics;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Security;
using BedrockSentry.Services.Updates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

// Paths come first, before anything else: configuration lives under the data root,
// so the root has to be resolved before there is any configuration to read — which
// is exactly why it cannot be a configuration setting itself.
//
// `--dataroot` is what install.ps1 writes into the service's ImagePath (D36), so an
// install that was told to keep its data off the system volume actually does. It
// takes precedence over BEDROCKSENTRY_DATAROOT and the ProgramData default, both of
// which still apply underneath it in that order.
// The tag that decides which endpoint a check appears on. One spelling, used by
// the registration and by the detailed endpoint's Predicate — two string
// literals would be a rule stated twice, and the failure would be a check that
// silently appears on neither endpoint.
const string DetailedTag = "detailed";

var paths = new SentryPaths(DataRootArgument(args));

// `T143`: registered here rather than wrapped around `app.Run()`, which is where
// this started and where it caught almost nothing. The certificate, the
// `Kestrel:Endpoints` refusal and `TlsOptions` validation all run inside
// `builder.Build()`, about 270 lines below and well before `app.Run()` — so a
// wrong PFX password produced a bare crash with an empty log, which is the exact
// failure `T138` was meant to close.
//
// A handler rather than a `try` for two reasons: it needs only `paths`, which the
// line above has just produced, and it covers every throw from this line onward
// without indenting the whole file into one block. The exception stays unhandled
// either way, so the service control manager still sees a failed start.
//
// `T159`: this used to sit BELOW `paths.EnsureCreated()`, with a comment claiming
// the event-log half still recorded a failure there. It did not — neither half
// did, because the handler that calls `StartupFailure.Record` did not exist yet,
// so an `UnauthorizedAccessException` on the data root unwound with no subscriber
// at all. That is the fourth lesson again: the comment was written from intent and
// the code disagreed, and everything written from the comment inherited the error.
// Both halves work from here — `paths.Logs` is a computed property that needs no
// directory, and `StartupFailure.WriteToFile` creates the directory itself.
//
// What is still not covered is the line above: constructing `SentryPaths` at all.
// The handler closes over `paths`, so it cannot be registered before there is a
// `paths` to close over. Covering that too would need a handler that does not
// depend on it — event-log-only, or a fallback directory — and it is a much
// smaller target: a constructor that parses an argument, with no I/O in it.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception failure)
    {
        StartupFailure.Record(failure, paths.Logs, StartupFailure.Phase);
    }
};

// Now covered by the handler above, which is the whole point of the ordering:
// this is the first thing in the process that touches the disk, and a bad
// `--dataroot` fails right here.
paths.EnsureCreated();

static string? DataRootArgument(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var argument = args[i];

        if (argument.StartsWith("--dataroot=", StringComparison.OrdinalIgnoreCase))
        {
            return NonEmpty(argument["--dataroot=".Length..]);
        }

        if (!argument.Equals("--dataroot", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        return NonEmpty(i + 1 < args.Length ? args[i + 1] : null);
    }

    return null;

    // A registration carrying --dataroot with nothing usable after it is malformed.
    // Falling through to ProgramData would put the data somewhere the operator
    // explicitly did not ask for, and would look like it had worked, so this fails
    // the start instead. It throws before logging exists — see T104 — which is a
    // known gap in its own right rather than something this switch introduces.
    static string NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("--dataroot was given without a path.")
            : value;
}

// Before the configuration is read, so a first run picks up the file it just
// wrote rather than seeing it appear on the next restart.
StarterConfig.EnsureCreated(paths);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,

    // Pinned unconditionally. A Windows Service starts with its working directory
    // set to C:\Windows\System32, and contrary to the documentation AddWindowsService
    // has not corrected the content root since .NET 7. The framework's own fallback
    // only applies when the working directory is an exact match for the system
    // directory, so anything that launches the service from elsewhere gets no
    // correction at all and appsettings.json silently fails to load.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = "BedrockSentry");

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "BedrockSentry";
        settings.LogName = "Application";
    });
}

// Layered so that operational settings can be hot-reloaded while credentials cannot.
// auth.json is deliberately unwatched: a malformed edit to a watched file resets its
// keys to empty, which would read as "never configured" and reopen the unauthenticated
// setup wizard on a live server.
builder.Configuration
    .AddJsonFile(Path.Combine(paths.Config, "bedrocksentry.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(AdminCredentialService.AuthFilePath(paths), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("BEDROCKSENTRY_");

// The space-hungry folders may be configured onto another volume (docs/design.md
// § 4.3). Applied here, before the container exists, so the first thing to
// touch backups or packages already looks in the right place. EnsureCreated ran
// earlier — it has to, the config directory comes from it — so the overridden
// directories are created here instead.
var storageOptions =
    builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
    ?? new StorageOptions();

paths.ApplyStorageOverrides(storageOptions.BackupsRoot, storageOptions.ServerPackagesRoot);
Directory.CreateDirectory(paths.Backups);
Directory.CreateDirectory(paths.ServerPackages);


// Endpoints declared in configuration are cumulative with endpoints declared in code,
// not overriding — so a stray Kestrel section produces a second listener and a port
// conflict at startup. Worse, Kestrel's certificate schema has no thumbprint key and
// ignores one silently, serving a different certificate than the operator configured.
// All TLS selection goes through Sentry:Tls instead.
if (builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
    throw new InvalidOperationException(
        "A 'Kestrel:Endpoints' configuration section was found. BedrockSentry configures its " +
        "endpoints in code, and the two are additive rather than overriding. Move any certificate " +
        $"settings to the '{TlsOptions.SectionName}' section and remove the Kestrel section.");
}

builder.Services.AddOptions<TlsOptions>()
    .Bind(builder.Configuration.GetSection(TlsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Held rather than logged here: the logger does not exist until the app is built, and
// this check has to run before binding. Drained where the other startup warnings are.
var startupWarnings = new List<string>();

// Sanitise the channel BEFORE binding, not after. The binder parses an enum from its
// member name and THROWS on a name no member carries, so a PostConfigure would never
// run — the service would already have failed to start. Every install written before
// 2026-09-15 has "Channel": "Prod" in its config, which is exactly that case.
//
// Setting it back through the configuration indexer is an in-memory override: it does
// not rewrite the operator's file, so the wrong value stays visible to be corrected
// rather than being silently laundered on first boot.
if (!UpdateChannelSetting.Parse(builder.Configuration[UpdateChannelSetting.Key], out var channel))
{
    startupWarnings.Add(
        UpdateChannelSetting.Unrecognised(builder.Configuration[UpdateChannelSetting.Key]));

    builder.Configuration[UpdateChannelSetting.Key] = channel.ToString();
}

builder.Services.AddOptions<UpdateOptions>()
    .Bind(builder.Configuration.GetSection(UpdateOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<AdminCredentialOptions>()
    .Bind(builder.Configuration.GetSection(AdminCredentialOptions.SectionName));

builder.Services.AddOptions<InstanceOptions>()
    .Bind(builder.Configuration.GetSection(InstanceOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<HostDetailsOptions>()
    .Bind(builder.Configuration.GetSection(HostDetailsOptions.SectionName));

builder.Services.AddOptions<BdsUpdateOptions>()
    .Bind(builder.Configuration.GetSection(BdsUpdateOptions.SectionName));

builder.Services.AddOptions<TestingOptions>()
    .Bind(builder.Configuration.GetSection(TestingOptions.SectionName));

// D45. No ValidateOnStart, for the same reason TestingOptions has none: a config
// file that has never heard of this section must mean "off", not "refuse to start".
builder.Services.AddOptions<BackupPolicyOptions>()
    .Bind(builder.Configuration.GetSection(BackupPolicyOptions.SectionName));

builder.Services.AddBedrockSentryServices(paths);

// Scoped, which for Blazor Server means one per circuit. Removing an instance
// navigates off that instance's page — it stops describing anything the moment it is
// gone (`T169`) — and this carries the report of what happened to the Status page it
// lands on. An internal NavigateTo keeps the circuit, so scoped state survives it.
// A singleton would show one administrator's removal to another; a transient would
// hand the page a different object from the one that was posted to.
builder.Services.AddScoped<PendingRemoval>();

// Singleton: the samples are the point, and a per-circuit copy would re-walk
// every world for every browser tab. Its walks run on the thread pool and it
// hands pages the last answer instantly (`D48`).
builder.Services.AddSingleton<InstanceSizeSampler>();

// One reader for the three destructive dialogs, so the rule is stated once.
builder.Services.AddSingleton<DestructiveConfirmPolicy>();

// The DPAPI call is mandatory, not belt-and-braces: PersistKeysToFileSystem turns OFF
// the automatic at-rest encryption that otherwise applies. And the key ring must live
// under the data root — inside the install tree it would be deleted by the next
// self-update, silently invalidating every auth cookie and antiforgery token.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("BedrockSentry")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeys));

if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/denied";

        // Must match [SupplyParameterFromQuery(Name = "returnUrl")] on the login page.
        // A casing mismatch here fails silently by dropping the user on "/" after sign-in.
        options.ReturnUrlParameter = "returnUrl";

        // The unremembered life. A sign-in with Keep me signed in ticked carries its
        // own ExpiresUtc and this number does not apply to it — see SignInLifetime,
        // which is where the other duration lives and says so (D58). Two numbers, one
        // each, and neither file restates the other's.
        options.ExpireTimeSpan = TimeSpan.FromHours(12);

        // Applies to both, and is what makes a remembered session mean "14 days since
        // you last used it": the handler renews a ticket using its own original span
        // rather than ExpireTimeSpan, so a 14-day ticket slides by 14 days and a
        // 12-hour one by 12 hours.
        options.SlidingExpiration = true;
        options.Cookie.Name = "BedrockSentry.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnValidatePrincipal = context =>
        {
            var credentials = context.HttpContext.RequestServices
                .GetRequiredService<IAdminCredentialService>();

            var expected = credentials.Current.SecurityStamp;

            // An unreadable or blank credential file means "configuration is broken",
            // not "the password changed". Signing everyone out on that reading would
            // turn a typo in a hand-edited file into a lockout — and, combined with the
            // first-run gate, into a way to reopen the setup wizard. Keep the session.
            if (string.IsNullOrEmpty(expected))
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("BedrockSentry.Auth")
                    .LogError("The administrator credential could not be read while validating a session.");

                return Task.CompletedTask;
            }

            var presented = context.Principal?.FindFirstValue(BedrockSentryClaims.SecurityStamp);

            if (!string.Equals(presented, expected, StringComparison.Ordinal))
            {
                context.RejectPrincipal();
                return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Two endpoints, one registry of checks, and the split is load-bearing (`T165`).
//
// Every check registered here is tagged `Detailed` and is served ONLY on
// HealthContract.ReadyPath. UpdateOptions.HealthPath selects none of them — see
// the Predicate where it is mapped — so it always answers Healthy, with the two
// fields the updater parses unchanged, which is what its frozen contract requires.
//
// `T178`. This used to claim the response was "byte-identical to what every
// previous build answered". It is not: the third field was `channel` before `T166`
// renamed it to `buildChannel`, in the same change that wrote the claim. Nothing
// broke, because UpdateRunner reads `status` and `version` only — which is exactly
// why the frozen part is those two fields and not the whole body. Saying
// byte-identical overstated the contract and was the stated reason for the
// predicate below, so it was wrong in the one place being relied on. Registering a check that can fail on THAT endpoint would roll back a
// replacement build whenever any server was down, and a server refused for a held
// port (`T151`) is the wrapper working correctly.
builder.Services.AddHealthChecks()
    .AddCheck<InstanceStateHealthCheck>(
        InstanceStateHealthCheck.CheckName, tags: [DetailedTag])
    .AddCheck<DataRootHealthCheck>(
        DataRootHealthCheck.CheckName, tags: [DetailedTag]);

// The shutdown budget, top to bottom (docs/design.md § 5.2):
//
//     updater stop timeout  >  host shutdown timeout  >  warning + instance drain
//
// Every managed server gets warned, saves, and exits inside the drain budget; the
// host waits for that plus slack; the updater's helper waits longer still before it
// force-kills anything. Invert any pair and the outer layer kills a server that is
// legitimately mid-save — which is the one failure this whole path exists to avoid.
// Derived from the configured values rather than hardcoded, so raising the drain
// budget cannot silently push it past the timeout that is supposed to contain it.
var instanceOptions = builder.Configuration
    .GetSection(InstanceOptions.SectionName).Get<InstanceOptions>() ?? new InstanceOptions();

var updateOptionsForShutdown = builder.Configuration
    .GetSection(UpdateOptions.SectionName).Get<UpdateOptions>() ?? new UpdateOptions();

var instanceDrain = instanceOptions.ServiceStopWarning + instanceOptions.ShutdownDrainBudget;
var shutdownTimeout = instanceDrain + TimeSpan.FromSeconds(15);

if (shutdownTimeout >= updateOptionsForShutdown.StopTimeout)
{
    // Failing at startup beats discovering the inversion during an update, when
    // the symptom is a corrupted world rather than a message.
    throw new InvalidOperationException(
        $"The instance drain budget ({instanceDrain.TotalSeconds:0}s plus slack) requires a host "
        + $"shutdown timeout of {shutdownTimeout.TotalSeconds:0}s, which is not below the updater's "
        + $"stop timeout of {updateOptionsForShutdown.StopTimeout.TotalSeconds:0}s. Lower "
        + $"'{InstanceOptions.SectionName}:ShutdownDrainBudget' or raise "
        + $"'{UpdateOptions.SectionName}:StopTimeout'.");
}

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = shutdownTimeout;
});

// Configured through the options system rather than builder.WebHost.ConfigureKestrel so
// the certificate provider and first-run state can be injected.
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<ServerCertificateProvider, IOptions<TlsOptions>, IOptions<UpdateOptions>, SetupState, IConfiguration>(
        (kestrel, certificates, tls, update, setup, configuration) =>
        {
            var certificate = certificates.GetCertificate();
            var allowRemoteSetup = configuration.GetValue("Sentry:AllowRemoteSetup", false);

            void UseHttps(ListenOptions listen) => listen.UseHttps(certificate);

            // Until an administrator account exists, anyone who can reach the port can
            // claim it. Staying on loopback means that requires already being on the box.
            // AllowRemoteSetup exists for headless Server Core installs where loopback is
            // not reachable, and is an explicit opt-in to that exposure.
            if (setup.IsConfigured || allowRemoteSetup)
            {
                kestrel.ListenAnyIP(tls.Value.Port, UseHttps);
            }
            else
            {
                kestrel.ListenLocalhost(tls.Value.Port, UseHttps);
            }

            // Plaintext, loopback-only, and separate from the UI listener on purpose.
            // The updater polls this to decide whether a freshly-installed build came up;
            // probing the HTTPS port instead would mean the helper has to tolerate
            // certificate errors, and a broken certificate is exactly the class of failure
            // it is supposed to catch.
            kestrel.ListenLocalhost(update.Value.HealthPort);
        });

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BedrockSentry.Startup");

// Anything the configuration had to be talked out of before binding. Warning rather
// than Information because the EventLog sink filters at Warning, and a value the
// wrapper silently overrode is exactly the kind nobody discovers otherwise.
foreach (var warning in startupWarnings)
{
    startupLogger.LogWarning("{Warning}", warning);
}

// Loud, at startup, every start. A switch that weakens a confirmation and says
// nothing is one somebody leaves on — the same shape as the licence manifest that
// was missing for months while a document claimed otherwise. The dialogs
// themselves carry a banner too, so it is visible where it applies as well as
// where it is configured.
if (!app.Services.GetRequiredService<DestructiveConfirmPolicy>().PasswordRequired)
{
    startupLogger.LogWarning(
        "{Setting} is on: Remove, Retire and Restore will act on confirmation alone, with no "
        + "password. This is for unattended testing. Turn it off before this install is used "
        + "for anything real.",
        $"{TestingOptions.SectionName}:{nameof(TestingOptions.SkipDestructiveConfirmPassword)}");
}

// D45, and the warning is in the ON direction because that is the weakened state
// here. It doubles as proof the binding above exists: an administrator who runs
// install.ps1 with the switch and sees no warning at startup knows the bind is
// broken, rather than chasing a panel that still says the command is blocked.
if (app.Services.GetRequiredService<CustomCommandPolicy>().Allowed)
{
    startupLogger.LogWarning(
        "{Setting} is on: a backup command typed into the panel will run on this machine as "
        + "the account this service runs as. Anyone who can sign in to the panel can use it. "
        + "Turn it off unless you are using the custom-command backup strategy.",
        $"{BackupPolicyOptions.SectionName}:{nameof(BackupPolicyOptions.AllowCustomCommand)}");
}

// One line that makes the whole class of "the service cannot find its config" problems
// visible immediately, instead of being diagnosed as something else entirely.
startupLogger.LogInformation(
    "BedrockSentry {Identity} (build channel {BuildChannel}) starting. " +
    "ContentRoot={ContentRoot} WebRoot={WebRoot} " +
    "CurrentDirectory={Cwd} WindowsService={IsService} DataRoot={DataRoot}",
    BuildInfo.Identity,
    BuildInfo.Channel,
    app.Environment.ContentRootPath,
    app.Environment.WebRootPath,
    Directory.GetCurrentDirectory(),
    WindowsServiceHelpers.IsWindowsService(),
    paths.DataRoot);

startupLogger.LogInformation(
    "Configuration sources: {Sources}",
    string.Join(", ", ((IConfigurationRoot)app.Configuration).Providers.Select(p => p.ToString())));

// Mint the setup token eagerly on an unconfigured install rather than waiting for
// someone to open the wizard. install.ps1 polls for the file so it can print the
// token at the end of an install, and an operator on Server Core has no other
// convenient way to read it.
var setupState = app.Services.GetRequiredService<SetupState>();

if (!setupState.IsConfigured)
{
    setupState.GetOrCreateToken();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

    // No UseHsts. The default certificate is self-signed, and HSTS removes the browser's
    // click-through on the resulting warning — which locks an administrator out of the
    // panel entirely until they clear HSTS state for the host by hand.
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Port and path are a compatibility contract with every future build: the plan
// file written by the build being replaced is consumed against the build
// replacing it, so changing either breaks updates from every version before it.
var healthPort = app.Services.GetRequiredService<IOptions<UpdateOptions>>().Value.HealthPort;

// The health endpoint is served only on the loopback listener the updater polls.
//
// This is a middleware check on the local port rather than RequireHost on the
// endpoint, for two reasons. The port a request arrived on is decided by the
// server; the Host header is supplied by the client, so keying on the port is
// the stronger guarantee. And RequireHost cannot express this at all: the host
// matcher throws on a bracketed IPv6 literal with a port ("[::1]:47324"), and
// because the routing table is built lazily on the first request, that failure
// takes down every route in the application rather than just this one.
//
// The paths and the match both live in LoopbackOnlyPaths, in Core, where a test
// can reach them. They used to be three Path.Equals clauses here — which is how
// `T175` happened: routing tolerates a trailing slash and an exact compare does
// not, so /readyz was refused on this listener and /readyz/ was served in full.
app.Use(async (context, next) =>
{
    if (LoopbackOnlyPaths.Matches(context.Request.Path.Value)
        && context.Connection.LocalPort != healthPort)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next(context);
});

app.UseFirstRunGate();

// Scoped to the UI listener. Applied globally it also redirects the plaintext loopback
// health request, which the updater then reads as a failed health check and rolls back.
app.UseWhen(
    context => context.Connection.LocalPort != healthPort,
    branch => branch.UseHttpsRedirection());

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// AllowAnonymous is load-bearing. MapStaticAssets registers real endpoints, so the
// fallback authorization policy applies to them — without this, an anonymous browser is
// redirected to the login page when it asks for blazor.web.js, and the app never boots.
app.MapStaticAssets().AllowAnonymous();

// Asked for by install.ps1 before it stops the service, so a hand-run upgrade
// gets the same warning, drain and backup a self-update does (docs/design.md § 12.4).
// Without it the installer's Stop-Service falls through to the service-stop path,
// which gives players five seconds and takes no backup — deliberately, because
// there the SCM is waiting. Here it is not: the installer is, and it can wait.
//
// Unauthenticated, and loopback-only three times over: Kestrel binds this port
// with ListenLocalhost, the middleware above 404s the path on any other port, and
// the remote address is checked again below.
//
// `T177`. This used to go on to say "anyone who can reach it can already run
// Stop-Service, so this grants no capability that local access did not", and that
// is FALSE for an ordinary local account. The service's own SDDL gives Interactive
// Users `CCLCSWLOCRRC` — query and interrogate, no RP/WP — so a non-administrator
// cannot start or stop the service, and can still POST here and drain every managed
// server. Local only, never remote, and a graceful drain rather than data loss; the
// capability it grants is a denial of service to somebody who already has an account
// on the box.
//
// Left open rather than gated, because install.ps1 has to reach it before it stops
// the service and that is the whole reason it exists. Whether it should carry an
// authority check, and what the installer would then present, is
// `D62-DrainEndpointAuthority`. The comment is corrected now so the next reader is
// not told the question has already been answered.
app.MapPost(UpdateOptions.DrainPath, async (
    HttpContext context,
    IManagedServerShutdown shutdown,
    IOptionsMonitor<UpdateOptions> updateOptions) =>
{
    if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
    {
        return Results.NotFound();
    }

    var warning = updateOptions.CurrentValue.PlayerWarning;

    // CancellationToken.None: the caller is an installer that is about to stop
    // this service anyway, and a drain abandoned half-way is worse than either
    // finishing it or never starting it.
    var drained = await shutdown.DrainAsync(StopReason.WrapperUpdate, warning, CancellationToken.None);

    return Results.Json(new
    {
        stopped = drained.Stopped,
        failed = drained.Failed,
        warningSeconds = (int)warning.TotalSeconds,
        version = BuildInfo.Identity,
    });
}).AllowAnonymous();

// Liveness, and nothing more. This is the endpoint the updater polls across a
// version boundary, so what it answers is a frozen contract: `status` must read
// Healthy and `version` must be the identity that was installed, or the update
// rolls back (UpdateRunner).
//
// Predicate selects NO checks — not "checks carrying some tag", which would be
// the same thing today and a trap tomorrow, because somebody would eventually
// tag a real check with it. A predicate that selects nothing cannot be widened
// by accident. An empty report is Healthy by definition and returns 200, which
// is exactly what this endpoint has always answered.
app.MapHealthChecks(UpdateOptions.HealthPath, new HealthCheckOptions
{
    Predicate = static _ => false,
    ResponseWriter = static (context, report) =>
    {
        context.Response.ContentType = "application/json";

        // The version matters as much as the status. A healthy response from the build
        // that was supposed to have been replaced means the stop or the swap did not
        // take effect — the updater treats that as a failure, not a success.
        return context.Response.WriteAsync(HealthContract.Serialize(new HealthBody
        {
            Status = report.Status.ToString(),
            Version = BuildInfo.Identity,
            BuildChannel = BuildInfo.Channel,
        }));
    },
}).AllowAnonymous();

// The detailed endpoint: the same two fields plus a per-check breakdown, and the
// only one of the two that can report anything other than Healthy.
//
// AllowAnonymous is not optional. The first-run gate exempts endpoints by
// IAllowAnonymous metadata rather than by path, so without it this returns a
// redirect to /setup on an install that has not been through the wizard. It is
// reachable only on the loopback port — see the gate above — and HTTPS
// redirection is already skipped for that whole port.
app.MapHealthChecks(HealthContract.ReadyPath, new HealthCheckOptions
{
    Predicate = static registration => registration.Tags.Contains(DetailedTag),
    ResponseWriter = static (context, report) =>
    {
        // Defence in depth, and it is here because the gate above failed once
        // (`T175`): the middleware is the guard, this is the backstop. Detail is
        // the only thing on either endpoint worth hiding — instance names, fault
        // reasons, working directories, the process holding a port — so it is
        // checked again at the one place that writes it. Nothing else in the
        // application can re-expose this by being edited.
        if (context.Connection.LocalPort
            != context.RequestServices.GetRequiredService<IOptions<UpdateOptions>>()
                .Value.HealthPort)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(HealthContract.Serialize(new HealthBody
        {
            Status = report.Status.ToString(),
            Version = BuildInfo.Identity,
            BuildChannel = BuildInfo.Channel,
            Detail = report.Entries
                .Select(e => new HealthDetail
                {
                    Name = e.Key,
                    Status = e.Value.Status.ToString(),
                    Description = e.Value.Description,
                })
                .ToList(),
        }));
    },
}).AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/account/login");
});

// Manual "install now" for notify mode. A form post rather than an interactive
// component so it works without a circuit — which matters here specifically,
// because the response to this request is the service shutting itself down.
// IOptionsMonitor, not IOptions. IOptions computes its value once at startup and
// never invalidates it on a configuration reload, so the player warning an
// operator had just saved on the Settings page — two inches above this button —
// was ignored by the drain this posts to.
app.MapPost("/admin/update/apply", (
    UpdateService updates,
    IOptionsMonitor<UpdateOptions> options) =>
{
    if (updates.Available is not { } manifest)
    {
        return Results.LocalRedirect("/settings");
    }

    // Started, not awaited. Applying now warns players and drains every server
    // first, so awaiting it here would hold the response open for the whole
    // countdown — and hand that countdown a token that cancels the moment the
    // browser gives up.
    updates.RequestApply(manifest, options.CurrentValue);

    return Results.LocalRedirect("/settings");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// `T143`: once the host is up, a later throw is a *shutdown* failure, and writing
// it under "failed to start" made the record actively misleading — a file that is
// present and wrong is worse than one that is absent, and it fooled the person who
// found it. `app.Run()` spans both phases, so the phase is latched here.
app.Lifetime.ApplicationStarted.Register(StartupFailure.MarkStarted);

app.Run();
