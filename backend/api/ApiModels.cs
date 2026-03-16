using System.Text.Json;

namespace Trigon.Api;

public static class ApiModels
{
    public sealed record SessionDto(
        Guid Id,
        string JoinCode,
        string GameType,
        string Status,
        JsonElement Config,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt
    );

    public sealed record CreateSessionRequest(
        string GameType,
        JsonElement? Config
    );

    public sealed record JoinSessionRequest(
        string JoinCode,
        string AnonymousId,
        string? TeamName,
        string? DisplayName
    );

    public sealed record JoinSessionResponse(
        SessionDto Session,
        Guid? TeamId,
        Guid? StudentId
    );

    public sealed record TriangleAttemptRequest(
        Guid SessionId,
        Guid? TeamId,
        string AnonymousStudentId,
        string QuestionId,
        int SelectedTriangleIndex,
        bool IsCorrect,
        int ResponseTimeMs,
        JsonElement? Measurements
    );

    public sealed record LevelCompletionRequest(
        Guid SessionId,
        string AnonymousStudentId,
        Guid? TeamId,
        string GameType,
        int Level,
        int MistakesCount,
        int ResetCount
    );

    public sealed record SessionOutcomeRequest(
        Guid SessionId,
        string AnonymousStudentId,
        Guid? TeamId,
        string GameType,
        int? FinalLevel,
        int TotalMistakes,
        int TotalResets
    );

    public sealed record StudentProgressDto(
        Guid SessionId,
        string AnonymousStudentId,
        int CurrentLevel
    );

    public sealed record SetStudentProgressRequest(
        int Level
    );
}

