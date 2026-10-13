import type { AuthSession } from '../../types/api';

// Memory only: refreshing the page requires login again; tokens are not persisted in browser storage.
let session: AuthSession | null = null;
const listeners = new Set<() => void>();

export function getSession() { return session; }
export function setSession(value: AuthSession | null) {
  session = value;
  listeners.forEach((notify) => notify());
}
export function clearSession() { setSession(null); }
export function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}
