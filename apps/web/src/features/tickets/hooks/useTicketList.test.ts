import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TicketFilters } from '../../../types/api';
import { page, ticketListItem } from '../../../test/fixtures';
import { useTicketList } from './useTicketList';

const fetchMock = vi.fn<typeof fetch>();
const response = () => new Response(JSON.stringify(page([ticketListItem()])));

function lastQuery() {
  return new URL(String(fetchMock.mock.lastCall?.[0]), 'http://localhost').searchParams;
}

async function settle() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(300);
  });
}

describe('useTicketList', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async () => response());
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.resetAllMocks();
  });

  it.each<[Partial<TicketFilters>, string, string]>([
    [{ status: 'InProgress' }, 'status', 'InProgress'],
    [{ priority: 'High' }, 'priority', 'High'],
    [{ slaStatus: 'AtRisk' }, 'slaStatus', 'AtRisk'],
    [{ categoryId: 2 }, 'categoryId', '2'],
    [{ customerId: 3 }, 'customerId', '3'],
    [{ assignedAgentId: 4 }, 'assignedAgentId', '4'],
    [{ unassignedOnly: true }, 'unassignedOnly', 'true'],
    [{ sortBy: 'priority', sortDirection: 'asc' }, 'sortBy', 'priority'],
    [{ page: 2 }, 'page', '2'],
    [{ pageSize: 10 }, 'pageSize', '10'],
  ])('immediately requests tickets when %s changes', async (patch, key, value) => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    fetchMock.mockClear();

    await act(async () => {
      result.current.updateFilters(patch);
    });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.lastCall?.[0])).toMatch(/^\/api\/tickets\?/);
    expect(lastQuery().get(key)).toBe(value);
    if (patch.sortDirection) expect(lastQuery().get('sortDirection')).toBe('asc');
  });

  it('requests only after typing settles, including the current status', async () => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    await act(async () => {
      result.current.updateFilters({ status: 'InProgress' });
    });
    fetchMock.mockClear();

    act(() => result.current.updateFilters({ search: 'inv' }));
    await act(async () => { await vi.advanceTimersByTimeAsync(200); });
    act(() => result.current.updateFilters({ search: ' invoice ' }));
    await act(async () => { await vi.advanceTimersByTimeAsync(299); });
    expect(fetchMock).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(lastQuery().get('search')).toBe('invoice');
    expect(lastQuery().get('status')).toBe('InProgress');
    expect(result.current.isLoading).toBe(false);
    expect(result.current.data?.items).toHaveLength(1);
  });

  it('still requests a status change after the search has been cleared', async () => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    act(() => result.current.updateFilters({ search: 'invoice' }));
    await settle();
    act(() => result.current.updateFilters({ search: '' }));
    await settle();
    expect(lastQuery().has('search')).toBe(false);
    fetchMock.mockClear();

    await act(async () => { result.current.updateFilters({ status: 'InProgress' }); });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(lastQuery().get('status')).toBe('InProgress');
    expect(lastQuery().has('search')).toBe(false);
  });

  it('resets pagination on a filter change and immediately clears filters', async () => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    await act(async () => { result.current.updateFilters({ page: 3 }); });
    await act(async () => { result.current.updateFilters({ priority: 'Critical' }); });
    expect(lastQuery().get('page')).toBe('1');
    expect(lastQuery().get('priority')).toBe('Critical');
    fetchMock.mockClear();

    await act(async () => { result.current.resetFilters(); });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(lastQuery().has('priority')).toBe(false);
    expect(lastQuery().get('page')).toBe('1');
  });

  it('cancels pending search when another filter changes, without a duplicate request', async () => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    fetchMock.mockClear();
    act(() => result.current.updateFilters({ search: 'invoice' }));

    await act(async () => { result.current.updateFilters({ status: 'Open' }); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(lastQuery().get('search')).toBe('invoice');
    expect(lastQuery().get('status')).toBe('Open');
    await settle();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('immediately clears a search and cancels its pending request on reset', async () => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    fetchMock.mockClear();
    act(() => result.current.updateFilters({ search: 'invoice' }));

    await act(async () => { result.current.resetFilters(); });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(lastQuery().has('search')).toBe(false);
    await settle();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('keeps the latest results when an older request finishes last', async () => {
    const { result } = renderHook(() => useTicketList());
    await settle();
    let finishOldRequest!: (value: Response) => void;
    fetchMock.mockImplementationOnce(() => new Promise<Response>((resolve) => {
      finishOldRequest = resolve;
    }));
    await act(async () => { result.current.updateFilters({ status: 'Open' }); });
    await act(async () => { result.current.updateFilters({ status: 'InProgress' }); });

    await act(async () => {
      finishOldRequest(new Response(JSON.stringify(page([]))));
    });

    expect(result.current.data?.items).toHaveLength(1);
    expect(result.current.isLoading).toBe(false);
  });

  it.each<Partial<TicketFilters>>([
    { status: 'Open' },
    { search: 'invoice' },
  ])('aborts an in-flight request when filters change to %s without showing an error', async (patch) => {
    fetchMock.mockImplementationOnce((_input, init) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => {
        reject(new DOMException('Request cancelled', 'AbortError'));
      }, { once: true });
    }));
    const { result } = renderHook(() => useTicketList());
    await settle();
    const oldSignal = fetchMock.mock.lastCall?.[1]?.signal;
    expect(oldSignal).toBeInstanceOf(AbortSignal);
    expect(oldSignal?.aborted).toBe(false);

    await act(async () => { result.current.updateFilters(patch); });

    expect(oldSignal?.aborted).toBe(true);
    expect(result.current.error).toBeUndefined();
    await settle();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.lastCall?.[1]?.signal?.aborted).toBe(false);
    expect(result.current.isLoading).toBe(false);
    expect(result.current.data?.items).toHaveLength(1);
  });

  it('aborts an in-flight request when the hook unmounts', async () => {
    fetchMock.mockImplementationOnce((_input, init) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => {
        reject(new DOMException('Request cancelled', 'AbortError'));
      }, { once: true });
    }));
    const { unmount } = renderHook(() => useTicketList());
    await settle();
    const signal = fetchMock.mock.lastCall?.[1]?.signal;
    expect(signal?.aborted).toBe(false);

    await act(async () => { unmount(); });

    expect(signal?.aborted).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('aborts the previous request when manually reloading', async () => {
    fetchMock.mockImplementationOnce((_input, init) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => {
        reject(new DOMException('Request cancelled', 'AbortError'));
      }, { once: true });
    }));
    const { result } = renderHook(() => useTicketList());
    await settle();
    const oldSignal = fetchMock.mock.lastCall?.[1]?.signal;

    await act(async () => { await result.current.reload(); });

    expect(oldSignal?.aborted).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.lastCall?.[1]?.signal?.aborted).toBe(false);
    expect(result.current.error).toBeUndefined();
    expect(result.current.data?.items).toHaveLength(1);
  });
});
