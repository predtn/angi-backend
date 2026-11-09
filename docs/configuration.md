# Configuration

The API reads its keys from `appsettings.json`, user secrets and environment variables; it does **not** read `.env` (that file only configures the local PostgreSQL container). Every key below is empty in `appsettings.json` and listed in `.env.example` as a reminder. Get the shared dev keys from the team's private channel and never commit a value.

## Keys

| Key | Used for | Needed locally |
|---|---|---|
| `ConnectionStrings:Default` | PostgreSQL (role `angi_backend`) | Always |
| `Jwt:SecretKey` | Signing access tokens, at least 32 bytes; a local random value is fine | Always: the API refuses to start without it |
| `Recommendation:BaseUrl` | Recommendation service; locally `http://127.0.0.1:8000`, not `localhost`: uvicorn listens on IPv4 only and Windows first tries `localhost` over IPv6, which takes about 2 s to fail, past the 800 ms timeout | Survey and recommendation features |
| `Recommendation:ApiKey` | Header `X-Api-Key`; must equal `RECO_API_KEY` in the `.env` of angi-reco | Survey and recommendation features |
| `Cloudinary:CloudName`, `Cloudinary:ApiKey`, `Cloudinary:ApiSecret` | Image and document upload | Upload features and the Cloudinary live tests |
| `Google:ClientId` | Verifying Google ID tokens; same client id as the frontend | Google sign-in |
| `Brevo:ApiKey` | Verification and password-reset emails | Sending emails: without it the API starts, but every email returns 503 `SERVICE_UNAVAILABLE` |
| `Mapbox:AccessToken` | Mapbox Directions (RM-14); the secret token, not the frontend's public `pk.*` token | Roadmap routes |
| `Ai:ApiKey` | OpenAI key of the ANGI project for the Agent and AI roadmap generation (RM-10); see [AI spending limit](agent.md#ai-spending-limit) | Optional: without it the Agent is unavailable and every day is scheduled by the job's own fallback |

Set each one once (stored outside the repo):

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=angi;Username=angi_backend;Password=backend_dev" --project ANGI.WebApi
dotnet user-secrets set "Jwt:SecretKey" "<at least 32 random characters>" --project ANGI.WebApi
dotnet user-secrets set "Recommendation:BaseUrl" "http://127.0.0.1:8000" --project ANGI.WebApi
dotnet user-secrets set "Recommendation:ApiKey" "<RECO_API_KEY of angi-reco>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:CloudName" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Cloudinary:ApiSecret" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Google:ClientId" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Brevo:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Mapbox:AccessToken" "<value>" --project ANGI.WebApi
dotnet user-secrets set "Ai:ApiKey" "<value>" --project ANGI.WebApi
dotnet user-secrets list --project ANGI.WebApi    # check what is set
```

Use the port and password from your `.env` in the connection string if you changed them. On Render the same keys are environment variables with `__` instead of `:` ([deploy](deploy.md)).

## Non-secret defaults

`appsettings.json` already holds:

- `Brevo:BaseUrl`, `Brevo:SenderName` (`ANGI`), `Brevo:TimeoutMilliseconds`.
- `Brevo:SenderEmail` (`angi.sep490.g71@gmail.com`), the team's verified Brevo sender. Override it only if your Brevo account uses another verified sender.
- `Frontend:BaseUrl` (`http://localhost:3000`), the base of the links in the emails (`/verify-email?token=...`, `/reset-password?token=...`). On Render set `Frontend__BaseUrl` to the Vercel URL.

## Checking the Brevo key

Send both emails to your own address (the live test is skipped unless both variables are set):

```bash
RUN_BREVO_LIVE_TESTS=1 BREVO_LIVE_TEST_TO=you@example.com dotnet test ANGI.IntegrationTests/ANGI.IntegrationTests.csproj --filter EmailServiceBrevoLiveTests
```
