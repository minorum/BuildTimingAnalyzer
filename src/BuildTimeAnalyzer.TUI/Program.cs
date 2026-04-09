using BuildTimeAnalyzer.Commands;
using Spectre.Console.Cli;

#pragma warning disable IL3050 // Spectre.Console.Cli uses reflection but works correctly under AOT
var app = new CommandApp();
#pragma warning restore IL3050

app.Configure(config =>
{
    config.SetApplicationName("btanalyzer");
    config.SetApplicationVersion(BuildTimeAnalyzer.VersionInfo.Version);
    config.AddCommand<BuildCommand>("build")
        .WithDescription("Build and analyze a project or solution.")
        .WithExample("build")
        .WithExample("build", ".", "-c", "Release", "-n", "10")
        .WithExample("build", "MyApp.sln", "-o", "report.html");
});

return await app.RunAsync(args);
