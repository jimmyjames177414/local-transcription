namespace LocalTranscriber.Storage.Tests;

public class ConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var service = new ConfigService(Path.Combine(_dir, "config.json"));
        var config = service.Load();
        Assert.Equal(Path.Combine("output", "transcripts"), config.TranscriptFolder);
        Assert.Equal(0.72, config.SpeakerMatchThreshold);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var service = new ConfigService(Path.Combine(_dir, "config.json"));
        var config = service.Load();
        config.TranscriptFolder = "custom/folder";
        config.ChunkSeconds = 15;
        service.Save(config);

        var reloaded = service.Load();
        Assert.Equal("custom/folder", reloaded.TranscriptFolder);
        Assert.Equal(15, reloaded.ChunkSeconds);
    }

    [Fact]
    public void TrySet_ParsesTypedValues()
    {
        var service = new ConfigService(Path.Combine(_dir, "config.json"));
        var config = service.Load();

        Assert.True(service.TrySet(config, "transcriptFolder", "x/y"));
        Assert.Equal("x/y", config.TranscriptFolder);

        Assert.True(service.TrySet(config, "enableMicCapture", "false"));
        Assert.False(config.EnableMicCapture);

        Assert.True(service.TrySet(config, "chunkSeconds", "30"));
        Assert.Equal(30, config.ChunkSeconds);

        Assert.True(service.TrySet(config, "speakerMatchThreshold", "0.8"));
        Assert.Equal(0.8, config.SpeakerMatchThreshold);
    }

    [Fact]
    public void TrySet_RejectsUnknownKeyAndBadValue()
    {
        var service = new ConfigService(Path.Combine(_dir, "config.json"));
        var config = service.Load();
        Assert.False(service.TrySet(config, "noSuchKey", "1"));
        Assert.False(service.TrySet(config, "chunkSeconds", "not-a-number"));
    }
}
