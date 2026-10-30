# NeuroTara: AI interview practice

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
| `Billing:PlusPricePaise`, `Billing:ProPricePaise` | Prices (₹199 and ₹499, placeholders) | |
| `Quotas:FreePerMonth`, `Quotas:PlusPerMonth`, `Quotas:ProPerMonth` | Interview limits (5, 30 and 100) | |
| `SuperAdmin:Email`, `SuperAdmin:Password` | Creates the first super admin on start, if none exists | No admin access |
| `Database:AutoMigrate` | Migrate on start (on in Development only) | Run `dotnet ef database update` |

Point Razorpay webhooks at `/api/billing/webhook`, event `payment.captured`.

## Roles

Everyone signs in at `/login/` and lands on the right area for their role.

- **User:** interviews, reports, progress and their own subscription.
- **Admin:** also sees metrics and the user list, can disable accounts and reset passwords for users. Cannot touch staff.
- **Super admin:** also creates staff, changes roles, sets plans, and sees payments, the audit log and the `/ops/jobs` dashboard.

Staff cannot read transcripts or resumes through the console. The first super admin comes only from configuration and must change the password at first sign-in. Sign-up can never grant a staff role.

## Levels and plans

| Level | Name | Plan needed |
|---|---|---|
| 1 | Zero | Free |
| 2 | Beginner | Free |
| 3 | Intermediate | Plus |
| 4 | Advanced | Plus |
| 5 | Expert | Pro |

A paid plan is a 30-day pass, not an auto-renewing mandate. Paying again before it ends adds 30 days.

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
- Email is not verified and password reset is admin-assisted, because no email provider is connected. Add one before opening public sign-up.
- Get the privacy notice reviewed by a lawyer.
