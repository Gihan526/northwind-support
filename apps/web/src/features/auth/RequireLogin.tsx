import { useSyncExternalStore } from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { getSession, subscribe } from './session';

export function RequireLogin() {
  const session = useSyncExternalStore(subscribe, getSession);
  const location = useLocation();
  return session ? <Outlet /> : <Navigate to="/login" replace
    state={{ from: location.pathname + location.search + location.hash }} />;
}
