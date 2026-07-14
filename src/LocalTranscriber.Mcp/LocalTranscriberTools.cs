using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace LocalTranscriber.Mcp;

[McpServerToolType]
public sealed class LocalTranscriberTools
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TranscriberService _service;
    private readonly ToolCallLogger _logger;

    public LocalTranscriberTools(TranscriberService service, ToolCallLogger logger)
    {
        _service = service;
        _logger = logger;
    }

    [McpServerTool(Name = "get_status"), Description("Get current transcription status: state, session id, output paths, event count.")]
    public async Task<string> GetStatus()
    {
        _logger.Log("get_status");
        var status = await _service.Engine.GetStatusAsync();
        return JsonSerializer.Serialize(status, Pretty);
    }

    [McpServerTool(Name = "start_fake_transcription"), Description("Start a FAKE transcription session (synthetic lines, no audio) that writes .txt/.jsonl into the configured transcript folder. Useful for testing.")]
    public async Task<string> StartFakeTranscription()
    {
        _logger.Log("start_fake_transcription");
        var options = await _service.StartFakeSessionAsync();
        return $"Started fake session {options.SessionId}. Writing to {options.OutputTextPath}";
    }

    [McpServerTool(Name = "start_transcription"), Description("Start a REAL local transcription session: captures mic/system audio, transcribes offline with whisper, labels speakers. Requires models and audio devices.")]
    public async Task<string> StartTranscription()
    {
        _logger.Log("start_transcription");
        try
        {
            var options = await _service.StartRealSessionAsync();
            return $"Started real session {options.SessionId} (mic: {options.EnableMicrophone}, system: {options.EnableSystemAudio}). Writing to {options.OutputTextPath}";
        }
        catch (Exception ex)
        {
            return $"Failed to start: {ex.Message}";
        }
    }

    [McpServerTool(Name = "stop_transcription"), Description("Stop the current transcription session.")]
    public async Task<string> StopTranscription()
    {
        _logger.Log("stop_transcription");
        await _service.Engine.StopAsync();
        return "Stopped.";
    }

    [McpServerTool(Name = "pause_transcription"), Description("Pause the current transcription session.")]
    public async Task<string> PauseTranscription()
    {
        _logger.Log("pause_transcription");
        await _service.Engine.PauseAsync();
        return "Paused.";
    }

    [McpServerTool(Name = "resume_transcription"), Description("Resume a paused transcription session.")]
    public async Task<string> ResumeTranscription()
    {
        _logger.Log("resume_transcription");
        await _service.Engine.ResumeAsync();
        return "Resumed.";
    }

    [McpServerTool(Name = "tail_transcript"), Description("Read the last N lines of a transcript. Only files inside the configured transcript folder are allowed.")]
    public string TailTranscript(
        [Description("Transcript file name or relative path inside the transcript folder. Omit for the current/latest transcript.")] string? file = null,
        [Description("Number of lines to return (default 20).")] int lines = 20)
    {
        _logger.Log("tail_transcript", file ?? "(default)");
        string? path = file is null ? _service.DefaultTranscriptPath() : _service.ResolveTranscriptPath(file);
        if (path is null)
        {
            return file is null
                ? "No transcript available yet."
                : "Access denied: path is outside the configured transcript folder.";
        }

        if (!File.Exists(path))
        {
            return $"Transcript not found: {Path.GetFileName(path)}";
        }

        var all = File.ReadAllLines(path);
        return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }

    [McpServerTool(Name = "read_current_transcript"), Description("Read the full current (or latest) transcript text file.")]
    public string ReadCurrentTranscript()
    {
        _logger.Log("read_current_transcript");
        string? path = _service.DefaultTranscriptPath();
        if (path is null || !File.Exists(path))
        {
            return "No transcript available yet.";
        }

        return File.ReadAllText(path);
    }

    [McpServerTool(Name = "list_sessions"), Description("List recorded transcription sessions.")]
    public async Task<string> ListSessions()
    {
        _logger.Log("list_sessions");
        var sessions = await _service.SessionStore.ListAsync();
        return sessions.Count == 0 ? "No sessions yet." : JsonSerializer.Serialize(sessions, Pretty);
    }

    [McpServerTool(Name = "export_minutes"), Description(
        "Export a recorded session as minutes-format markdown (YAML frontmatter + transcript + notes) " +
        "for the 'minutes' tool. Local file output only. Defaults to the most recent session and the configured minutes folder.")]
    public async Task<string> ExportMinutes(
        [Description("Session id or unique prefix; omit for the most recent session.")] string? sessionId = null,
        [Description("Destination folder; omit for the configured minutes folder (~/meetings).")] string? outputFolder = null)
    {
        _logger.Log("export_minutes", sessionId ?? "(latest)");
        try
        {
            string path = await _service.ExportMinutesAsync(sessionId, outputFolder);
            return $"Exported: {path}";
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_known_speakers"), Description("List known speakers stored locally.")]
    public async Task<string> ListKnownSpeakers()
    {
        _logger.Log("list_known_speakers");
        var speakers = await _service.SpeakerStore.ListAsync();
        return speakers.Count == 0 ? "No known speakers yet." : JsonSerializer.Serialize(speakers, Pretty);
    }

    [McpServerTool(Name = "rename_speaker"), Description("Rename a speaker. Creates the name if it does not exist yet.")]
    public async Task<string> RenameSpeaker(
        [Description("Current speaker name.")] string from,
        [Description("New speaker name.")] string to)
    {
        _logger.Log("rename_speaker", $"{from} -> {to}");
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return "Both 'from' and 'to' names are required.";
        }

        await _service.SpeakerStore.RenameAsync(from, to);
        return $"Renamed '{from}' to '{to}'.";
    }

    [McpServerTool(Name = "forget_speaker"), Description("Forget a known speaker and delete its stored voice embeddings.")]
    public async Task<string> ForgetSpeaker([Description("Speaker name to forget.")] string name)
    {
        _logger.Log("forget_speaker", name);
        return await _service.SpeakerStore.ForgetAsync(name)
            ? $"Forgot speaker '{name}'."
            : $"Speaker not found: {name}";
    }

    [McpServerTool(Name = "enroll_speaker"), Description("Enroll a speaker from a WAV voice sample inside the transcript folder, so future sessions recognize them.")]
    public async Task<string> EnrollSpeaker(
        [Description("Speaker name to enroll.")] string name,
        [Description("WAV file name or relative path inside the transcript folder.")] string audioFile)
    {
        _logger.Log("enroll_speaker", $"{name} <- {audioFile}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Speaker name is required.";
        }

        string? path = _service.ResolveTranscriptPath(audioFile);
        if (path is null)
        {
            return "Access denied: audio path is outside the configured transcript folder.";
        }

        try
        {
            using var embeddings = new LocalTranscriber.Speakers.SherpaOnnxEmbeddingService();
            var embedding = await embeddings.ExtractEmbeddingAsync(new LocalTranscriber.Speakers.SpeakerEmbeddingRequest
            {
                AudioPath = path,
                Models = _service.SpeakerModels
            });
            await _service.Recognition.EnrollAsync(name, embedding, sessionId: null);
            return $"Enrolled '{name}' from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            return $"Enrollment failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "match_speaker_sample"), Description("Identify the speaker in a WAV sample inside the transcript folder by comparing against known speakers.")]
    public async Task<string> MatchSpeakerSample(
        [Description("WAV file name or relative path inside the transcript folder.")] string audioFile)
    {
        _logger.Log("match_speaker_sample", audioFile);
        string? path = _service.ResolveTranscriptPath(audioFile);
        if (path is null)
        {
            return "Access denied: audio path is outside the configured transcript folder.";
        }

        try
        {
            using var embeddings = new LocalTranscriber.Speakers.SherpaOnnxEmbeddingService();
            var embedding = await embeddings.ExtractEmbeddingAsync(new LocalTranscriber.Speakers.SpeakerEmbeddingRequest
            {
                AudioPath = path,
                Models = _service.SpeakerModels
            });
            var match = await _service.Recognition.MatchAsync(embedding);
            if (match is null)
            {
                return "No match among known speakers.";
            }

            string label = match.Certainty == LocalTranscriber.Speakers.SpeakerMatchCertainty.Confident
                ? match.DisplayName
                : $"possibly {match.DisplayName}";
            return $"{label} (similarity: {match.Similarity:F3})";
        }
        catch (Exception ex)
        {
            return $"Match failed: {ex.Message}";
        }
    }

    [McpServerTool(Name = "set_output_folder"), Description("Set the transcript output folder used for new sessions and transcript reads.")]
    public string SetOutputFolder([Description("Folder path for transcripts.")] string path)
    {
        _logger.Log("set_output_folder", path);
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Path is required.";
        }

        try
        {
            Directory.CreateDirectory(path);
            _service.SetTranscriptFolder(path);
            return $"Transcript folder set to {Path.GetFullPath(path)}";
        }
        catch (Exception ex)
        {
            return $"Failed to set folder: {ex.Message}";
        }
    }
}
