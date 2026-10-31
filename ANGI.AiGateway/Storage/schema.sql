CREATE SCHEMA IF NOT EXISTS ai_control;
CREATE TABLE IF NOT EXISTS ai_control.control (
    id integer PRIMARY KEY CHECK(id=1), stopped boolean NOT NULL DEFAULT true,
    pricing_confirmed boolean NOT NULL DEFAULT false, policy_hash text NOT NULL
);
CREATE TABLE IF NOT EXISTS ai_control.budgets (
    workload text PRIMARY KEY CHECK(workload IN ('global','chat')),
    daily_usd numeric(20,10) NOT NULL DEFAULT 0 CHECK(daily_usd>=0),
    monthly_usd numeric(20,10) NOT NULL DEFAULT 0 CHECK(monthly_usd>=0),
    daily_calls integer NOT NULL DEFAULT 0 CHECK(daily_calls>=0),
    monthly_calls integer NOT NULL DEFAULT 0 CHECK(monthly_calls>=0)
);
CREATE TABLE IF NOT EXISTS ai_control.reservations (
    id uuid PRIMARY KEY, workload text NOT NULL REFERENCES ai_control.budgets(workload),
    admitted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    day_key date NOT NULL, month_key date NOT NULL, input_hash text NOT NULL,
    reserved_usd numeric(20,10) NOT NULL CHECK(reserved_usd>0),
    charged_upper_usd numeric(20,10), input_tokens integer, output_tokens integer, reasoning_tokens integer,
    outcome text NOT NULL DEFAULT 'pending', settled_at timestamptz,
    CHECK(charged_upper_usd IS NULL OR charged_upper_usd BETWEEN 0 AND reserved_usd)
);
CREATE INDEX IF NOT EXISTS ix_reservations_period ON ai_control.reservations(workload,month_key,day_key);
CREATE INDEX IF NOT EXISTS ix_reservations_pending ON ai_control.reservations(outcome) WHERE outcome='pending';
INSERT INTO ai_control.budgets(workload) VALUES('global'),('chat') ON CONFLICT DO NOTHING;
