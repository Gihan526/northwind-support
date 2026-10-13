import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { request, toErrorMessage } from '../../api/client';
import { ErrorMessage } from '../../components/ErrorMessage';
import { Field } from '../../components/Field';
import type { AuthSession } from '../../types/api';
import { getSession, setSession } from './session';

export function LoginPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const [error, setError] = useState<string>();
  const [isSaving, setIsSaving] = useState(false);
  const from = (location.state as { from?: string } | null)?.from;
  // Return only to our own app routes.
  const returnTo = typeof from === 'string' && from.startsWith('/') && !from.startsWith('//')
    && !from.startsWith('/\\') && !from.startsWith('/login') ? from : '/tickets';

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    const email = String(fields.get('email') ?? '').trim();
    const password = String(fields.get('password') ?? ''); // Spaces in passwords are meaningful.
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 256) {
      setError('Enter a valid email address.');
      return;
    }
    if (!password || password.length > 256) {
      setError('Enter your password (up to 256 characters).');
      return;
    }
    if (isSaving) return;
    setError(undefined);
    setIsSaving(true);
    try {
      const session = await request<AuthSession>('/auth/login', {
        method: 'POST', body: JSON.stringify({ email, password }),
      });
      setSession(session);
      await navigate(returnTo, { replace: true });
    } catch (failure) {
      setError(toErrorMessage(failure, 'Unable to sign in.'));
    } finally {
      setIsSaving(false);
    }
  }

  if (getSession()) return <Navigate to={returnTo} replace />;
  return (
    <main className="app__main">
      <section className="card login">
        <h1>Sign in</h1>
        <p>Use your support desk agent account.</p>
        <form onSubmit={(event) => void submit(event)} noValidate>
          <Field id="login-email" label="Email">
            {(props) => <input {...props} name="email" type="email" autoComplete="username" required disabled={isSaving} />}
          </Field>
          <Field id="login-password" label="Password">
            {(props) => <input {...props} name="password" type="password" autoComplete="current-password" required disabled={isSaving} />}
          </Field>
          {error && <ErrorMessage message={error} />}
          <button className="button button--primary" type="submit" disabled={isSaving}>
            {isSaving ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </section>
    </main>
  );
}
