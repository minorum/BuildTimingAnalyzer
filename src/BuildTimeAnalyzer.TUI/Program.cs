using BuildTimeAnalyzer.Commands;
using Spectre.Console.Cli;

#pragma warning disable IL3050 // Spectre.Console.Cli uses reflection but works correctly under AOT
var app = new CommandApp<BuildCommand>();
#pragma warning restore IL3050

app.Configure(config =>
{
    config.SetApplicationName("btanalyzer");
    config.SetApplicationVersion("1.0.0");
    config.AddExample(".", "--configuration Release", "--top 10");
    config.AddExample("MyApp.sln", "--output report.html");
    config.AddExample(".", "--output report.json", "--keep-log");
});

return await app.RunAsync(args);
