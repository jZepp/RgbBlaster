using RgbBlaster.Core;
using RgbBlaster.Engine;
using RgbBlaster.Service;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "rgbblaster.json"));
bool simulate = args.Contains("--simulate");

// Hosted services start before Kestrel binds its port, so a second copy would open the devices before it fails.
// One hardware instance per machine; simulation is not limited.
bool first = true;
using var instance = simulate ? null : new Mutex(true, @"Global\RgbBlaster.Service", out first);
if (!first)
{
    Console.Error.WriteLine("RgbBlaster service is already running. Stop it first (Ctrl+C in its window, or stop the Windows service).");
    return 1;
}
var dataDir = simulate
    ? Path.Combine(AppContext.BaseDirectory, "sim-data")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RgbBlaster");

builder.Host.UseWindowsService(o => o.ServiceName = "RgbBlaster");
// The GUI polls several times a second; per-request logs would flood the console and the event log.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
builder.WebHost.ConfigureKestrel(k =>
{
    if (config.Web.AllowLan) k.ListenAnyIP(config.Web.Port);
    else k.ListenLocalhost(config.Web.Port);
    k.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new SettingsStore(dataDir));
builder.Services.AddSingleton(sp => new RgbRuntime(config, sp.GetRequiredService<SettingsStore>(), simulate, sp.GetRequiredService<ILogger<RgbRuntime>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<RgbRuntime>());

var app = builder.Build();
app.Logger.LogInformation("RgbBlaster {Version}{Mode}", AppVersion.Display, simulate ? " (simulation)" : "");

// The API has no login. Two checks keep other web pages out:
// - the Host header must be a local name, which blocks DNS rebinding;
// - changes need the X-RgbBlaster header, which a page on another origin cannot send without a CORS preflight (never allowed).
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    bool localHost = host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    if (!config.Web.AllowLan && !localHost) { ctx.Response.StatusCode = 421; return; }
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !ctx.Request.Headers.ContainsKey("X-RgbBlaster"))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    await next();
});
app.UseDefaultFiles();
// no-cache: the browser revalidates, so a new build's GUI loads without a forced reload.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

var api = app.MapGroup("/api");
RgbRuntime rt() => app.Services.GetRequiredService<RgbRuntime>();

static IResult Guard(Func<IResult> f)
{
    try { return f(); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = $"Not found: {ex.Message}" }); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return Results.BadRequest(new { error = ex.Message }); }
}

api.MapGet("/status", () => Results.Json(rt().Status()));
api.MapGet("/history", () => rt().History());
api.MapGet("/messages", () => rt().Messages());
api.MapGet("/sensors", () => rt().Sensors());
api.MapGet("/audio", () => rt().AudioInfo());
api.MapGet("/version", () => new { version = AppVersion.Version, commit = AppVersion.Commit });
api.MapGet("/settings", () => rt().Settings());
api.MapGet("/effects", () => EffectFactory.Types);
api.MapGet("/leds", () => Results.Json(rt().LedPoints()));
// Computes LED positions for an unsaved layout, for the editor.
api.MapPost("/leds/preview", (List<ZonePlacement> layout) => Results.Json(rt().LedPoints(layout)));
api.MapGet("/preview", (int? w, int? h) =>
{
    int pw = Math.Clamp(w ?? 96, 8, 320), ph = Math.Clamp(h ?? 60, 8, 200);
    return Results.Bytes(rt().Preview(pw, ph), "application/octet-stream");
});

api.MapPut("/effect", (EffectSpec spec) => Guard(() => { rt().SetEffect(spec); return Results.NoContent(); }));
api.MapGet("/keyboard/modes", () => RgbBlaster.Drivers.EVision.EVisionProtocol.Modes.Select(m => new { mode = m.Mode, name = m.Name }));
api.MapPut("/keyboard", (KeyboardSettings k) => Guard(() => { rt().SetKeyboard(k); return Results.NoContent(); }));
api.MapPut("/fancurves", (List<FanCurveConfig> curves) => Guard(() => Results.Ok(new { warnings = rt().SetFanCurves(curves) })));
api.MapGet("/lighting/fps", () => new { fps = rt().LightingFps() });
api.MapPut("/lighting/fps", (FpsBody b) => Guard(() => { rt().SetLightingFps(b.Fps); return Results.NoContent(); }));
api.MapPut("/layout", (List<ZonePlacement> layout) => Guard(() => { rt().SetLayout(layout); return Results.NoContent(); }));

api.MapPost("/profiles", (ProfileName p) => Guard(() => { rt().SaveProfileAs(p.Name); return Results.NoContent(); }));
api.MapPost("/profiles/{name}/activate", (string name) => Guard(() => { rt().ActivateProfile(name); return Results.NoContent(); }));
api.MapDelete("/profiles/{name}", (string name) => Guard(() => { rt().DeleteProfile(name); return Results.NoContent(); }));

api.MapGet("/lcd/frame", () => rt().LcdFrame() is { } f ? Results.Bytes(f, "image/jpeg") : Results.NoContent());
api.MapGet("/lcd/screens", () => RgbBlaster.Drivers.Lcd.LcdDashboard.Screens.Select(s => new
{
    id = s.Id, name = s.Name, animated = s.FrameMs < 1000, lighting = s.UsesLighting,
    slots = RgbBlaster.Drivers.Lcd.LcdDashboard.DefaultSlots.GetValueOrDefault(s.Id) ?? [],
}));
api.MapGet("/lcd/metrics", () => RgbBlaster.Drivers.Lcd.LcdDashboard.Metrics.Select(m => new { id = m.Id, name = m.Name }));
api.MapGet("/lcd/screens/{id}/preview", (string id) => Guard(() => Results.Bytes(rt().ScreenPreview(id), "image/jpeg")));
api.MapPut("/lcd", (LcdSettings s) => Guard(() => { rt().SetLcd(s); return Results.NoContent(); }));
api.MapGet("/lcd/media", () => rt().MediaList());
api.MapGet("/lcd/media/{id}", (string id) => rt().MediaPath(id) is { } p ? Results.File(p, MediaType(p)) : Results.NotFound());
api.MapDelete("/lcd/media/{id}", (string id) => Guard(() => { rt().DeleteMedia(id); return Results.NoContent(); }));
api.MapPost("/lcd/media", async (HttpRequest req) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "Send the file as multipart form data." });
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.BadRequest(new { error = "No file." });
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")) return Results.BadRequest(new { error = "Use PNG, JPEG, BMP or GIF." });
    try
    {
        await using var s = file.OpenReadStream();
        return Results.Ok(new { id = rt().AddMedia(s, ext) });
    }
    catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
    {
        return Results.BadRequest(new { error = "The file is not an image that can be decoded." });
    }
}).DisableAntiforgery();

app.Run();
return 0;

static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".png" => "image/png",
    ".gif" => "image/gif",
    ".bmp" => "image/bmp",
    _ => "image/jpeg",
};

record ProfileName(string Name);
record FpsBody(int Fps);
