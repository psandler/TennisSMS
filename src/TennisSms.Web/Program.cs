using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TennisSms.Web.Components;
using TennisSms.Web.Infrastructure;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TennisSms")
    ?? throw new InvalidOperationException("Connection string 'TennisSms' is missing.");

if (builder.Environment.IsDevelopment())
    await Storage.EnsureDatabaseExistsAsync(connectionString);

builder.Services.AddTennisSms(builder.Configuration, connectionString);
builder.Host.UseWolverine(opts => opts.ConfigureTennisSmsWolverine());

// Organizer login (spec §11.1): a code texted to the organizer's phone, then a cookie.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "tennissms.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
