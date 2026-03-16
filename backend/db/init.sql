-- Postgres schema for Trigon (deployable self-hosted)
-- Loaded automatically by docker-compose via /docker-entrypoint-initdb.d

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE TABLE IF NOT EXISTS sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  teacher_id TEXT,
  join_code TEXT UNIQUE NOT NULL,
  game_type TEXT NOT NULL CHECK (game_type IN ('team_triangle', 'individual_qr')),
  status TEXT NOT NULL DEFAULT 'waiting' CHECK (status IN ('waiting', 'active', 'paused', 'ended')),
  config JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  started_at TIMESTAMPTZ,
  ended_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS teams (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  team_name TEXT NOT NULL,
  anonymous_id TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(session_id, anonymous_id)
);

CREATE TABLE IF NOT EXISTS students (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  anonymous_id TEXT NOT NULL,
  team_id UUID REFERENCES teams(id) ON DELETE SET NULL,
  display_name TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(session_id, anonymous_id)
);

CREATE TABLE IF NOT EXISTS triangle_attempts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  team_id UUID REFERENCES teams(id) ON DELETE CASCADE,
  anonymous_student_id TEXT NOT NULL,
  question_id TEXT NOT NULL,
  selected_triangle_index INTEGER NOT NULL,
  is_correct BOOLEAN NOT NULL,
  response_time_ms INTEGER NOT NULL,
  measurements JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS level_completions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  anonymous_student_id TEXT NOT NULL,
  team_id UUID REFERENCES teams(id) ON DELETE SET NULL,
  game_type TEXT NOT NULL,
  level INTEGER NOT NULL,
  completed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  mistakes_count INTEGER NOT NULL DEFAULT 0,
  reset_count INTEGER NOT NULL DEFAULT 0,
  UNIQUE(session_id, anonymous_student_id, game_type, level)
);

CREATE TABLE IF NOT EXISTS session_outcomes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  anonymous_student_id TEXT NOT NULL,
  team_id UUID REFERENCES teams(id) ON DELETE SET NULL,
  game_type TEXT NOT NULL,
  final_level INTEGER,
  total_mistakes INTEGER NOT NULL DEFAULT 0,
  total_resets INTEGER NOT NULL DEFAULT 0,
  completed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(session_id, anonymous_student_id, game_type)
);

CREATE TABLE IF NOT EXISTS student_progress (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  anonymous_student_id TEXT NOT NULL,
  current_level INTEGER NOT NULL DEFAULT 1,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE(session_id, anonymous_student_id)
);

CREATE TABLE IF NOT EXISTS teachers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  code TEXT UNIQUE NOT NULL,
  display_name TEXT NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sessions_join_code ON sessions(join_code);
CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions(status);
CREATE INDEX IF NOT EXISTS idx_teams_session ON teams(session_id);
CREATE INDEX IF NOT EXISTS idx_students_session ON students(session_id);
CREATE INDEX IF NOT EXISTS idx_triangle_attempts_session ON triangle_attempts(session_id);
CREATE INDEX IF NOT EXISTS idx_level_completions_session ON level_completions(session_id);
CREATE INDEX IF NOT EXISTS idx_session_outcomes_session ON session_outcomes(session_id);
CREATE INDEX IF NOT EXISTS idx_student_progress_session ON student_progress(session_id);


