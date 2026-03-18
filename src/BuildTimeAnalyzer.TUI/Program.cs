using BuildTimeAnalyzer.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<BuildCommand>();

app.Configure(config =>
{
    config.SetApplicationName("btanalyzer");
    config.SetApplicationVersion("1.0.0");
    config.AddExample(".", "--configuration Release", "--top 10");
    config.AddExample("MyApp.sln", "--output report.html");
    config.AddExample(".", "--output report.json", "--keep-log");
});

return await app.RunAsync(args);
