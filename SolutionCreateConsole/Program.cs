using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public class ProjectInfo
{
    public string? name { get; set; }
    public string? type { get; set; }
    public string framework { get; set; } = "net6.0";
    public List<string> references { get; set; } = new();
    public List<NuGetPackage> packages { get; set; } = new();
}

public class NuGetPackage
{
    public string name { get; set; } = string.Empty;
    public string version { get; set; } = string.Empty;
}

public class SolutionConfig
{
    public string? drive { get; set; }
    public string? baseFolder { get; set; }
    public string? solutionName { get; set; }
    public List<ProjectInfo> projects { get; set; } = new();
}

class Program
{
    static async Task<int> Main(string[] args)
    {
        var config = LoadConfiguration();
        if (config == null) return 1;

        PrepareProjectNames(config);

        string fullPath = Path.Combine(config.drive ?? "", config.baseFolder ?? "", config.solutionName ?? "");
        Directory.CreateDirectory(fullPath);
        Directory.SetCurrentDirectory(fullPath);

        await CreateSolutionAsync(config.solutionName!);
        await CreateProjectsAsync(config);
        await AddProjectReferencesAsync(config);
        ConfigureDAL(config);
        ConfigureMainApp(config);

        Console.WriteLine("✅ Project setup complete.");
        return 0;
    }

    static SolutionConfig? LoadConfiguration()
    {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "solution.json");
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine($"❌ solution.json not found at: {jsonPath}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var config = JsonSerializer.Deserialize<SolutionConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config == null || string.IsNullOrWhiteSpace(config.solutionName))
            {
                Console.WriteLine("❌ Invalid or missing solution name.");
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to read or parse solution.json: {ex.Message}");
            return null;
        }
    }

    static void PrepareProjectNames(SolutionConfig config)
    {
        for (int i = 0; i < config.projects.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(config.projects[i].name))
            {
                config.projects[i].name = i == 0
                    ? config.solutionName!
                    : $"{config.solutionName}_DAL";
            }
        }
    }

    static Task<int> RunCommandAsync(string command)
    {
        var tcs = new TaskCompletionSource<int>();

        var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Exited += (s, e) =>
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!string.IsNullOrEmpty(output))
                Console.WriteLine(output.Trim());

            if (!string.IsNullOrEmpty(error))
                Console.WriteLine($"❌ Error: {error.Trim()}");

            tcs.SetResult(process.ExitCode);
            process.Dispose();
        };

        if (!process.Start())
        {
            tcs.SetException(new InvalidOperationException("Failed to start process."));
        }

        return tcs.Task;
    }


    static async Task CreateSolutionAsync(string solutionName)
    {
        string solutionFolderPath = Path.Combine(Directory.GetCurrentDirectory(), solutionName);

        if (Directory.Exists(solutionFolderPath))
        {
            throw new IOException($"❌ A folder named '{solutionName}' already exists. Choose a different solution name or delete the existing folder.");
        }

        Console.WriteLine($"Creating solution '{solutionName}'...");
        await RunCommandAsync($"dotnet new sln -n {solutionName}");
    }


    static async Task CreateProjectsAsync(SolutionConfig config)
    {
        foreach (var project in config.projects)
        {
            Console.WriteLine($"Creating project '{project.name}' of type '{project.type}'...");
            await RunCommandAsync($"dotnet new {project.type} -n {project.name}");
            await RunCommandAsync($"dotnet sln add {project.name}/{project.name}.csproj");

            UpdateTargetFramework(project.name, project.framework);
        }
    }

    static void UpdateTargetFramework(string projectFolder, string targetFramework)
    {
        string csprojPath = Path.Combine(projectFolder, $"{projectFolder}.csproj");
        if (!File.Exists(csprojPath)) return;

        string content = File.ReadAllText(csprojPath);
        content = Regex.Replace(content,
            "<TargetFramework>.*?</TargetFramework>",
            $"<TargetFramework>{targetFramework}</TargetFramework>",
            RegexOptions.IgnoreCase);

        File.WriteAllText(csprojPath, content);
    }

    static async Task AddProjectReferencesAsync(SolutionConfig config)
    {
        var mainProject = config.projects[0];
        string mainProjPath = $"{mainProject.name}/{mainProject.name}.csproj";

        for (int i = 1; i < config.projects.Count; i++)
        {
            string refProjPath = $"{config.projects[i].name}/{config.projects[i].name}.csproj";
            Console.WriteLine($"Adding reference: {mainProject.name} -> {config.projects[i].name}");
            await RunCommandAsync($"dotnet add {mainProjPath} reference {refProjPath}");
        }

        if (mainProject.packages != null)
        {
            foreach (var pkg in mainProject.packages)
            {
                Console.WriteLine($"Adding NuGet package '{pkg.name}' version '{pkg.version}' to {mainProject.name}");
                await RunCommandAsync($"dotnet add {mainProject.name} package {pkg.name} --version {pkg.version}");
            }
        }
    }

    static void ConfigureDAL(SolutionConfig config)
    {
        foreach (var project in config.projects)
        {
            if (project.type.Equals("classlib", StringComparison.OrdinalIgnoreCase)
                && project.name.EndsWith("_DAL", StringComparison.OrdinalIgnoreCase))
            {
                string dalFolder = project.name;
                string modelsFolder = Path.Combine(dalFolder, "Models", "Home");
                Directory.CreateDirectory(modelsFolder);



                string ns = project.name;

                WriteFile(Path.Combine(modelsFolder, "Home.cs"), $@"
namespace {ns}.Models
{{
 public class Home
    {{
    public class GetUserLoginData
    {{
        public string? LoginId {{ get; set; }}
        public string? PasswordHash {{ get; set; }}
    }}
    }}
}}".Trim());

                WriteFile(Path.Combine(dalFolder, "DBConnection.cs"), $@"
using static {ns}.Models.Home;
using Microsoft.EntityFrameworkCore;

namespace {ns}
{{
    public class DBConnection : DbContext
    {{
        public DBConnection(DbContextOptions<DBConnection> options) : base(options) {{ }}

        public DbSet<GetUserLoginData> GetUserLoginData {{ get; set; }}

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {{
            modelBuilder.Entity<GetUserLoginData>().HasNoKey();
        }}
    }}
}}".Trim());
                // ✅ Write Home_DAL.cs

                string DataAccessFolder = Path.Combine(dalFolder, "DAL");
                Directory.CreateDirectory(DataAccessFolder);

                WriteFile(Path.Combine(DataAccessFolder, "Home_DAL.cs"), $@"
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using static {ns}.Models.Home;
using {ns}.Models;

namespace {ns}.DAL
{{
    public class Home_DAL
    {{
        private readonly DBConnection _context;

        public Home_DAL(DBConnection context)
        {{
            _context = context;
        }}
 public GetUserLoginData GetUserLoginData(string userId)
        {{
            try
            {{
                var results = _context
                    .GetUserLoginData.FromSqlRaw(
                        ""exec Usp_Select_LoginUser @userId"",
                        new SqlParameter(""@userId"", userId)
                    )
                    .AsEnumerable()
                    .FirstOrDefault();
                return results;
            }}
            catch (Exception ex)
            {{
                throw ex;
            }}
        }}

       
    }}
}}");

                // Add NuGet packages for DAL projects
                if (project.packages != null)
                {
                    foreach (var pkg in project.packages)
                    {
                        Console.WriteLine($"Adding NuGet package '{pkg.name}' to {project.name}");
                        RunCommandAsync($"dotnet add {project.name} package {pkg.name} --version {pkg.version}").Wait();
                    }
                }
            }
        }
    }

    static void ConfigureMainApp(SolutionConfig config)
    {
        var project = config.projects[0];
        if (!project.type.Equals("mvc", StringComparison.OrdinalIgnoreCase)) return;

        string appFolder = project.name;
        string programPath = Path.Combine(appFolder, "Program.cs");

        if (File.Exists(programPath))
        {
            string dalNamespace = $"{config.solutionName}_DAL";

            var programBuilder = new StringBuilder();
            programBuilder.AppendLine("using AspNetCoreRateLimit;");
            programBuilder.AppendLine($"using {dalNamespace};");
            programBuilder.AppendLine("using Microsoft.EntityFrameworkCore;");
            programBuilder.AppendLine("var builder = WebApplication.CreateBuilder(args);");
            programBuilder.AppendLine();
            programBuilder.AppendLine("// Add services to the container.");
            programBuilder.AppendLine("builder.Services.AddControllersWithViews().AddJsonOptions(options =>");
            programBuilder.AppendLine("{");
            programBuilder.AppendLine("    options.JsonSerializerOptions.MaxDepth = 64;");
            programBuilder.AppendLine("});");
            programBuilder.AppendLine($"builder.Services.AddDbContext<DBConnection>(options =>");
            programBuilder.AppendLine("    options.UseSqlServer(builder.Configuration.GetConnectionString(\"DefaultConnection\")));");
            programBuilder.AppendLine("builder.Services.AddSession(options =>");
            programBuilder.AppendLine("{");
            programBuilder.AppendLine("    options.IdleTimeout = TimeSpan.FromMinutes(30);");
            programBuilder.AppendLine("    options.Cookie.HttpOnly = true;");
            programBuilder.AppendLine("    options.Cookie.IsEssential = true;");
            programBuilder.AppendLine("});");
            programBuilder.AppendLine();
            programBuilder.AppendLine("// Add memory cache needed for rate limiting");
            programBuilder.AppendLine("builder.Services.AddMemoryCache();");
            programBuilder.AppendLine("// Configure IP rate limiting");
            programBuilder.AppendLine("builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection(\"IpRateLimiting\"));");
            programBuilder.AppendLine("builder.Services.AddInMemoryRateLimiting();");
            programBuilder.AppendLine("builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();");
            programBuilder.AppendLine();
            programBuilder.AppendLine("var app = builder.Build();");
            programBuilder.AppendLine();
            programBuilder.AppendLine("app.UseDeveloperExceptionPage();");
            programBuilder.AppendLine("app.UseHttpsRedirection();");
            programBuilder.AppendLine("app.UseStaticFiles();");
            programBuilder.AppendLine("app.UseIpRateLimiting();");
            programBuilder.AppendLine("app.UseSession();");
            programBuilder.AppendLine("app.UseRouting();");
            programBuilder.AppendLine("app.UseAuthorization();");
            programBuilder.AppendLine("app.MapControllerRoute(");
            programBuilder.AppendLine("    name: \"default\",");
            programBuilder.AppendLine("    pattern: \"{controller=Home}/{action=Index}/{id?}\");");
            programBuilder.AppendLine("app.Run();");

            WriteFile(programPath, programBuilder.ToString());
        }

        WriteFile(Path.Combine(appFolder, "appsettings.json"), GetAppSettingsContent());
    }

    static string GetAppSettingsContent() => @"{
  ""IpRateLimiting"": {
    ""EnableEndpointRateLimiting"": false,
    ""StackBlockedRequests"": false,
    ""RealIpHeader"": ""X-Real-IP"",
    ""ClientIdHeader"": ""X-ClientId"",
    ""HttpStatusCode"": 429,
    ""QuotaExceededMessage"": ""Repeated Request Limit Exceeded"",
    ""GeneralRules"": [
      {
        ""Endpoint"": ""*"",
        ""Period"": ""20s"",
        ""Limit"": 200
      }
    ]
  },
  ""ConnectionStrings"": {
    ""DefaultConnection"": ""Data Source=APOLBO03-175;Initial Catalog=HRMS;Integrated Security=True;MultipleActiveResultSets=true;TrustServerCertificate=true;""
  },
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft.AspNetCore"": ""Warning""
    }
  },
  ""AllowedHosts"": ""*"",
  ""Path"": {
    ""UploadFilePath"": ""E:\\\\LiveApplications\\\\HRMS\\\\UploadFiles""
  }
}";

    static void WriteFile(string path, string content, bool backupExisting = true)
    {
        if (backupExisting && File.Exists(path))
        {
            string backupPath = path + ".bak";
            File.Copy(path, backupPath, overwrite: true);
        }
        File.WriteAllText(path, content);
        Console.WriteLine($"Written file: {path}");
    }
}
