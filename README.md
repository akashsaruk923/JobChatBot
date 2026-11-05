# JobChat

Angular 16 + .NET 10 application for **Naukri search/apply in a visible local Edge browser** and **experience-matched public contact research from LinkedIn openings**. This workflow needs no API keys. LinkedIn apply/submit requests are disabled.

## Standalone NaukriTool: no API server or Angular needed

For Naukri-only automation, close any old tool instance and run from the repository:

```cmd
git pull origin main
scripts\naukri-tool.cmd
```

Requires Windows, installed Edge and .NET SDK 10.0.403. Node/npm, API keys and the JobChat webpage are not needed. First launch asks for exact profile name, Naukri account email, local PDF/DOCX resume path (up to 2 MB), target role, location and experience. It saves private configuration in ignored `.NaukriTool/config.json`. This directory also holds the separate Edge session, applications and lock file; do not commit or share it. Use one account per directory (`--data-dir <absolute path>` selects another).

Review the displayed plan and type `START` to authorize this launch. Sign in manually in the visible Edge window, verify your saved Naukri profile/answers and press Enter in the terminal. No password is stored by the tool. It immediately uploads your configured resume, saves the exact configured name if different (otherwise verifies it), and searches/applies to up to **3** eligible native Naukri jobs. It repeats the resume/name check and bounded application runs every **30 minutes** while the PC, terminal and Internet connection remain active. The same visible Edge session is reused. Notifications and LinkedIn research are not part of this standalone tool.

The upload needs a fresh success signal plus name/filename verification after profile reload. It does not prove Naukri's ranking changed. Unknown layouts, login challenges, questions or unconfirmed actions pause the tool. Scheduled updates are deferred during an active application, and paused cycles do not silently restart. `RESUME` explicitly continues after you resolve a page; `SKIP` closes an unfinished pre-submit attempt. `Ctrl+C` stops. Uncertain submissions require personally checking Naukri history and choosing `CONFIRMED-APPLIED` or `NOT-SUBMITTED`; the attempted job is never automatically retried. Interrupted in-flight submissions become uncertain on restart.

To change the resume/name or limits, stop the tool, edit `.NaukriTool/config.json`, then restart and review the plan. `MaxApplicationsPerCycle` accepts 1–10; `MinimumMatchScore` accepts 0–100. The interval is fixed at 30 minutes. A resume file changed mid-session requires restart/review. A private state lock prevents two instances from clicking or uploading concurrently.

If paused on an unsupported page, `.NaukriTool/last-layout.json` captures tag/ID/class/type structure only—no field values, passwords, cookies or page text. It can help diagnose selector changes; it is not a complete page capture. `scripts\naukri-tool.cmd --help` prints usage. The Windows launcher and live account flows remain unverified here; browser fixture tests exercise profile writes/reloads, repeat uploads and Naukri search-to-apply, not actual Naukri compatibility.

## Start on Windows

Prerequisites: installed Microsoft Edge, .NET SDK **10.0.403** (see `global.json`), Node **18.20.8** and npm **10.8.2**. No Playwright browser download is required for installed Edge.

Close old JobChat API/frontend terminals first. From the repository root:

```cmd
git pull origin main
scripts\start-windows.cmd
```

The helper builds the backend, installs frontend dependencies if absent and opens API/frontend terminals. Wait for both services, then open `http://127.0.0.1:4200`. Swagger is available at `http://127.0.0.1:5080/swagger`. Use the same hostname consistently to keep the same cookie workspace.

The Windows helper itself has not been executed in the Linux development environment. Its underlying build/start commands are repository-supported. Manual equivalent:

```cmd
cd backend\JobChatBot.Api
dotnet run --launch-profile http
```

In a second terminal:

```cmd
cd frontend
npm ci
npm start
```

The `http` launch profile pauses notification sending. Server startup and profile saving do not authorize any application submissions.

## Naukri search and applications

1. Save resume, name, valid application email, target role, location and **Total experience (years)**, for example `4`. Application phone should include the country code. WhatsApp is optional.
2. **Find jobs → Search Naukri in browser** opens the real Naukri website visibly in a separate persistent Edge profile. Sign in on the website if needed. Complete OTP/CAPTCHA yourself, then retry Search. Existing sessions persist across restarts. Optional Naukri login settings are protected on the local server; storing a password in JobChat is not required.
3. Search reads up to 25 visible cards. Only explicit experience ranges containing the saved experience are imported. For four years, a `3-5 Yrs` listing matches; `8-10 Yrs` and missing ranges do not. The current adapter supports `.srp-jobtuple-wrapper` and `article.jobTuple` cards. A changed/unsupported page stops with a message rather than fabricated jobs.
4. Review the resume and answers saved **IN YOUR NAUKRI ACCOUNT**. Native Naukri Apply uses those, not JobChat's uploaded file. Use **Apply on Naukri** for reviewed apply, then Continue and Approve & submit once. The Naukri page stays visible in Edge.
5. For recurring auto-apply, open **Naukri auto-apply**, choose limits and enable the Naukri-profile opt-in and real-submission authorization. Start Bot searches and applies only to eligible Naukri jobs. The backend must remain running; the frontend can close. Stop prevents subsequent jobs; a click already in flight can finish.

Unknown questions, CAPTCHA/OTP, employer redirects, changed forms and uncertain outcomes pause automation. Salary, eligibility and other missing answers are not invented. Success is recorded only after a supported site confirmation is observed. An uncertain submission is never automatically retried. An active attempt's browser cannot be replaced by a new search. Interrupted runs pause after a backend restart. Profile/login changes require fresh review/authorization.

A skill-overlap score is not a hiring probability. Location text and role keywords also filter automatic candidates. Browser behavior is best effort; site changes can break selectors.

## LinkedIn public contacts

Open **HR contacts → Find published contacts**. The backend searches public LinkedIn guest listings and checks up to ten public job descriptions without an API key. It saves this structure privately:

| Company | Published email | Job title | Experience evidence | Source opening | Collected time |
| --- | --- | --- | --- | --- | --- |
| From listing | Explicitly published in description | From listing | e.g. 3–5 years | Original listing URL | Timestamp |

A single explicit experience range must contain the candidate's saved years. Missing/ambiguous experience, missing company or absent email produce no contact record. No guessed emails, private recruiter profiles, email delivery or LinkedIn submissions are involved. Published addresses are **not verified HR identities**, deliverability checks or proof an opening remains active. Contacts persist across backend restarts and deduplicate by source/email. This adapter reads LinkedIn job descriptions, not arbitrary employer websites.

LinkedIn may restrict public access. Regional links such as `in.linkedin.com` normalize to the canonical supported origin. Cloud research requires allowed network access to `www.linkedin.com`; Naukri browsing occurs on the user's Windows backend.

## Storage and API

Browser cookie workspaces are isolated; they are not authenticated accounts. Clearing cookies loses access to that browser workspace. This is a local application, not a public multi-user service. Private data lives in ignored `backend/JobChatBot.Api/App_Data/sessions/<id>/`: profile/jobs/contacts JSON, retained resume, encrypted connection settings and Edge session files. Preserve `App_Data/keys` with encrypted settings in backups. Resume/JSON/browser files are not encrypted by this application. Do not commit private data or run duplicate API instances against the same store.

`DataDirectory` overrides the private data root. `BrowserApply__Enabled=false` disables local browser automation. Existing optional AI/notification provider settings remain compatible, but are unnecessary for the current workflow and their API-key inputs are omitted from Connections. Notification sending remains paused in the standard development profile. No real messages should be sent during setup.

Useful endpoints: `GET /api/health`, `GET /api/workspace`, `PUT /api/profile`, `POST /api/resume/extract`, `POST /api/search`, `POST /api/hr-contacts/search`, `GET /api/hr-contacts`, `POST /api/bot/start`, `POST /api/bot/stop`, `POST /api/jobs/{id}/apply`, and application-attempt Continue/Submit/Verify/Close endpoints. Browser operations require loopback access. Swagger is Development-only.

## Validation and limits

```bash
dotnet build backend/JobChatBot.Api
python3 scripts/smoke.py
BROWSER_TEST_EXECUTABLE=/usr/bin/chromium dotnet run --project backend/JobChatBot.Checks -- --browser
cd frontend
npm run build -- --configuration production
CHROME_BIN=/usr/bin/chromium npm test -- --watch=false --karma-config karma.conf.js
```

Linux cloud supports build/testing and public contact research, but cannot open the user's Windows Edge browser. `scripts/dev.sh` starts cloud development servers with notification delivery paused.

Offline browser fixtures exercise Naukri search-to-apply, four-year experience filtering, active-attempt preservation, recognized confirmation and single final clicks. Other checks cover authorization/stop behavior, uploads, workspace isolation, contact evidence, missing email/experience, persistence and deduplication. The supplied public LinkedIn response also parsed all ten listing cards.

**Real Naukri login, live-page selectors, live applications and real LinkedIn contact lookup have not been verified in this Linux environment.** Offline tests establish adapter behavior against fixtures, not current compatibility with an authenticated account. No real application or message was sent during development. If Naukri shows an unsupported layout, inspect that page in the visible Edge window; do not assume the bot submitted anything.
