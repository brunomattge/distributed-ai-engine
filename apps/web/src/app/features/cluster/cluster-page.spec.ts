import { registerLocaleData } from '@angular/common';
import localeEn from '@angular/common/locales/en';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ClusterPage } from './cluster-page';

describe('ClusterPage', () => {
  let http: HttpTestingController;

  registerLocaleData(localeEn, 'en-US');

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ClusterPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: LOCALE_ID, useValue: 'en-US' }],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    for (const request of http.match('/api/inference-status')) {
      request.flush({ available: true, state: 'ready', coordinatorState: 'ready', activeRpcWorkers: 3, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M', message: null });
    }
    http.verify();
  });

  it('renders registered nodes and labels simulator telemetry clearly', () => {
    const fixture = TestBed.createComponent(ClusterPage);
    fixture.detectChanges();
    http.expectOne('/api/nodes').flush([
      {
        nodeId: 'sim-a07-1',
        name: 'Galaxy A07 · Simulator 1',
        hardwareModel: 'Samsung Galaxy A07 (simulated)',
        registeredAtUtc: '2026-11-14T15:00:00Z',
        lastHeartbeatAtUtc: '2026-11-14T15:00:02Z',
        health: 'Online',
        metrics: { cpuPercent: 42.5, availableRamMiB: 1700, temperatureCelsius: 34.2, latencyMs: 18 },
      },
      {
        nodeId: 'sim-a07-2',
        name: 'Galaxy A07 · Simulator 2',
        hardwareModel: 'Samsung Galaxy A07 (simulated)',
        registeredAtUtc: '2026-11-14T15:00:00Z',
        lastHeartbeatAtUtc: null,
        health: 'Offline',
        metrics: null,
      },
    ]);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelectorAll('.node-card')).toHaveLength(2);
    expect(page.querySelector('.summary-value')?.textContent).toContain('2');
    expect(page.textContent).toContain('Simulation environment');
    expect(page.textContent).toContain('42.5%');
    expect(page.textContent).toContain('Offline');
  });


  it('distinguishes chat availability from per-device model readiness', () => {
    const fixture = TestBed.createComponent(ClusterPage);
    fixture.detectChanges();
    http.expectOne('/api/nodes').flush([1, 2, 3].map((index) => ({ nodeId: `adb-test-${index}`, name: `Galaxy A07 ${index}`, hardwareModel: 'Samsung Galaxy A07 (SM-A075M)', registeredAtUtc: '2026-11-15T00:00:00Z', lastHeartbeatAtUtc: '2026-11-15T00:00:01Z', health: 'Online', rpcWorkerProcessState: 'Running', metrics: null })));
    http.expectOne('/api/inference-status').flush({ available: true, state: 'ready', coordinatorState: 'ready', activeRpcWorkers: 3, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen fast', message: null });
    fixture.detectChanges();

    const page = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(page).toContain('Ready for chat');
    expect(page).toContain('3/3 active RPC processes');
    expect(page).toContain('coordinator: ready');
    expect(page).toContain('running” does not mean a model is loaded or ready for inference');
  });

  it('does not report chat available when no workers are registered', () => {
    const fixture = TestBed.createComponent(ClusterPage);
    fixture.detectChanges();
    http.expectOne('/api/nodes').flush([]);
    http.expectOne('/api/inference-status').flush({ available: false, state: 'cluster_incomplete', coordinatorState: 'ready', activeRpcWorkers: 0, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M', message: 'Distributed chat requires 3 active RPC processes on online devices; active: 0/3.' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Cluster incomplete');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('0/3 active RPC processes');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('coordinator: ready');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Qwen2.5 0.5B Instruct');
  });

  it('shows API errors and lets the user retry', () => {
    const fixture = TestBed.createComponent(ClusterPage);
    fixture.detectChanges();
    http.expectOne('/api/nodes').flush('API unavailable', { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain('cluster API');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.text-button')?.click();
    http.expectOne('/api/nodes').flush([]);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No devices registered');
  });

  it('shows all nodes offline after their heartbeats expire', () => {
    const fixture = TestBed.createComponent(ClusterPage);
    fixture.detectChanges();
    http.expectOne('/api/nodes').flush(
      [1, 2, 3].map((index) => ({
        nodeId: `sim-a07-${index}`,
        name: `Galaxy A07 · Simulator ${index}`,
        hardwareModel: 'Samsung Galaxy A07 (simulated)',
        registeredAtUtc: '2026-11-14T15:00:00Z',
        lastHeartbeatAtUtc: '2026-11-14T15:00:02Z',
        health: 'Offline',
        metrics: { cpuPercent: 42, availableRamMiB: 900, temperatureCelsius: 38, latencyMs: 14 },
      })),
    );
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelectorAll('.node-card')).toHaveLength(3);
    expect(page.querySelectorAll('.health-pill.offline')).toHaveLength(3);
    expect(page.querySelectorAll('.summary-value')[1].textContent).toContain('0/ 3');
  });

  it('discloses ADB metric provenance and supports an unavailable temperature sensor', () => {
    const fixture = TestBed.createComponent(ClusterPage);
    fixture.detectChanges();
    http.expectOne('/api/nodes').flush([
      {
        nodeId: 'adb-0123456789abcdef',
        name: 'Galaxy A07 · Android node 1',
        hardwareModel: 'Samsung Galaxy A07 (SM-A075M)',
        registeredAtUtc: '2026-11-14T15:00:00Z',
        lastHeartbeatAtUtc: '2026-11-14T15:00:02Z',
        health: 'Online',
        rpcWorkerProcessState: 'Running',
        metrics: { cpuPercent: 42.5, availableRamMiB: 1700, temperatureCelsius: null, latencyMs: 45.2 },
      },
    ]);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Android telemetry via ADB');
    expect(page.textContent).toContain('ADB collection');
    expect(page.textContent).toContain('RPC processRunning');
    expect(page.textContent).toContain('does not mean a model is loaded');
    expect(page.textContent).toContain('CPU42.5%');
    expect(page.textContent).toContain('Temperature—');
  });
});
