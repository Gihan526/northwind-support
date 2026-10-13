import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { App } from '../../App';
import { request } from '../../api/client';
import type { AuthSession } from '../../types/api';
import { LoginPage } from './LoginPage';
import { RequireLogin } from './RequireLogin';
import { clearSession, getSession, setSession } from './session';

const session: AuthSession = {
  accessToken: 'test-token', expiresAtUtc: '2099-01-01T00:00:00Z',
  agent: { id: 1, fullName: 'Alex Turner', email: 'alex@example.test' },
};
const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  clearSession();
  vi.stubGlobal('fetch', fetchMock);
  fetchMock.mockImplementation(async () => new Response(JSON.stringify(session)));
});
afterEach(() => { cleanup(); clearSession(); vi.unstubAllGlobals(); vi.resetAllMocks(); });

function showApp(path = '/tickets/12?view=history#latest') {
  const router = createMemoryRouter([
    { path: '/login', element: <LoginPage /> },
    { element: <RequireLogin />, children: [{ element: <App />, children: [
      { path: '/tickets', element: <h1>Ticket list</h1> },
      { path: '/tickets/:id', element: <h1>Ticket detail</h1> },
    ] }] },
  ], { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}

async function signIn(password = 'Local_Dev_Login_123!') {
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Email'), 'alex@example.test');
  if (password) await user.type(screen.getByLabelText('Password'), password);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

it('redirects a protected page to login without fetching its data', async () => {
  const router = showApp();
  await screen.findByRole('heading', { name: 'Sign in' });
  expect(router.state.location.pathname).toBe('/login');
  expect(router.state.location.state.from).toBe('/tickets/12?view=history#latest');
  expect(fetchMock).not.toHaveBeenCalled();
});

it('signs in, returns to the requested page, displays the agent and attaches the token', async () => {
  const router = showApp();
  await signIn();
  await screen.findByRole('heading', { name: 'Ticket detail' });
  expect(router.state.location.pathname + router.state.location.search + router.state.location.hash)
    .toBe('/tickets/12?view=history#latest');
  expect(screen.getByText('Alex Turner')).toBeInTheDocument();
  expect(String(fetchMock.mock.calls[0][0])).toBe('/api/auth/login');
  expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({ email: 'alex@example.test', password: 'Local_Dev_Login_123!' });
  await request('/tickets');
  expect(new Headers(fetchMock.mock.lastCall?.[1]?.headers).get('Authorization')).toBe('Bearer test-token');
});

it('logout clears the session and redirects to login', async () => {
  setSession(session);
  showApp();
  await userEvent.setup().click(screen.getByRole('button', { name: 'Log out' }));
  await screen.findByRole('heading', { name: 'Sign in' });
  expect(getSession()).toBeNull();
  expect(screen.queryByText('Alex Turner')).not.toBeInTheDocument();
});

it('a 401 clears the session and redirects to login', async () => {
  setSession(session);
  showApp();
  fetchMock.mockResolvedValueOnce(new Response('{}', { status: 401 }));
  await act(async () => {
    await expect(request('/tickets')).rejects.toMatchObject({ status: 401 });
  });
  expect(getSession()).toBeNull();
  await screen.findByRole('heading', { name: 'Sign in' });
});

it('shows generic rejected credentials and does not create a session', async () => {
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ detail: 'Email or password is incorrect.' }), { status: 401 }));
  showApp('/login');
  await signIn('Wrong password');
  expect(await screen.findByRole('alert')).toHaveTextContent('Email or password is incorrect.');
  expect(getSession()).toBeNull();
  expect(screen.getByLabelText('Email')).toHaveValue('alex@example.test');
});

it('blocks an empty password without calling the API', async () => {
  showApp('/login');
  await signIn('');
  expect(screen.getByRole('alert')).toHaveTextContent('Enter your password');
  expect(fetchMock).not.toHaveBeenCalled();
});

it('blocks an invalid email without calling the API', async () => {
  const user = userEvent.setup();
  showApp('/login');
  await user.type(screen.getByLabelText('Email'), 'bad-email');
  await user.type(screen.getByLabelText('Password'), 'Some password');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  expect(screen.getByRole('alert')).toHaveTextContent('Enter a valid email');
  expect(fetchMock).not.toHaveBeenCalled();
});

it('disables duplicate submission while the login request is pending', async () => {
  fetchMock.mockImplementation(() => new Promise(() => {}));
  showApp('/login');
  await signIn();
  expect(screen.getByRole('button', { name: 'Signing in…' })).toBeDisabled();
  expect(screen.getByLabelText('Password')).toBeDisabled();
  expect(fetchMock).toHaveBeenCalledTimes(1);
});
