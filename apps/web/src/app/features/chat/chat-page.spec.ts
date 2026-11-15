import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ChatPage } from './chat-page';

const readyInference = { available: true, state: 'ready', coordinatorState: 'ready', activeRpcWorkers: 3, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M', message: null };

describe('ChatPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ChatPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    for (const request of http.match('/api/inference-status')) {
      if (!request.cancelled) {
        request.flush({ available: true, state: 'ready', coordinatorState: 'ready', activeRpcWorkers: 3, requiredWorkers: 3, modelId: 'qwen-fast', modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M', message: null });
      }
    }
    http.verify({ ignoreCancelled: true });
    localStorage.clear();
    vi.unstubAllGlobals();
  });

  it('shows the same active cluster model in chat', () => {
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush({
      available: true,
      state: 'ready',
      coordinatorState: 'ready',
      activeRpcWorkers: 3,
      requiredWorkers: 3,
      modelId: 'qwen2.5-0.5b-instruct-q4_k_m',
      modelName: 'Qwen2.5 0.5B Instruct · Q4_K_M',
      message: null,
    });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Qwen2.5 0.5B Instruct');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('ready');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3/3 active RPC processes');
  });

  it('disables chat and does not fetch while a required worker is missing', () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush({
      available: false,
      state: 'cluster_incomplete',
      coordinatorState: 'ready',
      activeRpcWorkers: 1,
      requiredWorkers: 3,
      modelId: 'qwen-fast',
      modelName: 'Qwen fast',
      message: 'Distributed chat requires 3 active RPC processes on online devices; active: 1/3.',
    });
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('1/3 active RPC processes');
    expect(page.querySelector<HTMLTextAreaElement>('#chat-message')?.disabled).toBe(true);
    expect(page.querySelector<HTMLButtonElement>('.send-button')?.disabled).toBe(true);
    page.querySelector<HTMLFormElement>('.chat-composer')!.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );
    expect(fetch).not.toHaveBeenCalled();
  });

  it('restores the last saved conversation and lists recent conversations', () => {
    const conversationId = 'bb9fdcb8-54f9-43a1-9358-8ac245781684';
    localStorage.setItem('distributed-ai-agent.conversation-id', conversationId);
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([
      { id: conversationId, title: 'Remember our conversation?', updatedAtUtc: '2026-11-14T15:00:01Z' },
    ]);
    http.expectOne('/api/memories').flush([
      {
        id: 'd92ccf61-b93a-4050-a2fc-63e61f380d9a',
        content: 'I prefer C# examples.',
        createdAtUtc: '2026-11-14T15:00:00Z',
        updatedAtUtc: '2026-11-14T15:00:00Z',
      },
    ]);
    http.expectOne(`/api/conversations/${conversationId}`).flush({
      id: conversationId,
      messages: [
        { role: 'user', content: 'Remember our conversation?' },
        { role: 'assistant', content: 'Yes, I loaded it from PostgreSQL.' },
      ],
      createdAtUtc: '2026-11-14T15:00:00Z',
      updatedAtUtc: '2026-11-14T15:00:01Z',
    });
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Remember our conversation?');
    expect(page.textContent).toContain('Yes, I loaded it from PostgreSQL.');
    expect(page.textContent).toContain('Saved conversations (1)');
    expect(page.textContent).toContain('I prefer C# examples.');
  });

  it('opens a selected history item and starts a new chat without deleting saved conversations', () => {
    const conversationId = 'bb9fdcb8-54f9-43a1-9358-8ac245781684';
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([
      { id: conversationId, title: 'Previous conversation', updatedAtUtc: '2026-11-14T15:00:01Z' },
    ]);
    http.expectOne('/api/memories').flush([]);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    page.querySelector<HTMLButtonElement>('.chat-history-item')!.click();
    http.expectOne(`/api/conversations/${conversationId}`).flush({
      id: conversationId,
      messages: [
        { role: 'user', content: 'Previous conversation' },
        { role: 'assistant', content: 'Continuing from history.' },
      ],
      createdAtUtc: '2026-11-14T15:00:00Z',
      updatedAtUtc: '2026-11-14T15:00:01Z',
    });
    fixture.detectChanges();
    expect(page.textContent).toContain('Continuing from history.');
    expect(localStorage.getItem('distributed-ai-agent.conversation-id')).toBe(conversationId);

    page.querySelector<HTMLButtonElement>('.chat-new-button')!.click();
    fixture.detectChanges();
    expect(page.textContent).not.toContain('Continuing from history.');
    expect(page.textContent).toContain('Previous conversation');
    expect(localStorage.getItem('distributed-ai-agent.conversation-id')).toBeNull();
  });

  it('saves and removes user-managed agent memories', async () => {
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush(readyInference);
    const page = fixture.nativeElement as HTMLElement;
    const input = page.querySelector<HTMLTextAreaElement>('#memory-note')!;
    input.value = 'I prefer Python examples.';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    page.querySelector<HTMLFormElement>('.memory-composer')!.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );

    const create = http.expectOne('/api/memories');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ content: 'I prefer Python examples.' });
    create.flush({
      id: 'c62749da-17bd-4918-aa81-16c07f4c91af',
      content: 'I prefer Python examples.',
      createdAtUtc: '2026-11-14T15:00:00Z',
      updatedAtUtc: '2026-11-14T15:00:00Z',
    });
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.textContent).toContain('I prefer Python examples.');
      expect(page.textContent).toContain('1/10 notes');
    });

    page.querySelector<HTMLButtonElement>('.memory-list button')!.click();
    const remove = http.expectOne('/api/memories/c62749da-17bd-4918-aa81-16c07f4c91af');
    expect(remove.request.method).toBe('DELETE');
    remove.flush(null, { status: 204, statusText: 'No Content' });
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.textContent).not.toContain('I prefer Python examples.');
    });
  });

  it('streams the model reply and persists the completed conversation', async () => {
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush(readyInference);
    const page = fixture.nativeElement as HTMLElement;
    const input = page.querySelector<HTMLTextAreaElement>('#chat-message')!;
    input.value = 'Hello, cluster';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        headers: new Headers({
          'Content-Type': 'text/event-stream; charset=utf-8',
          'X-Agent-Memory-Status': 'unavailable',
        }),
        body: new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(
              new TextEncoder().encode(
                'event: tool_events\ndata: {"tool_events":[{"tool":"get_cluster_status","healthy":true}]}\n\n' +
                  'event: delta\ndata: {"text":"Hello! workers are active."}\n\n' +
                  'event: done\ndata: {"done":true}\n\n',
              ),
            );
            controller.close();
          },
        }),
      }),
    );
    page.querySelector<HTMLFormElement>('.chat-composer')!.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );

    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.textContent).toContain('Hello! workers are active.');
      expect(page.textContent).toContain('Cluster status checked · healthy');
      expect(page.textContent).toContain('Memory could not be loaded for this response');
    });
    const save = http.expectOne((request) => request.method === 'PUT' && request.url.startsWith('/api/conversations/'));
    expect(save.request.body.messages).toEqual([
      { role: 'user', content: 'Hello, cluster' },
      { role: 'assistant', content: 'Hello! workers are active.' },
    ]);
    save.flush({
      id: save.request.url.split('/').at(-1),
      messages: save.request.body.messages,
      createdAtUtc: '2026-11-14T15:00:00Z',
      updatedAtUtc: '2026-11-14T15:00:01Z',
    });
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.textContent).not.toContain('The agent is generating a response');
    });
    http.expectOne('/api/conversations').flush([
      {
        id: save.request.url.split('/').at(-1),
        title: 'Hello, cluster',
        updatedAtUtc: '2026-11-14T15:00:01Z',
      },
    ]);
    expect(localStorage.getItem('distributed-ai-agent.conversation-id')).toBe(save.request.url.split('/').at(-1));
  });

  it('keeps a completed reply in the current session when persistence is unavailable', async () => {
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush(readyInference);
    const page = fixture.nativeElement as HTMLElement;
    const input = page.querySelector<HTMLTextAreaElement>('#chat-message')!;
    input.value = 'Persistence test';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        headers: new Headers({ 'Content-Type': 'text/event-stream' }),
        body: new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(
              new TextEncoder().encode('event: delta\ndata: {"text":"Visible response"}\n\nevent: done\ndata: {"done":true}\n\n'),
            );
            controller.close();
          },
        }),
      }),
    );
    page.querySelector<HTMLFormElement>('.chat-composer')!.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );

    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.textContent).toContain('Visible response');
    });
    const save = http.expectOne((request) => request.method === 'PUT' && request.url.startsWith('/api/conversations/'));
    save.flush('database unavailable', { status: 503, statusText: 'Service Unavailable' });
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.textContent).toContain('Visible response');
      expect(page.textContent).toContain('Could not save chat history');
    });
    expect(localStorage.getItem('distributed-ai-agent.conversation-id')).toBeNull();
  });

  it('keeps chat usable when the saved-conversation list cannot be loaded', () => {
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush('database unavailable', {
      status: 503,
      statusText: 'Service Unavailable',
    });
    http.expectOne('/api/memories').flush('database unavailable', {
      status: 503,
      statusText: 'Service Unavailable',
    });
    http.expectOne('/api/inference-status').flush(readyInference);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Could not load the saved conversation list');
    expect(page.textContent).toContain('Memory is unavailable.');
    expect(page.querySelector<HTMLTextAreaElement>('#chat-message')?.disabled).toBe(false);
  });

  it('shows an SSE error event returned by the agent API', async () => {
    const fixture = TestBed.createComponent(ChatPage);
    fixture.detectChanges();
    http.expectOne('/api/conversations').flush([]);
    http.expectOne('/api/memories').flush([]);
    http.expectOne('/api/inference-status').flush(readyInference);
    const page = fixture.nativeElement as HTMLElement;
    const input = page.querySelector<HTMLTextAreaElement>('#chat-message')!;
    input.value = 'Teste';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        headers: new Headers({ 'Content-Type': 'text/event-stream' }),
        body: new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(new TextEncoder().encode('event: error\ndata: {"error":"model failure"}\n\n'));
            controller.close();
          },
        }),
      }),
    );
    page.querySelector<HTMLFormElement>('.chat-composer')!.dispatchEvent(
      new Event('submit', { bubbles: true, cancelable: true }),
    );

    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(page.querySelector('[role="alert"]')?.textContent).toContain('model failure');
      expect(page.querySelector<HTMLTextAreaElement>('#chat-message')?.disabled).toBe(true);
      expect(page.textContent).toContain('RPC session failed');
    });
  });
});
