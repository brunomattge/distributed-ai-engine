import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from './app';
import { appRoutes } from './app.routes';

describe('Application routes', () => {
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    http.verify({ ignoreCancelled: true });
  });

  it('navigates between the cluster overview and the agent chat in the shared shell', async () => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(appRoutes), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/cluster');
    http.expectOne('/api/nodes').flush([]);
    http.expectOne('/api/inference-status').flush({ available: true, state: 'ready', coordinatorState: 'ready', activeRpcWorkers: 3, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M', message: null });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('h1')?.textContent).toContain('Cluster overview');

    await router.navigateByUrl('/chat');
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush({ available: true, state: 'ready', coordinatorState: 'ready', activeRpcWorkers: 3, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M', message: null });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('h1')?.textContent).toContain('Agent chat');

    const navigation = fixture.nativeElement as HTMLElement;
    expect(navigation.querySelector('.app-shell > .route-outlet app-chat-page')).not.toBeNull();
    expect(navigation.querySelectorAll('.sidebar .nav-item')).toHaveLength(2);
    expect(navigation.querySelector('.sidebar .nav-item.active')?.textContent).toContain('Agent chat');
  });
});
