import { TestBed, fakeAsync, flushMicrotasks } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { AppComponent } from './app.component';

describe('JobChat workspace', () => {
  let http: HttpTestingController;
  const empty = { profile: { name: '', email: '', whatsApp: '', resumeText: '', desiredRole: '', location: '' }, matches: [], applications: [], notifications: [], integrations: { ai: false, email: false, whatsApp: false, linkedIn: false, naukri: false } };
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AppComponent, HttpClientTestingModule] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  it('shows the empty workspace without inventing jobs or an AI connection', async () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    http.expectOne('/api/workspace').flush(empty);
    await fixture.whenStable(); fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Good things start with a first step.');
    expect(page.textContent).toContain('Local .NET guide');
    expect(page.querySelectorAll('.job-card').length).toBe(0);
  });
  it('shows API errors instead of claiming a profile was saved', async () => {
    const component = TestBed.createComponent(AppComponent).componentInstance;
    const saving = component.saveProfile();
    http.expectOne('/api/profile').flush({ error: 'Enter a valid email address.' }, { status: 400, statusText: 'Bad Request' });
    await saving;
    expect(component.error).toBe('Enter a valid email address.');
    expect(component.success).toBe('');
  });
  it('does not record a submission when the confirmation is declined', async () => {
    spyOn(window, 'confirm').and.returnValue(false);
    const component = TestBed.createComponent(AppComponent).componentInstance;
    await component.confirm({ id: '1', title: 'Developer', company: 'Acme', location: '', url: 'https://example.com', description: '' });
    http.expectNone('/api/jobs/1/confirm');
  });
  it('displays the response mode returned by the backend', async () => {
    const component = TestBed.createComponent(AppComponent).componentInstance;
    component.message = 'Help me find a match';
    const sending = component.sendMessage();
    http.expectOne('/api/chat').flush({ reply: 'No jobs have been imported.', mode: 'local-guide' });
    await sending;
    expect(component.messages[component.messages.length - 1].mode).toBe('local-guide');
    expect(component.chatBusy).toBeFalse();
  });
});

describe('Personalized search and connection setup', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AppComponent, HttpClientTestingModule] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  it('clears credential inputs after saving and reloads presence metadata only', fakeAsync(() => {
    const component = TestBed.createComponent(AppComponent).componentInstance;
    component.credentialValues = { 'Search:BraveApiKey': 'test-placeholder-only' };
    const saving = component.saveConnections();
    const req = http.expectOne('/api/connections');
    expect(req.request.body.enableNotifications).toBeFalse();
    req.flush({});
    flushMicrotasks();
    http.expectOne('/api/connections').flush({ fields: [{key:'Search:BraveApiKey',configured:true}], enableNotifications:false });
    flushMicrotasks();
    http.expectOne('/api/workspace').flush({profile:{},matches:[],applications:[],notifications:[],integrations:{}});
    flushMicrotasks();
    expect(component.credentialValues).toEqual({});
    expect(component.configured['Search:BraveApiKey']).toBeTrue();
  }));
  it('shows missing search credentials without inventing matches', fakeAsync(() => {
    const component = TestBed.createComponent(AppComponent).componentInstance;
    const searching = component.searchJobs();
    http.expectOne('/api/search').flush({status:'needs-credentials',added:0,message:'Add your search key',plan:{role:'Python Developer'}});
    flushMicrotasks();
    http.expectOne('/api/workspace').flush({profile:{},matches:[],applications:[],notifications:[],integrations:{}});
    flushMicrotasks();
    expect(component.searchResult?.status).toBe('needs-credentials');
    expect(component.data.matches.length).toBe(0);
  }));
  it('uses the saved server plan for search links rather than a fixed developer role', () => {
    const component = TestBed.createComponent(AppComponent).componentInstance;
    component.data.plan = {role:'Python Developer',location:'Mumbai',skills:['Python'],query:'Python Mumbai',linkedInUrl:'https://www.linkedin.com/jobs/search/?keywords=Python',naukriUrl:'https://www.naukri.com/python-developer-jobs-in-mumbai'};
    expect(component.searchUrl('linkedin')).toContain('Python');
    expect(component.searchUrl('naukri')).toContain('mumbai');
  });
});
