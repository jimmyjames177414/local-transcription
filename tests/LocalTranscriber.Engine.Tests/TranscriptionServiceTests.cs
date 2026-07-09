using LocalTranscriber.AI;

namespace LocalTranscriber.Engine.Tests;

public class TranscriptionServiceTests
{
    [Fact]
    public async Task MissingModel_ThrowsHelpfulError()
    {
        using var service = new WhisperCppTranscriptionService();
        var request = new TranscriptionRequest
        {
            AudioPath = "does-not-matter.wav",
            ModelPath = Path.Combine(Path.GetTempPath(), "no-such-model.bin")
        };

        var ex = await Assert.ThrowsAsync<WhisperModelNotFoundException>(() => service.TranscribeAsync(request));
        Assert.Contains("Whisper model not found", ex.Message);
        Assert.Contains("no-such-model.bin", ex.Message);
        Assert.Contains("update config", ex.Message);
    }

    [Fact]
    public async Task MissingAudio_ThrowsFileNotFound()
    {
        string fakeModel = Path.Combine(Path.GetTempPath(), "lt-fake-model-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(fakeModel, new byte[] { 1, 2, 3 });
        try
        {
            using var service = new WhisperCppTranscriptionService();
            var request = new TranscriptionRequest
            {
                AudioPath = Path.Combine(Path.GetTempPath(), "no-such-audio.wav"),
                ModelPath = fakeModel
            };

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => service.TranscribeAsync(request));
            Assert.Contains("Audio file not found", ex.Message);
        }
        finally
        {
            File.Delete(fakeModel);
        }
    }

    [Fact]
    public void RequestDefaults_AreSane()
    {
        var request = new TranscriptionRequest { AudioPath = "a.wav", ModelPath = "m.bin" };
        Assert.Null(request.Language);
        Assert.False(request.TranslateToEnglish);
        Assert.True(request.IncludeTimestamps);
        Assert.Null(request.Timeout);
    }

    [Fact]
    public void Result_MapsSegments()
    {
        var segments = new[]
        {
            new TranscribedSegment("Hello.", 0, 1200, 0.9),
            new TranscribedSegment("World.", 1200, 2400, 0.8)
        };
        var result = new TranscriptionResult("Hello. World.", segments, 0.85, TimeSpan.FromSeconds(1));
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(1200, result.Segments[0].EndMs);
        Assert.Equal("Hello. World.", result.Text);
    }
}
