import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, map, merge, of, Subject, timer } from 'rxjs';
import { ClusterApiService, ClusterNode, InferenceStatus, NodeMetrics } from './services/cluster-api.service';

type PollResult = { nodes: ClusterNode[]; inferenceStatus: InferenceStatus; error: null } | { nodes: null; inferenceStatus: InferenceStatus; error: string };

@Component({
  selector: 'app-cluster-page',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './cluster-page.html',
})
export class ClusterPage {
  private readonly clusterApi = inject(ClusterApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshRequests = new Subject<void>();

  protected readonly nodes = signal<ClusterNode[]>([]);
  protected readonly loading = signal(true);
  protected readonly apiError = signal<string | null>(null);
  protected readonly lastUpdated = signal<Date | null>(null);
  protected readonly inferenceStatus = signal<InferenceStatus>({ available: false, state: 'unavailable', coordinatorState: 'unavailable', activeRpcWorkers: 0, requiredWorkers: 3, modelId: 'none', modelName: 'No model loaded', message: null });
  protected readonly onlineCount = computed(() => this.nodes().filter((node) => node.health === 'Online').length);
  protected readonly simulationMode = computed(() => this.nodes().some((node) => node.nodeId.startsWith('sim-')));
  protected readonly androidMode = computed(() => this.nodes().some((node) => node.nodeId.startsWith('adb-')));

  constructor() {
    merge(of(undefined), timer(5_000, 5_000), this.refreshRequests)
      .pipe(
        exhaustMap(() =>
          this.clusterApi.getNodes().pipe(
            map((nodes) => ({ nodes, inferenceStatus: this.inferenceStatus(), error: null }) as PollResult),
            catchError(() => of<PollResult>({ nodes: null, inferenceStatus: this.inferenceStatus(), error: 'Could not reach the cluster API.' })),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((result) => {
        this.loading.set(false);
        if (result.nodes === null) {
          this.apiError.set(result.error);
          return;
        }

        this.nodes.set(result.nodes);
        this.apiError.set(null);
        this.lastUpdated.set(new Date());
      });

    merge(of(undefined), timer(5_000, 5_000))
      .pipe(
        exhaustMap(() => this.clusterApi.getInferenceStatus().pipe(
          catchError(() => of<InferenceStatus>({ available: false, state: 'unavailable', coordinatorState: 'unavailable', activeRpcWorkers: 0, requiredWorkers: 3, modelId: 'none', modelName: 'No model loaded', message: null })),
        )),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((status) => this.inferenceStatus.set(status));
  }

  protected refresh(): void {
    this.loading.set(true);
    this.refreshRequests.next();
  }

  protected formatTime(value: Date | null): string {
    return value?.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) ?? '—';
  }

  protected metric(node: ClusterNode, key: keyof NodeMetrics): number | null {
    return node.metrics?.[key] ?? null;
  }

  protected healthLabel(health: ClusterNode['health']): string {
    switch (health) {
      case 'Online':
        return 'Online';
      case 'Offline':
        return 'Offline';
      default:
        return 'Awaiting heartbeat';
    }
  }

  protected rpcWorkerProcessLabel(state: ClusterNode['rpcWorkerProcessState']): string {
    switch (state) {
      case 'Running':
        return 'Running';
      case 'Stopped':
        return 'Stopped';
      default:
        return 'Unknown';
    }
  }

  protected inferenceStatusLabel(state: InferenceStatus['state']): string {
    switch (state) {
      case 'ready': return 'Ready for chat';
      case 'cluster_incomplete': return 'Cluster incomplete';
      case 'degraded': return 'Session degraded';
      case 'loading': return 'Loading model';
      default: return 'Unavailable';
    }
  }

  protected latencyLabel(node: ClusterNode): string {
    if (node.nodeId.startsWith('adb-')) return 'ADB collection';
    if (node.nodeId.startsWith('sim-')) return 'Simulated latency';
    return 'Latency';
  }

  protected ramPercent(node: ClusterNode): number {
    const available = this.metric(node, 'availableRamMiB');
    return available === null ? 0 : Math.max(0, Math.min(100, (available / 4_096) * 100));
  }
}
