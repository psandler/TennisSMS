using TennisSms.Core.Time;
using TennisSms.Web.Components;
using TennisSms.Web.Infrastructure;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TennisSms")
    ?? throw new InvalidOperationException("Connection string 'TennisSms' is missing.");

if (builder.Environment.IsDevelopment())
    await Storage.EnsureDatabaseExistsAsync(connectionString);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(GameClock.Chicago());
builder.Services.AddTennisSmsStorage(connectionString);
builder.Host.UseWolverine(opts => opts.ConfigureTennisSmsWolverine());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
