# JobChat

Angular **16** + **.NET 10** job search and local browser application bot. Resume extraction, search, matching, browser login/form handling, recurring runs and notifications are implemented in C#.

## Run on Windows

Stop the old backend first (Ctrl+C, or Shift+F5 in Visual Studio), then from the repository root:

```cmd
git pull origin main
cd backend\JobChatBot.Api
dotnet restore
dotnet run --launch-profile bot
```

Swagger: **http://127.0.0.1:5080/swagger**.

In another terminal:

```cmd
cd C:\Users\akash\source\repos\JobChatBot\frontend
npm ci
npm start
```

Frontend: **http://127.0.0.1:4200**. Use the same hostname for frontend and Swagger if they should share a browser workspace.

The frontend was developed/tested with Node 18.20.8 and npm 10.8.2, as required by Angular 16. If you intentionally use your installed Node 24 instead, `npm ci --engine-strict=false` bypasses the version check; Node 24 compatibility with Angular 16 is not guaranteed.

Use **installed Microsoft Edge** on Windows. No Playwright browser download command is needed. `dotnet restore` installs the .NET client/driver library. A cloud/Linux backend cannot operate the Edge browser on your Windows machine.

## Start once → search → match → apply

1. **Resume & profile:** upload PDF/DOCX/TXT or paste text. Fill your name, email, target role/location, explicit first/last name and application phone number. The application phone is separate from WhatsApp. Review extracted text, then save. An uploaded original file is retained privately for attachment; editing extracted text stops automatic attachment of a mismatched file.
2. **Connections:** add a **Brave Search API key**. Optional LinkedIn/Naukri login email/password can be saved here, protected by .NET Data Protection. You can also log in directly in the separate Edge window and keep that session. Never paste passwords into chat.
3. For email/WhatsApp, add SendGrid/Meta settings and profile recipients. Check the pending queue and enable notification sending explicitly. The `bot` backend launch profile permits sending; `http` and `scripts/dev.sh` keep notification sending paused. Starting `bot` can send an already-authorized pending notification queue—review it first.
4. **Auto-apply bot:** choose minimum match score, maximum applications per run (1–10), and repeat interval (30–1440 minutes). Naukri native-profile apply has a separate opt-in because it uses the resume and answers saved **in your Naukri account**, not JobChat's uploaded file.
5. Check the explicit authorization for real submissions, then **Start Bot**. Each run searches indexed LinkedIn/Naukri listings, filters supported URLs/role/skill score/location text, skips previously attempted/applied listings, and processes eligible applications up to the limit. The backend must remain running; the frontend may be closed.
6. **Stop bot** prevents subsequent jobs. A Submit already in flight may still complete. Changing the resume or connection settings stops the bot and requires fresh authorization.

### What can run without per-job approval

For supported **English LinkedIn Easy Apply** dialogs, the agent fills only recognized fields from the saved profile and attaches a matching uploaded resume when supported. It advances recognized Next/Review buttons and submits once under the Start Bot authorization. It does not infer salary, years of experience, work authorization, demographic information, application consent or unknown answers.

For a recognized **Naukri native Apply** button, explicit Naukri opt-in permits applying with the account's existing profile/resume. Employer redirects, custom questionnaires and other layouts are not automated.

**This is best-effort browser automation, not a guaranteed unattended integration for every job.** Real LinkedIn/Naukri account flows have not been tested in this environment. Sites can change layouts, require verification or restrict automation. Credentials alone do not guarantee access. No official LinkedIn/Naukri submission API is configured. Do not use this as a public production service.

### Pauses and confirmation

- CAPTCHA, OTP, login problems, unknown required questions, unsupported layouts and uncertain confirmation **pause** the bot. Complete verification yourself in Edge; it is never bypassed.
- After resolving a known-field/login screen, Start Bot can resume the unfinished application under fresh authorization. If you changed your saved profile, close the old attempt and open it again.
- An attempt can also be opened manually from an opportunity card. **Continue**, **Approve & submit once**, **Verify confirmation** and **Close browser** are available under Application attempts when the bot is stopped.
- The final Submit transition is persisted before clicking. Concurrent/repeated requests cannot click Submit twice. Unknown results are never retried automatically.
- An application and two notification entries are created only after a supported confirmation is observed in the same job-site browser. This is browser evidence, **not a platform API receipt** or hiring outcome.
- For an uncertain outcome, check the original site and use Verify. If you personally verified submission, the existing **I submitted an application** action records a separately labeled user confirmation. Do not resubmit uncertain applications.
- If you checked the original site and verified that it was **not** submitted, use **I checked: not submitted** to clear the uncertain attempt. That job remains excluded from automatic retries; starting it again requires a deliberate manual Open action.
- Interrupted active runs are paused after a backend restart. Persistent site sessions can survive, but live pages/connections must not be assumed to survive.

Indexed search snippets may be stale or incomplete. Scores represent recognized keyword overlap, not hiring probability. A specified location must appear in the saved listing title/description/location before unattended application; listings with unknown location may be skipped. Job location/company are not invented when absent from provider results.

## Backend API / Swagger

Swagger is enabled only in Development. `dotnet run --no-launch-profile` may default to Production and hide `/swagger`. Use `--launch-profile http` for development with notification sending paused, or `--launch-profile bot` for explicitly enabled delivery plus local browser operation.

| API | Purpose |
| --- | --- |
| `GET /api/health`, `GET /api/workspace` | Readiness and private workspace state |
| `PUT /api/profile`, `POST /api/resume/extract` | Profile and resume upload/extraction |
| `GET/PUT/DELETE /api/connections` | Presence metadata / protected settings / removal |
| `POST /api/search`, `POST /api/chat` | Resume search and optional AI guidance |
| `GET /api/bot` | Current bot state and local browser capability |
| `POST /api/bot/start` | Authorize recurring bounded applications |
| `POST /api/bot/stop` | Stop subsequent applications |
| `GET /api/automation/capabilities` | Supported browser mode and live-verification status |
| `POST /api/jobs/{id}/apply` | Open a supported listing in the local Edge profile |
| `GET /api/application-attempts` | Attempt states and review details |
| `POST /api/application-attempts/{id}/continue` | Fill supported fields / resume current form |
| `POST /api/application-attempts/{id}/submit` | One-time reviewed submission with approval + token |
| `POST /api/application-attempts/{id}/verify` | Check visible confirmation without another submission |
| `POST /api/application-attempts/{id}/not-submitted` | Explicitly resolve an uncertain result after checking the site |
| `DELETE /api/application-attempts/{id}/browser` | Close that local application browser |
| `POST /api/jobs/{id}/confirm` | Record the person's own submission confirmation |

Start request example (uses the current browser cookie workspace):

```json
{
  "authorizeSubmission": true,
  "minimumMatchScore": 70,
  "maxApplicationsPerRun": 3,
  "intervalMinutes": 60,
  "allowNaukriProfileApply": false
}
```

Browser-launch/submission APIs accept only loopback requests. No bot is started simply by saving credentials or booting the backend. Explicit Start authorization is bound to the current profile and a unique authorization ID, so a stopped run cannot inherit authorization from a new run.

## Credentials and local storage

Different browser profiles/contexts use separate random HttpOnly SameSite=Strict cookie workspaces. This is **browser isolation, not authenticated user accounts**. A shared browser profile shares its workspace. Cookies last 30 days; clearing them loses workspace access. Cross-device login, managed authentication and a multi-user database remain unimplemented.

Private data is in `backend/JobChatBot.Api/App_Data/sessions/<opaque-id>/` (ignored by Git): profile/job JSON, retained resume file, application attempts and a separate persistent Edge profile per login identity. Credentials use workspace-specific ASP.NET Core Data Protection. APIs return configured/unverified flags, never secret values; successful saves clear credential fields in the UI. No keys/passwords are stored in browser localStorage/sessionStorage. Blank fields preserve saved values.

Preserve `App_Data/keys` together with encrypted settings in backups. Data Protection keys depend on filesystem protection; an administrator/compromised server can access them. Resume/job files and Edge session files are not encrypted by this application. The local data root is owner-only on Unix. Do not commit or share it, and do not run multiple API instances against the JSON store.

Legacy `App_Data/workspace.json` is preserved without exposing it to new browser workspaces.

Optional server defaults (UI per-workspace settings take precedence):

| Variable | Purpose |
| --- | --- |
| `LinkedIn__Email`, `LinkedIn__Password` | Optional fixed-origin LinkedIn browser login |
| `Naukri__Email`, `Naukri__Password` | Optional fixed-origin Naukri browser login |
| `Search__BraveApiKey` | Indexed search at `api.search.brave.com` |
| `Ai__ApiKey`, `Ai__Model` | Optional OpenAI-compatible guidance |
| `Ai__BaseUrl` | HTTPS API base, default `https://api.openai.com/v1` |
| `Notifications__SendGridApiKey`, `Notifications__FromEmail` | SendGrid at `api.sendgrid.com` with verified sender |
| `Notifications__WhatsAppToken`, `Notifications__WhatsAppPhoneId` | Meta Business API at `graph.facebook.com` |
| `Notifications__WhatsAppTemplate` | Approved template with job title, company and listing URL body parameters |
| `Notifications__WhatsAppLanguage` | Template language, default `en_US` |
| `Notifications__DisableSending` | Server-wide notification pause |
| `BrowserApply__Enabled` | Enable/disable Windows installed-Edge automation |
| `DataDirectory` | Optional absolute private data root |

The `http` launch profile and `scripts/dev.sh` force notification sending **paused**. The `bot` launch profile permits provider calls only when the workspace also opts in and has configured recipients/credentials. Use recipients who consent. Pending notifications are processed every ten seconds. `provider-accepted` is not final delivery; delivery webhooks and automatic retries are not implemented. AI chat sends resume text/matches to the configured provider, with dedicated email, WhatsApp and application phone fields removed; resume text may still contain personal information.

Cloud network settings must allow required provider destinations and job-site login/page dependencies. This cloud instance is not configured for live Windows site automation. No real keys/accounts were supplied and no live applications/messages were sent during development.

## Validate

```bash
dotnet build backend/JobChatBot.Api
python3 scripts/smoke.py
dotnet run --project backend/JobChatBot.Checks
# Optional offline browser fixtures, using an already installed browser:
BROWSER_TEST_EXECUTABLE=/usr/bin/chromium dotnet run --project backend/JobChatBot.Checks -- --browser
cd frontend
npm run build -- --configuration production
CHROME_BIN=/usr/bin/chromium npm test -- --watch=false --karma-config karma.conf.js
```

Tests use temporary workspaces, fake HTTP responses and locally fulfilled browser pages. They test search/credential isolation, uploads, Swagger, limits, authorization, pause/stop behavior, exact-once final clicks, changed-form rejection, receipt handling and notification creation. They **do not validate live account login, site compatibility, actual applications or provider delivery**.
