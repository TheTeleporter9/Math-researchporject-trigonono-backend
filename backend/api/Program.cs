using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Trigon.Api;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Comma-separated list of allowed origins. Use "*" only for local dev.
        var origins = (builder.Configuration["CORS_ORIGINS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (origins.Length == 0 || origins.Contains("*"))
        {
            policy
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .SetIsOriginAllowed(_ => true);
        }
        else
        {
            policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

var connString = Db.GetConnectionString(builder.Configuration);
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connString).Build());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// In containers we typically run behind a proxy; don't force HTTPS redirects in production here.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

var masterTeacherToken = builder.Configuration["TEACHER_TOKEN"] ?? "";
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/teacher"))
    {
        var provided = ctx.Request.Headers["X-Teacher-Token"].ToString();
        if (string.IsNullOrWhiteSpace(provided))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing teacher token" });
            return;
        }

        // 1) Master token (for you as experiment admin)
        if (!string.IsNullOrWhiteSpace(masterTeacherToken) &&
            string.Equals(provided, masterTeacherToken, StringComparison.Ordinal))
        {
            await next();
            return;
        }

        // 2) Per-teacher account codes stored in DB
        var dataSource = ctx.RequestServices.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await dataSource.OpenConnectionAsync();
        var teacher = await conn.QuerySingleOrDefaultAsync("""
            SELECT id, code, display_name, is_active
            FROM teachers
            WHERE code = @code AND is_active = TRUE
            """, new { code = provided });

        if (teacher is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unknown or inactive teacher code" });
            return;
        }
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { ok = true })).WithOpenApi();

static string GenerateJoinCode()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    Span<char> buffer = stackalloc char[6];
    for (var i = 0; i < buffer.Length; i++)
    {
        buffer[i] = chars[Random.Shared.Next(chars.Length)];
    }
    return new string(buffer);
}

static ApiModels.SessionDto ToSessionDto(dynamic row)
{
    // Dapper returns dynamic with snake_case props matching SELECT aliases
    var config = (string)row.config_json;
    var configEl = string.IsNullOrWhiteSpace(config)
        ? JsonDocument.Parse("{}").RootElement
        : JsonDocument.Parse(config).RootElement;

    return new ApiModels.SessionDto(
        (Guid)row.id,
        (string)row.join_code,
        (string)row.game_type,
        (string)row.status,
        configEl,
        (DateTimeOffset)row.created_at,
        (DateTimeOffset?)row.started_at,
        (DateTimeOffset?)row.ended_at
    );
}

// Teacher API
var teacherApi = app.MapGroup("/api/teacher").WithOpenApi();

teacherApi.MapGet("/sessions", async (NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var rows = await conn.QueryAsync("""
        SELECT
          id,
          join_code,
          game_type,
          status,
          config::text as config_json,
          created_at,
          started_at,
          ended_at
        FROM sessions
        ORDER BY created_at DESC
        """);

    return Results.Ok(rows.Select(ToSessionDto));
});

teacherApi.MapPost("/sessions", async (ApiModels.CreateSessionRequest req, NpgsqlDataSource ds) =>
{
    var joinCode = GenerateJoinCode();
    var rawConfig = (req.Config ?? JsonDocument.Parse("{}").RootElement).GetRawText();
    var configObj = JsonNode.Parse(rawConfig) as JsonObject ?? new JsonObject();
    if (configObj["seed_base"] is null)
    {
        configObj["seed_base"] = Random.Shared.Next(1, 1_000_000_000);
    }
    var configJson = configObj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    var gameType = string.IsNullOrWhiteSpace(req.GameType) ? "team_triangle" : req.GameType;

    await using var conn = await ds.OpenConnectionAsync();
    var row = await conn.QuerySingleAsync("""
        INSERT INTO sessions (join_code, game_type, status, config)
        VALUES (@join_code, @game_type, 'waiting', @config::jsonb)
        RETURNING
          id,
          join_code,
          game_type,
          status,
          config::text as config_json,
          created_at,
          started_at,
          ended_at
        """, new { join_code = joinCode, game_type = gameType, config = configJson });

    return Results.Ok(ToSessionDto(row));
});

teacherApi.MapPost("/sessions/{id:guid}/start", async (Guid id, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var updated = await conn.ExecuteAsync("""
        UPDATE sessions
        SET status='active', started_at=NOW()
        WHERE id=@id
        """, new { id });

    return updated == 1 ? Results.Ok(new { ok = true }) : Results.NotFound();
});

teacherApi.MapPost("/sessions/{id:guid}/end", async (Guid id, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var updated = await conn.ExecuteAsync("""
        UPDATE sessions
        SET status='ended', ended_at=NOW()
        WHERE id=@id
        """, new { id });

    return updated == 1 ? Results.Ok(new { ok = true }) : Results.NotFound();
});

teacherApi.MapGet("/sessions/{id:guid}/export", async (Guid id, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var session = await conn.QuerySingleOrDefaultAsync("""
        SELECT
          id,
          join_code,
          game_type,
          status,
          config::text as config_json,
          created_at,
          started_at,
          ended_at
        FROM sessions
        WHERE id=@id
        """, new { id });

    if (session is null) return Results.NotFound();

    var attempts = await conn.QueryAsync("""
        SELECT *
        FROM triangle_attempts
        WHERE session_id=@id
        ORDER BY created_at ASC
        """, new { id });

    var completions = await conn.QueryAsync("""
        SELECT *
        FROM level_completions
        WHERE session_id=@id
        ORDER BY completed_at ASC
        """, new { id });

    var outcomes = await conn.QueryAsync("""
        SELECT *
        FROM session_outcomes
        WHERE session_id=@id
        ORDER BY completed_at ASC
        """, new { id });

    return Results.Ok(new
    {
        session_id = (Guid)session.id,
        exported_at = DateTimeOffset.UtcNow,
        session = ToSessionDto(session),
        triangle_attempts = attempts,
        level_completions = completions,
        session_outcomes = outcomes
    });
});

// Public API
var api = app.MapGroup("/api").WithOpenApi();

api.MapGet("/sessions/by-code/{joinCode}", async (string joinCode, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var row = await conn.QuerySingleOrDefaultAsync("""
        SELECT
          id,
          join_code,
          game_type,
          status,
          config::text as config_json,
          created_at,
          started_at,
          ended_at
        FROM sessions
        WHERE join_code=@join_code
        """, new { join_code = joinCode.ToUpperInvariant() });

    return row is null ? Results.NotFound() : Results.Ok(ToSessionDto(row));
});

api.MapGet("/sessions/{id:guid}", async (Guid id, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var row = await conn.QuerySingleOrDefaultAsync("""
        SELECT
          id,
          join_code,
          game_type,
          status,
          config::text as config_json,
          created_at,
          started_at,
          ended_at
        FROM sessions
        WHERE id=@id
        """, new { id });

    return row is null ? Results.NotFound() : Results.Ok(ToSessionDto(row));
});

api.MapPost("/sessions/join", async (ApiModels.JoinSessionRequest req, NpgsqlDataSource ds) =>
{
    var code = (req.JoinCode ?? "").Trim().ToUpperInvariant();
    if (code.Length != 6) return Results.BadRequest(new { error = "Invalid join code" });
    if (string.IsNullOrWhiteSpace(req.AnonymousId)) return Results.BadRequest(new { error = "anonymous_id required" });

    await using var conn = await ds.OpenConnectionAsync();
    var session = await conn.QuerySingleOrDefaultAsync("""
        SELECT
          id,
          join_code,
          game_type,
          status,
          config::text as config_json,
          created_at,
          started_at,
          ended_at
        FROM sessions
        WHERE join_code=@join_code
        """, new { join_code = code });

    if (session is null) return Results.NotFound(new { error = "Session not found" });
    if ((string)session.status != "active") return Results.Conflict(new { error = $"Session is {(string)session.status}" });

    var sessionDto = ToSessionDto(session);

    Guid? teamId = null;
    Guid? studentId = null;

    if (sessionDto.GameType == "team_triangle")
    {
        var teamName = string.IsNullOrWhiteSpace(req.TeamName) ? $"Team {req.AnonymousId[..Math.Min(6, req.AnonymousId.Length)]}" : req.TeamName!;

        var row = await conn.QuerySingleAsync("""
            INSERT INTO teams (session_id, team_name, anonymous_id)
            VALUES (@session_id, @team_name, @anonymous_id)
            ON CONFLICT (session_id, anonymous_id)
            DO UPDATE SET team_name = EXCLUDED.team_name
            RETURNING id
            """, new
        {
            session_id = sessionDto.Id,
            team_name = teamName,
            anonymous_id = req.AnonymousId
        });

        teamId = (Guid)row.id;
    }
    else
    {
        var row = await conn.QuerySingleAsync("""
            INSERT INTO students (session_id, anonymous_id, display_name)
            VALUES (@session_id, @anonymous_id, @display_name)
            ON CONFLICT (session_id, anonymous_id)
            DO UPDATE SET display_name = EXCLUDED.display_name
            RETURNING id
            """, new
        {
            session_id = sessionDto.Id,
            anonymous_id = req.AnonymousId,
            display_name = req.DisplayName
        });

        studentId = (Guid)row.id;
    }

    return Results.Ok(new ApiModels.JoinSessionResponse(sessionDto, teamId, studentId));
});

api.MapGet("/sessions/{sessionId}/students/{anonymousId}/progress", async (Guid sessionId, string anonymousId, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var row = await conn.QuerySingleOrDefaultAsync("""
        SELECT current_level
        FROM student_progress
        WHERE session_id=@session_id AND anonymous_student_id=@anonymous_student_id
        """, new { session_id = sessionId, anonymous_student_id = anonymousId });

    var level = row?.current_level ?? 1;
    return Results.Ok(new ApiModels.StudentProgressDto(sessionId, anonymousId, level));
});

api.MapPost("/sessions/{sessionId}/students/{anonymousId}/progress", async (Guid sessionId, string anonymousId, ApiModels.SetStudentProgressRequest req, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO student_progress (session_id, anonymous_student_id, current_level)
        VALUES (@session_id, @anonymous_student_id, @level)
        ON CONFLICT (session_id, anonymous_student_id)
        DO UPDATE SET current_level = EXCLUDED.current_level
        """, new { session_id = sessionId, anonymous_student_id = anonymousId, level = req.Level });

    return Results.Ok(new { ok = true });
});

api.MapPost("/triangle/attempts", async (ApiModels.TriangleAttemptRequest req, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var measurements = req.Measurements?.GetRawText();
    await conn.ExecuteAsync("""
        INSERT INTO triangle_attempts (
          session_id, team_id, anonymous_student_id, question_id,
          selected_triangle_index, is_correct, response_time_ms, measurements
        )
        VALUES (
          @session_id, @team_id, @anonymous_student_id, @question_id,
          @selected_triangle_index, @is_correct, @response_time_ms, @measurements::jsonb
        )
        """, new
    {
        session_id = req.SessionId,
        team_id = req.TeamId,
        anonymous_student_id = req.AnonymousStudentId,
        question_id = req.QuestionId,
        selected_triangle_index = req.SelectedTriangleIndex,
        is_correct = req.IsCorrect,
        response_time_ms = req.ResponseTimeMs,
        measurements = measurements ?? "{}"
    });

    return Results.Ok(new { ok = true });
});

api.MapPost("/level-completions", async (ApiModels.LevelCompletionRequest req, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO level_completions (
          session_id, anonymous_student_id, team_id, game_type, level,
          mistakes_count, reset_count
        )
        VALUES (
          @session_id, @anonymous_student_id, @team_id, @game_type, @level,
          @mistakes_count, @reset_count
        )
        ON CONFLICT (session_id, anonymous_student_id, game_type, level)
        DO UPDATE SET
          mistakes_count = EXCLUDED.mistakes_count,
          reset_count = EXCLUDED.reset_count,
          completed_at = NOW()
        """, new
    {
        session_id = req.SessionId,
        anonymous_student_id = req.AnonymousStudentId,
        team_id = req.TeamId,
        game_type = req.GameType,
        level = req.Level,
        mistakes_count = req.MistakesCount,
        reset_count = req.ResetCount
    });

    return Results.Ok(new { ok = true });
});

api.MapPost("/outcomes", async (ApiModels.SessionOutcomeRequest req, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO session_outcomes (
          session_id, anonymous_student_id, team_id, game_type,
          final_level, total_mistakes, total_resets
        )
        VALUES (
          @session_id, @anonymous_student_id, @team_id, @game_type,
          @final_level, @total_mistakes, @total_resets
        )
        ON CONFLICT (session_id, anonymous_student_id, game_type)
        DO UPDATE SET
          final_level = EXCLUDED.final_level,
          total_mistakes = EXCLUDED.total_mistakes,
          total_resets = EXCLUDED.total_resets,
          completed_at = NOW()
        """, new
    {
        session_id = req.SessionId,
        anonymous_student_id = req.AnonymousStudentId,
        team_id = req.TeamId,
        game_type = req.GameType,
        final_level = req.FinalLevel,
        total_mistakes = req.TotalMistakes,
        total_resets = req.TotalResets
    });

    return Results.Ok(new { ok = true });
});

app.Run();
