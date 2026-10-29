# JobChat

Angular **16** frontend and **.NET 10** backend, resume parser, search agent, AI client and notification worker.

## Workflow

1. Open **Resume & profile** and upload PDF/DOCX/TXT/Markdown, or paste resume text. Review extracted text, add the person's name, contacts, preferred role/location, then **Save & find matching jobs**. Text PDFs up to 20 pages and files up to 2 MB are supported; scanned PDFs need manual text. Files are parsed in .NET and discarded; extracted text is saved only when the profile is saved.
2. The .NET agent uses that workspace's saved resume skills, role and location. A missing role is inferred from recognized skills when possible; otherwise enter it manually. It creates personalized LinkedIn/Naukri search links without any credentials.
3. In **Connections**, add a **Brave Search API key** to retrieve indexed LinkedIn/Naukri listings. The app sends the role/skill/location query, not the full resume or contact fields, to Brave. Direct listing URLs are validated, deduplicated, saved and ranked. Listings may be outdated; scores based on snippets are explicitly labeled. Company names are not invented when the provider doesn't supply them. Manual job imports remain available.
4. In chat, **Find jobs** or a message such as “Find jobs for my resume” invokes the .NET search agent. Other questions use an optional OpenAI-compatible AI provider, or an honest local guide when AI is not configured.
5. Apply on the original website, then confirm submission on the saved job card. No application is recorded merely from clicking its URL. Duplicate confirmations do not create duplicate notifications.
6. Add SendGrid / WhatsApp credentials and contacts; review the pending queue before enabling notification sending. Sending defaults **off**. The development launcher also enforces a server-wide sending pause. Provider acceptance is distinguished from final delivery.

**Not implemented:** LinkedIn/Naukri automated form submission, direct platform account/API integration, verified application-status synchronization, delivery webhooks, OCR, user login, cross-device accounts or production multi-user access controls. Providing credentials in Connections does not enable automatic job applications. The `/apply` endpoint returns 409 rather than inventing success.

## Workspace and credential isolation

An opaque, random 256-bit **HttpOnly, SameSite=Strict** cookie identifies each browser workspace. Different browser contexts get different resumes, jobs, settings, applications and notifications. Reloading and server restart retain the same browser's data. Cookies expire after 30 days; clearing cookies loses access to that workspace. This is browser isolation, **not authenticated accounts**. People sharing a browser profile share a workspace. Use separate browser profiles/contexts for different candidates until account authentication is implemented.

Workspace files live under `backend/JobChatBot.Api/App_Data/sessions/<opaque-id>/` and are ignored by Git. Credentials entered in Connections use **ASP.NET Core Data Protection** with a workspace-specific purpose. APIs return presence metadata only, never saved values. UI fields are cleared after a successful save and are not persisted in localStorage/sessionStorage. Blank fields preserve saved settings; **Remove saved settings** clears that workspace's settings and disables sending. Server environment defaults may remain available.

Data Protection keys are in `App_Data/keys`; preserve them in backups together with encrypted settings. This is local server protection, not protection against a compromised server/admin. Resume and job JSON are not encrypted. The data root has owner-only permissions on Unix. Old `App_Data/workspace.json`, if present from an earlier version, is preserved but not automatically assigned to a new browser; it is never exposed to other candidates.

**Development only:** the service binds to loopback. Do not publish it as a public multi-user service without authentication, authorization, HTTPS, quotas/rate limits and managed data/key storage. Do not run multiple API instances against the JSON store.

## Run

Tools: .NET SDK 10.0.403, Angular CLI 16.2.16, Node 18.20.8, npm 10.8.2. Angular 16 and Node 18 are end of support; retained at the user's request.

```bash
cd /workspace/JobChatBot
. /workspace/.setup/activate.sh
cd frontend && npm ci
cd ..
bash scripts/dev.sh
```

**Development launcher safety:** `scripts/dev.sh` sets `Notifications__DisableSending=true`, so no email/WhatsApp messages are sent even if workspace credentials are saved and sending is enabled. Real delivery requires a separately started backend without this pause, explicit workspace opt-in, valid providers/recipients and appropriate network access.

API: internal loopback port **5080**. Frontend: **4200**, proxying `/api` to the API. `dev.sh` stops the API it starts when the frontend exits. Stop only listeners you own before restarting. Outside the cloud environment, install the pinned prerequisites; the activation file is optional.

## Validate

```bash
dotnet build backend/JobChatBot.Api
python3 scripts/smoke.py
dotnet run --project backend/JobChatBot.Checks
cd frontend
npm run build -- --configuration production
CHROME_BIN=/usr/bin/chromium npm test -- --watch=false --karma-config karma.conf.js
```

The HTTP suite uses temporary data and cookie jars to check isolation, profile matching, uploads, secret non-disclosure/protected storage, validation, duplicate prevention and restart persistence. Provider/agent checks use fake HTTP responses to verify provider request construction, URL filtering, deduplication, error handling, AI responses and contact exclusion. They do not submit jobs, send messages or make live provider calls. Browser tests exercise rendering, confirmation, search states and credential form clearing.

## Optional configuration

Use **Connections** for per-browser credential settings. Placeholders are examples only. Configured status indicates field presence, not verified provider authorization. Keys are transmitted to the .NET server; use HTTPS for any non-loopback deployment.

Environment variables or .NET user-secrets provide optional server defaults:

| Variable | Purpose / destination |
| --- | --- |
| `Search__BraveApiKey` | Brave Search API key; `api.search.brave.com` |
| `Ai__ApiKey` | OpenAI-compatible chat provider key |
| `Ai__Model` | Provider's model ID |
| `Ai__BaseUrl` | HTTPS API base, default `https://api.openai.com/v1` |
| `Notifications__SendGridApiKey` | SendGrid; `api.sendgrid.com` |
| `Notifications__FromEmail` | Verified sender email |
| `Notifications__WhatsAppToken` | Meta WhatsApp Business token; `graph.facebook.com` |
| `Notifications__WhatsAppPhoneId` | Business phone-number ID |
| `Notifications__WhatsAppTemplate` | Approved template with three body parameters: job title, company, listing URL |
| `Notifications__WhatsAppLanguage` | Template language, default `en_US` |
| `Notifications__DisableSending` | Server-wide pause; `dev.sh` forces `true` |
| `DataDirectory` | Optional absolute data root |

Cloud network settings must allow the destinations actually used, including the selected AI provider. The environment draft includes api.search.brave.com, api.openai.com, api.sendgrid.com and graph.facebook.com. Review/save and publish environment settings to apply the draft; live access is not verified. No real credentials were supplied. Never commit credentials or paste them into chat. No LinkedIn/Naukri passwords are requested or stored.

When a workspace enables sending, its pending and `awaiting-configuration` notifications are processed every ten seconds using its own settings and recipient profile. Only send to recipients who consent. `provider-accepted` is not delivery confirmation. Ambiguous/failed sends are not automatically retried, avoiding duplicates. A crash during sending leaves `sending`; investigate the provider before recovery. Retry UI and delivery webhooks are not implemented.

AI chat sends resume text and saved matches to the configured provider. Dedicated contact fields are excluded, but resume text itself may contain personal information. AI cannot submit applications or send messages. Actual provider access, live search quality and message delivery require real keys and remain unverified in this development environment.
