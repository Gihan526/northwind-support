import { useSyncExternalStore } from 'react';
import { clearSession, getSession, subscribe } from './features/auth/session';
import { Link, NavLink, Outlet } from 'react-router-dom';

/** Application shell: the header, the navigation, and wherever the router puts the page. */
export function App() {
  const session = useSyncExternalStore(subscribe, getSession);
  return (
    <div className="app">
      <header className="app__header">
        <Link className="app__brand" to="/tickets">
          Northwind Support
        </Link>

        <nav className="app__nav" aria-label="Main">
          <NavLink to="/tickets">Tickets</NavLink>
          <NavLink to="/customers">Customers</NavLink>
        </nav>
        {session && <div className="app__session">
          <span>{session.agent.fullName}</span>
          <button className="button button--ghost" type="button" onClick={clearSession}>Log out</button>
        </div>}
      </header>

      <main className="app__main">
        <Outlet />
      </main>
    </div>
  );
}
