import { Component, OnInit, NgZone } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

interface Profile { name: string; email: string; whatsApp: string; resumeText: string; desiredRole: string; location: string; firstName?: string; lastName?: string; phoneNumber?: string; }
interface Job { id: string; title: string; company: string; location: string; url: string; description: string; source?: string; descriptionKind?: string; }
interface Match { job: Job; score: number; matchedSkills: string[]; missingSkills: string[]; }
interface Application { id: string; jobId: string; status: string; confirmedAt: string; }
interface Notice { id: string; applicationId: string; channel: string; status: string; detail: string; }
interface SearchPlan { role: string; location: string; skills: string[]; query: string; linkedInUrl: string; naukriUrl: string; }
interface SearchResult { status: string; plan: SearchPlan; added: number; message: string; }
interface Attempt { id: string; jobId: string; status: string; detail: string; reviewToken?: string; fields?: {label:string;value:string}[]; }
interface BotState { enabled: boolean; status: string; detail: string; submittedThisRun: number; nextRunAt?: string; }
interface Workspace { bot?: BotState; applicationAttempts?: Attempt[]; plan?: SearchPlan; profile: Profile; matches: Match[]; applications: Application[]; notifications: Notice[]; integrations: { ai: boolean; email: boolean; whatsApp: boolean; linkedIn: boolean; naukri: boolean; search?: boolean; notificationsEnabled?: boolean; sendingPaused?: boolean; }; }
@Component({ selector: 'app-root', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './app.component.html', styleUrls: ['./app.component.css'] })
export class AppComponent implements OnInit {
  tab = 'overview'; busy = false; error = ''; success = ''; loading = true;
  profile: Profile = { name: '', email: '', whatsApp: '', resumeText: '', desiredRole: '', location: '' };
  data: Workspace = { profile: this.profile, matches: [], applications: [], notifications: [], integrations: { ai: false, email: false, whatsApp: false, linkedIn: false, naukri: false } };
  draft = { title: '', company: '', location: '', url: '', description: '' };
  botPolicy = { authorizeSubmission: false, minimumMatchScore: 70, maxApplicationsPerRun: 3, intervalMinutes: 60, allowNaukriProfileApply: false };
  botTimer?: ReturnType<typeof setInterval>;
  searchBusy = false; searchResult: SearchResult | null = null; uploading = false;
  credentialValues: Record<string, string> = {}; configured: Record<string, boolean> = {}; enableNotifications = false;
  credentialFields = [
    { key: 'LinkedIn:Email', label: 'LinkedIn login email', placeholder: 'Your LinkedIn sign-in email', secret: false, group: 'Job-site login', note: 'Used only in the local installed Edge browser. OTP/CAPTCHA pauses the bot.' },
    { key: 'LinkedIn:Password', label: 'LinkedIn password', placeholder: 'Your LinkedIn password (optional with saved session)', secret: true, group: 'Job-site login', note: 'Protected on the local backend. Never paste passwords into chat.' },
    { key: 'Naukri:Email', label: 'Naukri login email', placeholder: 'Your Naukri sign-in email', secret: false, group: 'Job-site login', note: 'Naukri native apply uses the resume/profile saved in your Naukri account.' },
    { key: 'Naukri:Password', label: 'Naukri password', placeholder: 'Your Naukri password (optional with saved session)', secret: true, group: 'Job-site login', note: 'Existing Edge sessions can be used instead. Unknown forms require your action.' },
    { key: 'Search:BraveApiKey', label: 'Brave Search API key', placeholder: 'Enter your Brave Search API key', secret: true, group: 'Job search', note: 'Retrieves indexed LinkedIn / Naukri listings. Destination: api.search.brave.com.' },
    { key: 'Ai:ApiKey', label: 'AI provider API key', placeholder: 'Enter your AI provider API key', secret: true, group: 'AI assistant', note: 'Used by the .NET AI agent. Resume text is shared with this provider for chat.' },
    { key: 'Ai:Model', label: 'AI model', placeholder: 'Your provider model ID', secret: false, group: 'AI assistant', note: '' },
    { key: 'Ai:BaseUrl', label: 'AI API base URL', placeholder: 'https://api.openai.com/v1', secret: false, group: 'AI assistant', note: 'Optional. HTTPS OpenAI-compatible API base.' },
    { key: 'Notifications:SendGridApiKey', label: 'SendGrid API key', placeholder: 'SG.…', secret: true, group: 'Email', note: 'Destination: api.sendgrid.com.' },
    { key: 'Notifications:FromEmail', label: 'Verified sender email', placeholder: 'jobs@yourdomain.com', secret: false, group: 'Email', note: 'Recipient is taken from this workspace’s resume profile.' },
    { key: 'Notifications:WhatsAppToken', label: 'WhatsApp Business access token', placeholder: 'Enter your Meta access token', secret: true, group: 'WhatsApp', note: 'Destination: graph.facebook.com.' },
    { key: 'Notifications:WhatsAppPhoneId', label: 'WhatsApp Business phone ID', placeholder: 'Your business phone-number ID', secret: false, group: 'WhatsApp', note: '' },
    { key: 'Notifications:WhatsAppTemplate', label: 'Approved WhatsApp template', placeholder: 'job_application_recorded', secret: false, group: 'WhatsApp', note: 'Three body parameters: job title, company, listing URL. Recipient must opt in.' },
    { key: 'Notifications:WhatsAppLanguage', label: 'Template language', placeholder: 'en_US', secret: false, group: 'WhatsApp', note: '' }
  ];
  message = ''; chatBusy = false;
  messages = [{ role: 'assistant', text: 'Hi! Save your resume, add a job listing, and I’ll help compare your skills. Save your resume to build a personalized search. Search needs a provider key. Start Bot can authorize bounded local browser applications; challenges pause it.', mode: 'local-guide' }];
  constructor(private http: HttpClient, private zone: NgZone) {}
  async ngOnInit() { await this.refresh(true); this.zone.runOutsideAngular(() => { this.botTimer = setInterval(() => { if (this.tab === 'bot' || this.data.bot?.enabled) this.zone.run(() => this.refresh()); }, 5000); }); }
  ngOnDestroy() { if (this.botTimer) clearInterval(this.botTimer); }
  async refresh(initial = false) {
    try { this.data = await firstValueFrom(this.http.get<Workspace>('/api/workspace')); if (initial) this.profile = { ...this.data.profile }; }
    catch (e) { this.showError(e); } finally { this.loading = false; }
  }
  showError(e: unknown) { this.error = e instanceof HttpErrorResponse ? e.error?.error || 'Cannot reach the API. Check that the .NET service is running.' : 'Something went wrong. Please try again.'; }
  async saveProfile() {
    this.busy = true; this.error = ''; this.success = '';
    try { await firstValueFrom(this.http.put('/api/profile', this.profile)); await this.refresh(); this.success = 'Resume saved. Your personalized search is ready.'; this.tab = 'search'; await this.searchJobs(); }
    catch (e) { this.showError(e); } finally { this.busy = false; }
  }
  async importJob() {
    this.busy = true; this.error = ''; this.success = '';
    try { await firstValueFrom(this.http.post('/api/jobs', this.draft)); this.draft = { title: '', company: '', location: '', url: '', description: '' }; await this.refresh(); this.success = 'Listing saved and compared with your resume.'; this.tab = 'overview'; }
    catch (e) { this.showError(e); } finally { this.busy = false; }
  }
  async confirm(job: Job) {
    if (!window.confirm(`Have you submitted your application for ${job.title} at ${job.company} on the original website? This records your confirmation and queues notifications.`)) return;
    this.busy = true; this.error = ''; this.success = '';
    try { await firstValueFrom(this.http.post(`/api/jobs/${job.id}/confirm`, { submittedByMe: true })); await this.refresh(); this.success = 'Your submission was recorded. Check notification status in Applications.'; }
    catch (e) { this.showError(e); } finally { this.busy = false; }
  }
  async sendMessage() {
    const text = this.message.trim(); if (!text || this.chatBusy) return;
    this.message = ''; this.chatBusy = true; this.messages.push({ role: 'user', text, mode: '' });
    try { const result = await firstValueFrom(this.http.post<{ reply: string; mode: string; search?: SearchResult }>('/api/chat', { message: text })); this.messages.push({ role: 'assistant', text: result.reply, mode: result.mode }); if (result.search) { this.searchResult = result.search; await this.refresh(); this.tab = 'search'; } }
    catch (e) { this.showError(e); this.messages.push({ role: 'assistant', text: 'Could not get a response. Please try again.', mode: 'error' }); }
    finally { this.chatBusy = false; }
  }
  async startBot() {
    if (!window.confirm('Start Bot can submit real applications using your saved profile, policy and job-site sessions. CAPTCHA, missing answers and uncertain results pause it. Continue?')) return;
    this.busy = true; this.error = ''; this.success = '';
    try { await firstValueFrom(this.http.post('/api/bot/start', this.botPolicy)); await this.refresh(); this.success = 'Bot started. Keep this Windows backend and Edge running. Check status below.'; }
    catch(e) { this.showError(e); } finally { this.busy = false; }
  }
  async stopBot() {
    try { await firstValueFrom(this.http.post('/api/bot/stop', {})); await this.refresh(); }
    catch(e) { this.showError(e); }
  }
  async openApply(job: Job) {
    this.busy = true; this.error = '';
    try { await firstValueFrom(this.http.post(`/api/jobs/${job.id}/apply`, {})); this.tab = 'bot'; await this.refresh(); }
    catch(e) { this.showError(e); } finally { this.busy = false; }
  }
  async attemptAction(attempt: Attempt, action: string) {
    if (action === 'submit' && !window.confirm('Submit this reviewed application once? This sends a real application to the job site.')) return;
    if (action === 'not-submitted' && !window.confirm('Have you checked the original site’s application history and verified that no application was submitted? This clears the block but never retries automatically.')) return;
    this.busy = true; this.error = '';
    try {
      if (action === 'close') await firstValueFrom(this.http.delete(`/api/application-attempts/${attempt.id}/browser`));
      else await firstValueFrom(this.http.post(`/api/application-attempts/${attempt.id}/${action}`, action === 'submit' ? { approved:true, reviewToken:attempt.reviewToken } : action === 'not-submitted' ? {checkedOriginalSite:true} : {}));
      await this.refresh();
    } catch(e) { this.showError(e); } finally { this.busy = false; }
  }
  applied(id: string) { return this.data.applications.some(a => a.jobId === id); }
  jobFor(id: string) { return this.data.matches.find(m => m.job.id === id)?.job; }
  notices(id: string) { return this.data.notifications.filter(n => n.applicationId === id); }
  searchUrl(source: string) {
    return source === 'linkedin' ? this.data.plan?.linkedInUrl || 'https://www.linkedin.com/jobs/' : this.data.plan?.naukriUrl || 'https://www.naukri.com/';
  }
  async searchJobs() {
    this.searchBusy = true; this.error = '';
    try { this.searchResult = await firstValueFrom(this.http.post<SearchResult>('/api/search', {})); await this.refresh(); }
    catch (e) { this.showError(e); } finally { this.searchBusy = false; }
  }
  async openConnections() {
    this.tab = 'connections'; this.error = ''; this.success = ''; this.credentialValues = {};
    try { const result = await firstValueFrom(this.http.get<{ fields: {key:string;configured:boolean}[]; enableNotifications:boolean }>('/api/connections')); this.configured = Object.fromEntries(result.fields.map(f => [f.key, f.configured])); this.enableNotifications = result.enableNotifications; }
    catch (e) { this.showError(e); }
  }
  async saveConnections() {
    this.busy = true; this.error = ''; this.success = '';
    try {
      await firstValueFrom(this.http.put('/api/connections', { values: this.credentialValues, enableNotifications: this.enableNotifications }));
      this.credentialValues = {}; await this.openConnections(); await this.refresh(); this.success = 'Connection settings saved securely on the server. Values are never returned to the browser. Provider access has not been verified.';
    } catch(e) { this.showError(e); } finally { this.busy = false; }
  }
  async clearConnections() {
    if (!window.confirm('Remove this workspace’s saved connection settings? Notification sending will be disabled. Server defaults, if configured, remain available.')) return;
    this.busy = true;
    try { await firstValueFrom(this.http.delete('/api/connections')); await this.openConnections(); await this.refresh(); this.success = 'Workspace connection settings removed.'; }
    catch(e) { this.showError(e); } finally { this.busy = false; }
  }
  async uploadResume(event: Event) {
    const input = event.target as HTMLInputElement; const file = input.files?.[0]; if (!file) return;
    if (file.size > 2 * 1024 * 1024) { this.error = 'Please use a resume file up to 2 MB.'; input.value = ''; return; }
    this.uploading = true; this.error = '';
    try { const form = new FormData(); form.append('file', file); const result = await firstValueFrom(this.http.post<{ text: string }>('/api/resume/extract', form)); this.profile.resumeText = result.text; this.success = 'Resume text extracted. Review it and save your profile to search.'; }
    catch(e) { this.showError(e); } finally { this.uploading = false; input.value = ''; }
  }
  get strongMatches() { return this.data.matches.filter(m => m.score >= 70).length; }
  trackJob(_: number, match: Match) { return match.job.id; }
}
