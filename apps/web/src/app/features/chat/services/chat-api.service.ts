import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { readSseEvents } from '../../../shared/sse';

export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  toolEvents?: ChatToolEvent[];
}

export interface ChatToolEvent {
  tool: string;
  healthy: boolean;
}

export interface StoredChatConversation {
  id: string;
  messages: ChatMessage[];
}

export interface ChatConversationSummary {
  id: string;
  title: string;
  updatedAtUtc: string;
}

export interface AgentMemory {
  id: string;
  content: string;
  createdAtUtc: string;
  updatedAtUtc: string;
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

export interface ChatStreamEvent {
  event: string;
  text?: string;
  error?: string;
  tool_events?: ChatToolEvent[];
  memoryUnavailable?: boolean;
}

export class ChatApiError extends Error {
  constructor(message: string, readonly status: number, readonly code?: string) {
    super(message);
    this.name = 'ChatApiError';
  }
}

@Injectable({ providedIn: 'root' })
export class ChatApiService {
  private readonly http = inject(HttpClient);

  getInferenceStatus(): Observable<InferenceStatus> {
    return this.http.get<InferenceStatus>('/api/inference-status');
  }

  listConversations(): Observable<ChatConversationSummary[]> {
    return this.http.get<ChatConversationSummary[]>('/api/conversations');
  }

  getConversation(id: string): Observable<StoredChatConversation> {
    return this.http.get<StoredChatConversation>(`/api/conversations/${id}`);
  }

  saveConversation(id: string, messages: Pick<ChatMessage, 'role' | 'content'>[]): Promise<StoredChatConversation> {
    return firstValueFrom(this.http.put<StoredChatConversation>(`/api/conversations/${id}`, { messages }));
  }

  listMemories(): Observable<AgentMemory[]> {
    return this.http.get<AgentMemory[]>('/api/memories');
  }

  createMemory(content: string): Promise<AgentMemory> {
    return firstValueFrom(this.http.post<AgentMemory>('/api/memories', { content }));
  }

  deleteMemory(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/memories/${id}`));
  }

  getHttpStatus(error: unknown): number | undefined {
    return error instanceof HttpErrorResponse ? error.status : undefined;
  }

  async *streamChat(messages: ChatMessage[], signal: AbortSignal): AsyncGenerator<ChatStreamEvent> {
    const response = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
      body: JSON.stringify({ messages: messages.map(({ role, content }) => ({ role, content })) }),
      signal,
    });

    if (!response.ok) {
      let message = `The agent server returned HTTP ${response.status}.`;
      let code: string | undefined;
      try {
        const body = (await response.json()) as { code?: string; message?: string };
        if (body.message) message = body.message;
        code = body.code;
      } catch { /* Keep the HTTP status when the body is not a JSON error. */ }
      throw new ChatApiError(message, response.status, code);
    }

    if (!response.headers.get('content-type')?.includes('text/event-stream') || !response.body) {
      throw new ChatApiError('The server returned an invalid SSE stream.', response.status);
    }
    if (response.headers.get('X-Agent-Memory-Status') === 'unavailable') {
      yield { event: 'metadata', memoryUnavailable: true };
    }

    for await (const event of readSseEvents(response.body)) {
      const payload = JSON.parse(event.data) as Omit<ChatStreamEvent, 'event'>;
      yield { ...payload, event: event.event };
    }
  }
}
