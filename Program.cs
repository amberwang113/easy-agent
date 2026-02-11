using EasyAgent;
using EasyAgent.Plugins;
using EasyAgent.Services;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// Set up configuration
builder.Services.Configure<ChatbotConfiguration>(builder.Configuration);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// Register the agent service as singleton for thread-safe initialization
builder.Services.AddSingleton<IAgentService, AgentService>();

// Register the tool call executor with a typed HttpClient
builder.Services.AddHttpClient<ToolCallExecutor>();

// Eagerly initialize the agent service at startup
builder.Services.AddHostedService<AgentInitializationService>();

// Register the plugin as scoped instead of singleton to ensure proper dependency injection
builder.Services.AddScoped<SiteContextPlugin>();

var app = builder.Build();

// Debug: Print configuration values from both sources
Console.WriteLine("=== CONFIGURATION DEBUG ===");
Console.WriteLine("From IConfiguration:");
Console.WriteLine($"  WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT: '{builder.Configuration["WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT"]}'");
Console.WriteLine($"  WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL: '{builder.Configuration["WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL"]}'");
Console.WriteLine($"  WEBSITE_EASYAGENT_FOUNDRY_AGENTID: '{builder.Configuration["WEBSITE_EASYAGENT_FOUNDRY_AGENTID"]}'");
Console.WriteLine($"  WEBSITE_MANAGED_CLIENT_ID: '{builder.Configuration["WEBSITE_MANAGED_CLIENT_ID"]}'");
Console.WriteLine("");
Console.WriteLine("From Environment.GetEnvironmentVariable:");
Console.WriteLine($"  WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT: '{Environment.GetEnvironmentVariable("WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT")}'");
Console.WriteLine($"  WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL: '{Environment.GetEnvironmentVariable("WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL")}'");
Console.WriteLine($"  WEBSITE_EASYAGENT_FOUNDRY_AGENTID: '{Environment.GetEnvironmentVariable("WEBSITE_EASYAGENT_FOUNDRY_AGENTID")}'");
Console.WriteLine($"  WEBSITE_MANAGED_CLIENT_ID: '{Environment.GetEnvironmentVariable("WEBSITE_MANAGED_CLIENT_ID")}'");
Console.WriteLine("");
Console.WriteLine("Site Extension Info:");
Console.WriteLine($"  XDT_EXTENSIONPATH: '{Environment.GetEnvironmentVariable("XDT_EXTENSIONPATH")}'");
Console.WriteLine($"  HOME: '{Environment.GetEnvironmentVariable("HOME")}'");
Console.WriteLine("===========================");

app.UseStaticFiles();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Deploy WebJob - works both locally and on Azure
try
{
    Console.WriteLine("=== WEBJOB DEPLOYMENT START ===");
    Console.WriteLine($"Environment: {app.Environment.EnvironmentName}");
    Console.WriteLine($"ContentRoot: {app.Environment.ContentRootPath}");
    
    string? homeDir = Environment.GetEnvironmentVariable("HOME");
    string webJobDestination;
    
    if (!string.IsNullOrEmpty(homeDir))
    {
        // Azure App Service
        Console.WriteLine($"Running on Azure App Service (HOME={homeDir})");
        webJobDestination = Path.Combine(homeDir, "site", "Jobs", "Continuous", "EasyAgentScraper");
    }
    else
    {
        // Local development - create WebJobs folder in temp or project directory
        Console.WriteLine("Running locally (no HOME environment variable)");
        string localWebJobsRoot = Path.Combine("D:", "LocalWebJobs");
        webJobDestination = Path.Combine(localWebJobsRoot, "Continuous", "EasyAgentScraper");
        Console.WriteLine($"Local WebJobs path: {webJobDestination}");
    }

    // Source: WebJob zip file
    string appRoot = app.Environment.ContentRootPath;
    string webJobZipPath = Path.Combine(appRoot, "WebJobs", "EasyAgentScraper.zip");
    
    Console.WriteLine($"Source zip: {webJobZipPath}");
    Console.WriteLine($"Source zip exists: {File.Exists(webJobZipPath)}");
    Console.WriteLine($"Destination: {webJobDestination}");
    
    if (File.Exists(webJobZipPath))
    {
        // Clean destination directory if it exists
        if (Directory.Exists(webJobDestination))
        {
            Console.WriteLine($"Cleaning existing WebJob directory: {webJobDestination}");
            Directory.Delete(webJobDestination, recursive: true);
        }
        
        // Create destination directory
        Directory.CreateDirectory(webJobDestination);
        Console.WriteLine($"Created destination directory: {webJobDestination}");
        
        // Extract the zip file
        Console.WriteLine($"Extracting WebJob zip file...");
        ZipFile.ExtractToDirectory(webJobZipPath, webJobDestination);
        
        // Count extracted files
        var extractedFiles = Directory.GetFiles(webJobDestination, "*.*", SearchOption.AllDirectories);
        Console.WriteLine($"Successfully extracted {extractedFiles.Length} files to WebJob directory");
        
        // List first few files for verification
        foreach (var file in extractedFiles.Take(10))
        {
            Console.WriteLine($"  Extracted: {Path.GetRelativePath(webJobDestination, file)}");
        }
        
        if (extractedFiles.Length > 10)
        {
            Console.WriteLine($"  ... and {extractedFiles.Length - 10} more files");
        }
        
        // Create run.cmd if it doesn't exist (required for WebJobs)
        string runCmd = Path.Combine(webJobDestination, "run.cmd");
        if (!File.Exists(runCmd))
        {
            // Check if there's an exe file or if we should use dotnet
            var exeFiles = Directory.GetFiles(webJobDestination, "*.exe", SearchOption.TopDirectoryOnly);
            if (exeFiles.Length > 0)
            {
                string exeName = Path.GetFileName(exeFiles[0]);
                File.WriteAllText(runCmd, exeName);
                Console.WriteLine($"Created run.cmd to execute: {exeName}");
            }
            else
            {
                File.WriteAllText(runCmd, "dotnet EasyAgentWebjob.dll");
                Console.WriteLine("Created run.cmd for dotnet execution");
            }
        }
        else
        {
            Console.WriteLine("run.cmd already exists in the zip file");
        }
    }
    else
    {
        Console.WriteLine($"WARNING: WebJob zip file not found at {webJobZipPath}");
        Console.WriteLine("Make sure to create EasyAgentScraper.zip in the WebJobs folder!");
        
        // Check if the WebJobs directory exists
        string webJobsDir = Path.Combine(appRoot, "WebJobs");
        if (Directory.Exists(webJobsDir))
        {
            Console.WriteLine($"Contents of WebJobs directory:");
            foreach (var file in Directory.GetFiles(webJobsDir))
            {
                Console.WriteLine($"  - {Path.GetFileName(file)}");
            }
        }
        else
        {
            Console.WriteLine($"WebJobs directory does not exist at: {webJobsDir}");
        }
    }
    
    Console.WriteLine("=== WEBJOB DEPLOYMENT END ===");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR deploying WebJob: {ex}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    // Don't fail app startup if WebJob deployment fails
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapControllers();

app.Run();
