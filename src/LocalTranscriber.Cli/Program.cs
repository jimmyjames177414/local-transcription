using System.CommandLine;
using LocalTranscriber.Cli;

return await CliApp.BuildRootCommand().InvokeAsync(args);
