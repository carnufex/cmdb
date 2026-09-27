import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Auth } from './auth';
import { authInterceptor } from './auth-interceptor';

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: Auth, useValue: { accessToken: () => 'secret-token' } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('adds the bearer token to API calls', () => {
    http.get('/api/me').subscribe();

    const req = backend.expectOne('/api/me');
    expect(req.request.headers.get('Authorization')).toBe('Bearer secret-token');
    req.flush({});
  });

  it('never sends the token to other origins', () => {
    http.get('https://tiles.example/1/2/3.png').subscribe();

    const req = backend.expectOne('https://tiles.example/1/2/3.png');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });
});
