import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface NodeMetrics {
  cpuPercent: number;
  availableRamMiB: number;
  temperatureCelsius: number | null;
  latencyMs: number;
}

export interface ClusterNode {
  nodeId: string;
  name: string;
  hardwareModel: string;
  registeredAtUtc: string;
  lastHeartbeatAtUtc: string | null;
  health: 'AwaitingHeartbeat' | 'Online' | 'Offline';
  rpcWorkerProcessState?: 'Unknown' | 'Running' | 'Stopped';
  metrics: NodeMetrics | null;
}

export interface InferenceStatus {
  available: boolean;
  state: 'ready' | 'loading' | 'unavailable' | 'cluster_incomplete' | 'degraded';
  coordinatorState: string;
  activeRpcWorkers: number;
  requiredWorkers: number;
  modelId: string;
  modelName: string;
  message: string | null;
}

@Injectable({ providedIn: 'root' })
export class ClusterApiService {
  private readonly http = inject(HttpClient);

  getNodes(): Observable<ClusterNode[]> {
    return this.http.get<ClusterNode[]>('/api/nodes');
  }

  getInferenceStatus(): Observable<InferenceStatus> {
    return this.http.get<InferenceStatus>('/api/inference-status');
  }
}
