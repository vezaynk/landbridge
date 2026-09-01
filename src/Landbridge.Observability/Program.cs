using Landbridge.Observability.Components;
using Landbridge.Observability.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The fake fleet: one simulator shared by every viewer, advanced on a background timer.
builder.Services.AddSingleton<DashboardSimulator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardSimulator>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
