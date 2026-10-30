# AI Interview Practice Platform — Build Plan

> Dev database: SQL Server named instance `DESKTOP-2ERCDGB\MSSQLSERVER01`
> Prepared: 30 October 2026

---

## 1. Goal

Build a practice-only AI mock interview platform (similar to Yoodli, Huru, Final Round AI, Interviews by AI) focused on Indian freshers and early-career candidates.

- Real-time voice interviewer that adapts follow-up questions
- Category-specific rubrics (HR, Behavioural/STAR, Technical first)
- Evidence-based feedback with top 3 improvements and a practice plan
- Progress tracking that proves the user is improving
- No live-interview "copilot" (avoids cheating risk, keeps B2B trust)

---

## 2. Database setup (SQL Server)

### 2.1 Local dev instance

| Item | Value |
|---|---|
| Server | `DESKTOP-2ERCDGB\MSSQLSERVER01` |
| Database | `InterviewAI` |
| Auth (dev) | Windows authentication |
| Use | Development only |

**One-time setup**

1. SQL Server Configuration Manager → *SQL Server Network Configuration* → *Protocols for MSSQLSERVER01* → enable **TCP/IP**.
2. Start the **SQL Server Browser** service and set it to Automatic (required for named instances).
3. Restart the `SQL Server (MSSQLSERVER01)` service.
4. In SSMS, create the database:

```sql
CREATE DATABASE InterviewAI;
GO
ALTER DATABASE InterviewAI SET READ_COMMITTED_SNAPSHOT ON;  -- fewer read/write blocks
GO
SELECT @@VERSION;  -- check version (decides vector support)
```

**Connection string (dev)**

```
Server=DESKTOP-2ERCDGB\MSSQLSERVER01;Database=InterviewAI;Trusted_Connection=True;TrustServerCertificate=True;
```

Keep it in `appsettings.Development.json` / `.env`, never commit production secrets.

### 2.2 Production

Your desktop instance cannot serve real users (not internet-reachable, not always on). Use the same schema on:

- **Azure SQL Database** (recommended, includes native `VECTOR` type), or
- **AWS RDS for SQL Server** (Mumbai region for India latency)

Use SQL authentication or managed identity in production, automated backups, and point-in-time restore.

### 2.3 Vector search (question ↔ resume/JD matching)

| SQL Server version | Approach |
|---|---|
| SQL Server 2025 / Azure SQL | Native `VECTOR(1536)` column + `VECTOR_DISTANCE()` |
| 2019 / 2022 | Store embeddings outside (e.g. small vector service) or `VARBINARY` + app-side similarity for small question banks |

---

## 3. Tech stack

| Part | Choice | Why |
|---|---|---|
| Backend API | ASP.NET Core 8/9 Web API + EF Core | Best SQL Server support, migrations, LINQ |
| Background jobs | Hangfire (SQL Server storage) | Job queue in the same DB, no extra infra early |
| Voice agent | Python + LiveKit Agents | Best real-time voice ecosystem; calls the API, never the DB directly |
| Frontend | Next.js (React, TypeScript, Tailwind) | Interview room, reports, dashboards |
| Speech-to-text | Deepgram / AssemblyAI (streaming) | Test on Indian English accents |
| LLM | Claude (fast model for interviewer, stronger model for evaluator) | Streaming, structured JSON output |
| Text-to-speech | ElevenLabs / Cartesia / Azure Neural TTS | Low first-audio latency |
| File storage | Azure Blob / S3 | Resumes, optional recordings (auto-delete) |
| Cache (later) | Redis | Live session state at scale |
| Observability | Serilog + OpenTelemetry, Sentry, Langfuse | Logs, traces, LLM cost per session |
| Payments | Razorpay | UPI + cards in INR |

*Alternative:* Python everywhere (FastAPI + SQLAlchemy + pyodbc). Works, but weaker migration tooling than EF Core.

---

## 4. Database schema (v1)

Use `UNIQUEIDENTIFIER` or `BIGINT IDENTITY` keys, `DATETIME2` timestamps, `NVARCHAR(MAX)` + `ISJSON()` check constraints for JSON columns.

| Table | Key columns | Notes |
|---|---|---|
| `Users` | Id, Email (unique), Name, Role, OrgId, Plan, CreatedAt | Role: candidate / org_admin / admin |
| `Organizations` | Id, Name, Type, Seats, CreatedAt | College / company (B2B) |
| `Profiles` | UserId (PK/FK), ResumeBlobUrl, ParsedResumeJson, TargetRole, ExperienceLevel | |
| `Rubrics` | Id, Category, Version, DimensionsJson | Versioned so scores stay comparable |
| `InterviewTemplates` | Id, Category, Difficulty, Persona, RubricId, DurationMin | |
| `Questions` | Id, Category, Difficulty, Text, Tags, Embedding | Embedding = VECTOR or external |
| `Sessions` | Id, UserId, TemplateId, JobDescription, Status, StartedAt, EndedAt, CostPaise | Index (UserId, StartedAt DESC) |
| `Turns` | Id, SessionId, Seq, Speaker, Text, StartMs, EndMs | Largest table; index (SessionId, Seq) |
| `Evaluations` | SessionId, RubricVersion, OverallScore, ScoresJson, FeedbackJson, ModelVersion | |
| `DeliveryMetrics` | SessionId, Wpm, FillerCount, LongPauses, TalkRatio, AvgAnswerSec | Computed in code, not by LLM |
| `PracticePlans` | Id, UserId, Skill, DrillsJson, Status, DueDate | |
| `Subscriptions` | Id, UserId/OrgId, Plan, Status, RenewsAt | |
| `Payments` | Id, SubscriptionId, AmountPaise, Provider, ProviderRef, Status | |

**Rules**

- Never store raw audio/video in the DB — only blob URLs; delete raw media after processing unless the user opts in.
- Store money as integer paise.
- Add `RowVersion` (rowversion) on tables edited concurrently.

---

## 5. System flow

1. User picks category + difficulty, uploads resume, pastes JD → API creates `Session` and an interview plan.
2. Client gets a LiveKit token and joins the room; a voice agent worker joins.
3. Agent loop: streaming STT → interviewer LLM → streaming TTS, with barge-in.
4. Agent posts turns to the API in batches → saved to `Turns`.
5. Session ends → Hangfire `EvaluateSession` job:
   - compute delivery metrics in code
   - evaluator LLM scores each answer against the rubric with transcript evidence
   - aggregate scores in code → write `Evaluations`, `PracticePlans`
6. Client notified (SignalR) when the report is ready (target < 60 s).

**Latency target:** voice-to-voice ≤ 1.5 s at p95.

---

## 6. Phased plan

| Phase | Weeks | Deliverable | Exit criteria |
|---|---|---|---|
| 0. Setup | 1 | Repo, SQL Server connection, EF Core migrations, config, CI | `dotnet ef database update` creates all tables on `MSSQLSERVER01` |
| 1. Core API | 2–3 | Auth (Google + email), profile, resume parsing, templates, sessions API | Postman/integration tests pass for full session flow |
| 2. Text interview | 4 | Interviewer AI over chat; turns saved | Full HR interview completes in text |
| 3. Evaluator | 5 | Hangfire scoring + delivery metrics + report | Report < 60 s; consistent across 5 reruns |
| 4. Voice | 6–7 | LiveKit + streaming STT/LLM/TTS, interruptions | p95 voice reply < 1.5 s |
| 5. Frontend | 6–8 (parallel) | Interview room, report, history, progress chart | 10 test users finish without help |
| 6. Quality | 8–9 | 100 human-rated answers per category, regression evals | AI within ±1 of human score on ≥ 85% |
| 7. Beta launch | 10–12 | Payments, rate limits, monitoring, consent/privacy, move DB to cloud | 200 beta users; cost per interview tracked |

**Launch categories:** HR, Behavioural (STAR), Technical Q&A.
**Next (v1):** Coding round, Case, Campus placement, video presence, mobile app, B2B college portal.

> Build text-mode first: it perfects questions, follow-ups and feedback cheaply. Voice is then only a transport layer.

---

## 7. Folder structure

```
interview-ai/
├── backend/            # ASP.NET Core API + EF Core + Hangfire
│   ├── Api/
│   ├── Domain/
│   ├── Infrastructure/ # DbContext, migrations, AI provider clients
│   └── Tests/
├── agent/              # Python LiveKit voice agent
├── web/                # Next.js app
├── content/
│   ├── rubrics/        # *.json per category
│   ├── questions/      # *.json question banks
│   └── prompts/        # interviewer + evaluator prompts
├── evals/              # golden datasets + scoring regression tests
├── infra/              # docker-compose, deployment scripts
└── BUILD_PLAN.md
```

---

## 8. Quality & feedback rules

- Every critique quotes or timestamps the candidate's answer.
- Every weakness comes with a concrete rewrite or drill.
- Show top 3 improvements first; details on click.
- Never let the LLM count things (fillers, WPM) — compute in code.
- Never score accent; run fairness checks across accents.
- Prompt or model changes must pass the regression eval suite in CI.

---

## 9. Security & privacy

- Explicit consent before recording; video off by default.
- Encryption in transit and at rest; secrets in a vault (Azure Key Vault / AWS Secrets Manager).
- Tenant isolation for B2B (OrgId filters / row-level security).
- User data export and deletion (India DPDP Act 2023, GDPR) — confirm specifics with a lawyer.
- Treat resume/JD text as data; guard against prompt injection.
- Rate limits and quotas per user/org to cap AI cost.

---

## 10. Metrics

| Area | Metric |
|---|---|
| Activation | % sign-ups completing first interview within 24 h |
| Engagement | Interviews per active user per week |
| Outcome | Score improvement per skill after 5 sessions |
| Quality | Feedback helpfulness rating; AI–human agreement |
| Reliability | Session completion rate; p95 voice latency |
| Business | Free→paid conversion, churn, cost per interview, gross margin |

---

## 11. Week 1 checklist

- [ ] Enable TCP/IP + SQL Server Browser on `MSSQLSERVER01`
- [ ] Create `InterviewAI` database; run `SELECT @@VERSION`
- [ ] Scaffold ASP.NET Core API; add `Microsoft.EntityFrameworkCore.SqlServer`
- [ ] Write schema (section 4) as EF Core entities + first migration
- [ ] Write HR and Behavioural rubrics as JSON + 50 curated questions each
- [ ] Seed DB; test interviewer prompt in plain chat against 10 sample answers

---

## 12. Open decisions

1. Backend language: **C#/.NET (recommended)** or Python?
2. SQL Server version (from `SELECT @@VERSION`) → native vector search or external?
3. Cloud for production: Azure or AWS?
