using System.CommandLine;
using LocalTranscriber.Cli;

namespace LocalTranscriber.Engine.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-cli-tests-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void TailFile_ReturnsLastLines()
    {
        string path = Path.Combine(_dir, "tail.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 30).Select(i => $"line {i}"));

        var result = CliApp.TailFile(path, 5);
        Assert.Equal(5, result.Count);
        Assert.Equal("line 26", result[0]);
        Assert.Equal("line 30", result[4]);
    }

    [Fact]
    public void TailFile_HandlesShortFile()
    {
        string path = Path.Combine(_dir, "short.txt");
        File.WriteAllLines(path, new[] { "only line" });

        var result = CliApp.TailFile(path, 20);
        Assert.Single(result);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsNonZero()
    {
        var root = CliApp.BuildRootCommand();
        int exit = await root.InvokeAsync(new[] { "no-such-command" });
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Tail_MissingFile_ReturnsNonZero()
    {
        Environment.ExitCode = 0;
        var root = CliApp.BuildRootCommand();
        await root.InvokeAsync(new[] { "tail", "--file", Path.Combine(_dir, "missing.txt") });
        Assert.NotEqual(0, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task FakeSession_WritesTxtAndJsonl()
    {
        string output = Path.Combine(_dir, "session.txt");
        var root = CliApp.BuildRootCommand();
        int exit = await root.InvokeAsync(new[] { "fake-session", "--output", output, "--lines", "5" });

        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.ChangeExtension(output, ".jsonl")));
        Assert.True(File.ReadAllLines(output).Length >= 5);
    }
}
