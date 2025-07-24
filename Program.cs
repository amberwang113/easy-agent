using EasyAgent.Plugins;
using EasyAgent;
using EasyAgent.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Set up configuration
builder.Services.Configure<ChatbotConfiguration>(builder.Configuration);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// Register the agent service as singleton for thread-safe initialization
builder.Services.AddSingleton<IAgentService, AgentService>();

// Register the plugin as scoped instead of singleton to ensure proper dependency injection
builder.Services.AddScoped<SiteContextPlugin>();
builder.Services.AddHostedService<WebsiteScrapingService>();

var app = builder.Build();

app.UseStaticFiles();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapControllers();

app.Run();
