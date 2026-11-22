using ComicPos.Core.Services;
using ComicPos.Data;
using ComicPos.Web.Components;
using ComicPos.Web.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Local-only by default - this is a one-register shop POS, not a public
// site. Pin Kestrel to localhost so it never ends up reachable on the
// shop's Wi-Fi unless someone deliberately changes this later.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5080);
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ComicDbContext.OnConfiguring already points at the same
// %LocalAppData%\ComicPos\comic_pos.db file the WinForms app
// (ComicPos.App) reads and writes, so nothing here needs its own
// connection string - same data, either front end.
// Scoped (one DbContext per request/circuit), not a single shared
// instance like the desktop app used - WinForms only ever had one user
// at a time, but a web app can have more than one browser tab/circuit
// open, and EF Core's DbContext is not safe to share across those.
builder.Services.AddDbContext<ComicDbContext>();

builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();
builder.Services.AddScoped<ISalesHistoryService, SalesHistoryService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IPullListService, PullListService>();
builder.Services.AddScoped<IIssueHoldService, IssueHoldService>();
builder.Services.AddScoped<IImportService, ImportService>();
builder.Services.AddScoped<IFocReportService, FocReportService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IAutoFetchService, AutoFetchService>();
builder.Services.AddScoped<IWorkerService, WorkerService>();

// No DbContext dependency - safe (and cheaper) as singletons.
builder.Services.AddSingleton<IEmailService, EmailService>();
builder.Services.AddSingleton<IEmailSettingsService, EmailSettingsService>();
builder.Services.AddSingleton<IStoreSettingsService, StoreSettingsService>();
builder.Services.AddSingleton<IComicVineService, ComicVineService>();
builder.Services.AddSingleton<IComicListService, ComicListService>();
builder.Services.AddSingleton<IComicadeFocService, ComicadeFocService>();
builder.Services.AddSingleton<IGcdService, GcdService>();
builder.Services.AddSingleton<IMetronService, MetronService>();
builder.Services.AddSingleton<ILocgService, LocgService>();
builder.Services.AddScoped<ComicPos.Web.Services.CurrentWorkerContext>();

// Web equivalent of MainForm's hourly maintenance timer - see
// BackgroundJobsService's own header comment for why this runs in its own
// scope instead of borrowing a browser circuit's.
builder.Services.AddHostedService<BackgroundJobsService>();

var app = builder.Build();

// Same auto-migrate-on-startup behavior as the WinForms app - a shop
// running this for the first time should never have to know what
// "dotnet ef database update" means.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ComicDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Comic POS could not set up its database and cannot start: {ex.Message}");
        return;
    }

    // One-time repair for the "wrong era cover" bug fixed 2026-11-21 (see
    // StoreSettings.WrongEraCoverCleanupApplied and AutoFetchService's new
    // IssueDateHint) - clears out covers that already got filled in, but
    // with the wrong decade's image, so the next Re-scan correctly
    // refetches them using each issue's own release date instead of the
    // series' often-wrong StartYear. Runs once per store, ever.
    var storeSettings = scope.ServiceProvider.GetRequiredService<IStoreSettingsService>();
    var settings = storeSettings.Load();
    if (!settings.WrongEraCoverCleanupApplied)
    {
        // Named here on purpose, not auto-detected - "which series were
        // affected" isn't something that can be told apart from "a
        // genuinely correct cover" after the fact. Add a series name here
        // if another one turns out to have the same problem.
        var affectedSeriesNames = new[] { "Wonder Woman" };

        var affectedCovers = db.Covers
            .Include(c => c.Issue)
            .Where(c => affectedSeriesNames.Contains(c.Issue.Series.Name)
                        && c.CoverImagePath != null && c.CoverImagePath != "")
            .ToList();

        if (affectedCovers.Count > 0)
        {
            foreach (var cover in affectedCovers)
                cover.CoverImagePath = null;
            db.SaveChanges();
            Console.WriteLine($"One-time cleanup: cleared {affectedCovers.Count} cover(s) saved with the wrong era's image ({string.Join(", ", affectedSeriesNames)}) - run Re-scan Existing Books to refill them correctly.");
        }

        settings.WrongEraCoverCleanupApplied = true;
        storeSettings.Save(settings);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// MapStaticAssets() below fully replaces UseStaticFiles() in a Blazor
// Web App - having both registered at once is what just corrupted every
// static response (200/500 with an empty body): two middleware/endpoint
// layers both trying to write the same response.
app.UseAntiforgery();

// Cover art lives on disk as plain files (Cover.CoverImagePath), same as
// the desktop app reads them - this just streams that same file back as
// an http response so a plain <img src="..."> tag in a Razor page can
// show it. No caching headers yet; covers don't change often, but this
// is the simple version first.
app.MapGet("/api/cover-image/{coverId:int}", async (int coverId, ComicDbContext db) =>
{
    var cover = await db.Covers.FindAsync(coverId);
    if (cover == null || string.IsNullOrWhiteSpace(cover.CoverImagePath) || !File.Exists(cover.CoverImagePath))
        return Results.NotFound();

    var bytes = await File.ReadAllBytesAsync(cover.CoverImagePath);
    var contentType = cover.CoverImagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
    return Results.File(bytes, contentType);
});

// Same idea as the cover-image endpoint above, but for
// StoreSettings.LogoPath - used on printed/on-screen receipts and
// (later) the Settings page's logo preview.
app.MapGet("/api/store-logo", async (IStoreSettingsService storeSettings) =>
{
    var path = storeSettings.Load().LogoPath;
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        return Results.NotFound();

    var bytes = await File.ReadAllBytesAsync(path);
    var contentType = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
    return Results.File(bytes, contentType);
});

// .NET 10's Static Web Assets are served as endpoints from the
// generated manifest (compression/fingerprinting included) - this is
// what serves framework files like _framework/blazor.web.js, plus
// everything under wwwroot (css/app.css, images, etc). Without this,
// blazor.web.js 404s and every @onclick/@oninput in the app silently
// does nothing (no circuit ever connects).
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
