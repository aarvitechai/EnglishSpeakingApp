# Pratibha: AI interview practice

Practice-only mock interviews for Indian freshers and early-career candidates: HR, behavioural (STAR) and technical rounds, by voice or text, with feedback that quotes the candidate's own words. See [BUILD_PLAN.md](BUILD_PLAN.md) for the product plan.

## Run locally

Needs the .NET 10 SDK, Node 24, and the SQL Server instance named in `backend/Api/appsettings.Development.json`.

```powershell
cd web; npm ci; npm run build; npm run publish:api; cd ..
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project backend/Api --no-launch-profile --urls http://localhost:5090
```

Open http://localhost:5090. The first start creates the `InterviewAI` database and seeds rubrics, templates and 150 questions.

## Turning on real AI

Without a key the app runs on a rule-based interviewer and scorer, and reports say so. To use Claude, keep the key out of the repo:

```powershell
dotnet user-secrets --project backend/Api set Ai:ApiKey <key>   # or the Ai__ApiKey environment variable
```

The interviewer uses `claude-haiku-5-5` and the evaluator `claude-opus-5-5` (`Ai:InterviewerModel`, `Ai:EvaluatorModel`). The evaluator enables Anthropic's default server-side refusal fallback.

## Configuration

| Setting | Purpose | Without it |
|---|---|---|
| `ConnectionStrings:Default` | SQL Server | App will not start |
| `Ai:ApiKey` | Claude | Rule-based engine, labelled in reports |
| `Google:ClientId` | Google sign-in | Email sign-in only |
| `Billing:KeyId`, `Billing:KeySecret`, `Billing:WebhookSecret` | Razorpay | Upgrade hidden |
| `Billing:ProPricePaise` | Pro price (default ₹499, a placeholder) | |
| `Quotas:FreePerMonth`, `Quotas:ProPerMonth` | Interview limits (5 and 100) | |
| `Admin:Emails:0` | Grants admin at sign-up | No admin access |
| `Database:AutoMigrate` | Migrate on start (on in Development only) | Run `dotnet ef database update` |

Point Razorpay webhooks at `/api/billing/webhook`, event `payment.captured`.

## Tests

```powershell
dotnet test backend/InterviewAI.slnx   # creates a throwaway InterviewAI_Test database
```

`evals/golden/answers.json` drives the scoring regression test. Its labels are the author's, not human raters', so treat its agreement figure as a regression guard, not a quality measure.

## Layout

- `backend/`: ASP.NET Core API, EF Core, Hangfire, SignalR. Also serves the static web build.
- `web/`: Next.js static export.
- `content/`: rubrics, question banks and prompts, loaded at startup.
- `evals/`: golden scoring data.
- `infra/`: Dockerfile and compose stack.

## Production checklist

- Use Azure SQL or RDS for SQL Server, and keep secrets in a vault.
- Run behind an HTTPS reverse proxy. Forwarded headers are trusted from any source, so the app must not be directly reachable.
- Cookie signing keys live in the `DataProtectionKeys` table unencrypted, so protect them with database encryption (TDE, on by default in Azure SQL) or configure a key vault.
- Get the privacy notice reviewed by a lawyer.
