import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, merge, of, timer } from 'rxjs';
import {
  AgentMemory,
  ChatApiError,
  ChatApiService,
  ChatConversationSummary,
  ChatMessage,
  ChatToolEvent,
  InferenceStatus,
  StoredChatConversation,
} from './services/chat-api.service';

@Component({
  selector: 'app-chat-page',
  imports: [DatePipe],
  templateUrl: './chat-page.html',
})
export class ChatPage {
  private readonly chatApi = inject(ChatApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly conversationStorageKey = 'distributed-ai-agent.conversation-id';
  private activeChat?: AbortController;
  private conversationId?: string;
  private historyRequestVersion = 0;

  protected readonly chatMessages = signal<ChatMessage[]>([]);
  protected readonly chatDraft = signal('');
  protected readonly chatBusy = signal(false);
  protected readonly chatError = signal<string | null>(null);
  protected readonly chatHistoryLoading = signal(false);
  protected readonly chatPersistenceError = signal<string | null>(null);
  protected readonly conversationHistory = signal<ChatConversationSummary[]>([]);
  protected readonly memories = signal<AgentMemory[]>([]);
  protected readonly memoryDraft = signal('');
  protected readonly memoryBusy = signal(false);
  protected readonly memoryError = signal<string | null>(null);
  protected readonly inferenceStatus = signal<InferenceStatus>({ available: false, state: 'unavailable', coordinatorState: 'unavailable', activeRpcWorkers: 0, requiredWorkers: 3, modelId: 'none', modelName: 'No model loaded', message: null });

  constructor() {
    this.destroyRef.onDestroy(() => this.activeChat?.abort());
    this.loadConversationHistory();
    this.loadMemories();
    this.restoreConversation();
    merge(of(undefined), timer(5_000, 5_000))
      .pipe(
        exhaustMap(() => this.chatApi.getInferenceStatus().pipe(
          catchError(() => of<InferenceStatus>({ available: false, state: 'unavailable', coordinatorState: 'unavailable', activeRpcWorkers: 0, requiredWorkers: 3, modelId: 'none', modelName: 'No model loaded', message: null })),
        )),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((status) => this.inferenceStatus.set(status));
  }

  protected inferenceStatusLabel(state: InferenceStatus['state']): string {
    switch (state) {
      case 'ready': return 'ready';
      case 'loading': return 'loading';
      case 'cluster_incomplete': return 'cluster incomplete';
      case 'degraded': return 'session degraded';
      default: return 'unavailable';
    }
  }

  protected sendMessage(event: Event): void {
    event.preventDefault();
    const content = this.chatDraft().trim();
    if (!content || this.chatBusy() || this.chatHistoryLoading() || !this.inferenceStatus().available) return;

    const conversation = [...this.chatMessages(), { role: 'user' as const, content }];
    this.chatMessages.set([...conversation, { role: 'assistant', content: '', toolEvents: [] }]);
    this.chatDraft.set('');
    this.chatBusy.set(true);
    this.chatError.set(null);
    this.memoryError.set(null);

    const controller = new AbortController();
    this.activeChat = controller;
    void this.streamChat(conversation, controller);
  }

  protected updateChatDraft(value: string): void {
    this.chatDraft.set(value);
  }

  protected updateMemoryDraft(value: string): void {
    this.memoryDraft.set(value);
  }

  protected async saveMemory(event: Event): Promise<void> {
    event.preventDefault();
    const content = this.memoryDraft().trim();
    if (!content || this.memoryBusy() || this.memories().length >= 10) return;

    this.memoryBusy.set(true);
    this.memoryError.set(null);
    try {
      const memory = await this.chatApi.createMemory(content);
      this.memories.update((items) => [memory, ...items]);
      this.memoryDraft.set('');
    } catch (error) {
      const status = this.chatApi.getHttpStatus(error);
      this.memoryError.set(
        status === 409
          ? 'The 10-note limit has been reached. Delete a note before adding another.'
          : 'Could not save memory to PostgreSQL.',
      );
    } finally {
      this.memoryBusy.set(false);
    }
  }

  protected async deleteMemory(id: string): Promise<void> {
    if (this.memoryBusy()) return;
    this.memoryBusy.set(true);
    this.memoryError.set(null);
    try {
      await this.chatApi.deleteMemory(id);
      this.memories.update((items) => items.filter((item) => item.id !== id));
    } catch {
      this.memoryError.set('Could not delete memory from PostgreSQL.');
    } finally {
      this.memoryBusy.set(false);
    }
  }

  protected currentConversationId(): string | undefined {
    return this.conversationId;
  }

  protected startNewConversation(): void {
    if (this.chatBusy() || this.chatHistoryLoading()) return;
    this.conversationId = undefined;
    this.chatMessages.set([]);
    this.chatError.set(null);
    this.chatPersistenceError.set(null);
    try {
      localStorage.removeItem(this.conversationStorageKey);
    } catch {
      this.chatPersistenceError.set('Could not update the current conversation in browser storage.');
    }
  }

  protected openConversation(conversationId: string): void {
    if (this.chatBusy() || this.chatHistoryLoading() || conversationId === this.conversationId) return;

    const requestVersion = ++this.historyRequestVersion;
    this.chatHistoryLoading.set(true);
    this.chatError.set(null);
    this.chatPersistenceError.set(null);
    this.chatApi
      .getConversation(conversationId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (conversation) => {
          if (requestVersion !== this.historyRequestVersion) return;
          this.conversationId = conversation.id;
          this.chatMessages.set(conversation.messages.map((message) => ({ ...message })));
          this.chatHistoryLoading.set(false);
          try {
            localStorage.setItem(this.conversationStorageKey, conversation.id);
          } catch {
            this.chatPersistenceError.set('The conversation loaded, but the browser could not save its ID for automatic resumption.');
          }
        },
        error: () => {
          if (requestVersion !== this.historyRequestVersion) return;
          this.chatHistoryLoading.set(false);
          this.chatPersistenceError.set('Could not open this saved conversation. The list is still available.');
        },
      });
  }

  protected toolEventLabel(event: ChatToolEvent): string {
    if (event.tool === 'get_cluster_status') {
      return event.healthy ? 'Cluster status checked · healthy' : 'Cluster status checked · issues detected';
    }

    return 'Tool executed: ' + event.tool;
  }

  private async streamChat(messages: ChatMessage[], controller: AbortController): Promise<void> {
    try {
      let completed = false;
      for await (const event of this.chatApi.streamChat(messages, controller.signal)) {
        const payload = event;
        if (event.memoryUnavailable) {
          this.memoryError.set('Memory could not be loaded for this response; chat continued without it.');
        }
        if (event.event === 'delta' && typeof payload.text === 'string') {
          this.chatMessages.update((items) => {
            const last = items.at(-1);
            if (!last || last.role !== 'assistant') return items;
            return [...items.slice(0, -1), { ...last, content: last.content + payload.text }];
          });
        } else if (event.event === 'tool_events' && Array.isArray(payload.tool_events)) {
          this.chatMessages.update((items) => {
            const last = items.at(-1);
            if (!last || last.role !== 'assistant') return items;
            return [...items.slice(0, -1), { ...last, toolEvents: payload.tool_events }];
          });
        } else if (event.event === 'error') {
          const message = payload.error || 'The inference session failed; wait for supervised recovery.';
          this.markSessionDegraded(message);
          throw new Error(message);
        } else if (event.event === 'done') {
          completed = true;
          break;
        }
      }
      if (!completed) throw new Error('The stream ended before the done event.');
      await this.persistConversation();
    } catch (error) {
      if (!controller.signal.aborted) {
        const message = error instanceof Error ? error.message : 'Could not connect to the agent.';
        if (error instanceof ChatApiError && (error.status === 502 || error.code === 'inference_degraded')) {
          this.markSessionDegraded(message);
        }
        this.markSessionDegraded(message);
        this.chatError.set(message);
        this.chatDraft.set(messages.at(-1)?.content ?? '');
        this.chatMessages.set(messages.slice(0, -1));
      }
    } finally {
      if (this.activeChat === controller) {
        this.activeChat = undefined;
        this.chatBusy.set(false);
      }
    }
  }

  private markSessionDegraded(message: string): void {
    this.inferenceStatus.update((status) => ({
      ...status,
      available: false,
      state: 'degraded',
      message: message.includes('supervisor')
        ? message
        : 'The RPC session failed. The supervisor is recovering the cluster; wait before retrying.',
    }));
  }

  private restoreConversation(): void {
    try {
      this.conversationId = localStorage.getItem(this.conversationStorageKey) ?? undefined;
    } catch {
      this.chatPersistenceError.set('The browser does not allow access to local storage. Chat will continue without persistent history.');
      return;
    }

    if (!this.conversationId) return;

    const conversationId = this.conversationId;
    this.chatHistoryLoading.set(true);
    this.chatApi
      .getConversation(conversationId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (conversation) => {
          if (this.conversationId !== conversationId) return;
          this.chatMessages.set(conversation.messages.map((message) => ({ ...message })));
          this.chatHistoryLoading.set(false);
        },
        error: (error: { status?: number }) => {
          if (this.conversationId !== conversationId) return;
          this.chatHistoryLoading.set(false);
          if (error.status === 404) {
            this.conversationId = undefined;
            try {
              localStorage.removeItem(this.conversationStorageKey);
            } catch {
              // Keep the in-memory chat available if browser storage is blocked.
            }
            return;
          }

          this.chatPersistenceError.set('Could not load saved history. You can still chat in this session.');
        },
      });
  }

  private loadConversationHistory(): void {
    this.chatApi
      .listConversations()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (conversations) => this.conversationHistory.set(conversations),
        error: () => this.chatPersistenceError.set('Could not load the saved conversation list.'),
      });
  }

  private loadMemories(): void {
    this.chatApi
      .listMemories()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (memories) => this.memories.set(memories),
        error: () => this.memoryError.set('Memory is unavailable. You can still chat without saved notes.'),
      });
  }

  private async persistConversation(): Promise<void> {
    const messages = this.chatMessages()
      .filter((message) => message.content.trim().length > 0)
      .map(({ role, content }) => ({ role, content }));
    if (messages.length === 0) return;

    const conversationId = this.conversationId ?? globalThis.crypto.randomUUID();
    this.chatPersistenceError.set(null);
    try {
      const conversation = await this.chatApi.saveConversation(conversationId, messages);
      this.conversationId = conversation.id;
      try {
        localStorage.setItem(this.conversationStorageKey, conversation.id);
      } catch {
        this.chatPersistenceError.set('The conversation was saved on the server, but the browser could not store its ID for automatic resumption.');
      }
      this.loadConversationHistory();
    } catch {
      this.chatPersistenceError.set('Could not save chat history. The conversation remains available while this page is open.');
    }
  }
}
