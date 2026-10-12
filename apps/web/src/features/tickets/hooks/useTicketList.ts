import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../../../api/client';
import { ticketsApi } from '../../../api/tickets';
import type { PagedResult, TicketFilters, TicketListItem } from '../../../types/api';

const DEBOUNCE_MS = 300;

export const emptyFilters: TicketFilters = {
  search: '',
  status: undefined,
  priority: undefined,
  categoryId: undefined,
  customerId: undefined,
  assignedAgentId: undefined,
  unassignedOnly: false,
  page: 1,
  pageSize: 20,
  sortBy: 'createdAtUtc',
  sortDirection: 'desc',
};

interface State {
  isLoading: boolean;
  data?: PagedResult<TicketListItem>;
  error?: string;
}

/** Owns the ticket list's filters and the data they produce. */
export function useTicketList() {
  const [filters, setFilters] = useState<TicketFilters>(emptyFilters);
  const [state, setState] = useState<State>({ isLoading: true });
  const previousSearch = useRef(filters.search);
  const latestRequest = useRef(0);
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    const requestId = ++latestRequest.current;
    setState((current) => ({ ...current, isLoading: true, error: undefined }));

    try {
      const data = await ticketsApi.getTickets(filters, controller.signal);
      if (requestId === latestRequest.current) {
        setState({ isLoading: false, data, error: undefined });
      }
    } catch (error) {
      // Cancellation is expected when filters change or the page unmounts.
      if (controller.signal.aborted) return;

      const message = error instanceof ApiError ? error.message : 'Unable to load tickets.';
      if (requestId === latestRequest.current) {
        setState((current) => ({ ...current, isLoading: false, error: message }));
      }
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, [filters]);

  useEffect(() => {
    const requestSequence = latestRequest;
    const pendingRequest = activeRequest;
    const searchChanged = previousSearch.current !== filters.search;
    previousSearch.current = filters.search;

    // Only typing waits; dropdowns, paging, sorting and reset fetch immediately.
    const handle = searchChanged && filters !== emptyFilters
      ? window.setTimeout(() => void load(), DEBOUNCE_MS)
      : undefined;

    if (handle === undefined) void load();

    return () => {
      window.clearTimeout(handle);
      pendingRequest.current?.abort();
      pendingRequest.current = null;
      // A response for old filters must not replace the current results.
      requestSequence.current++;
    };
  }, [filters, load]);

  const updateFilters = (patch: Partial<TicketFilters>) =>
    setFilters((current) => ({ ...current, ...patch, page: patch.page ?? 1 }));

  const resetFilters = () => setFilters(emptyFilters);

  return { filters, updateFilters, resetFilters, reload: load, ...state };
}
