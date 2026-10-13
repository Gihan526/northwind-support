import { useCallback, useState, type FormEvent } from 'react';
import { toErrorMessage } from '../../../api/client';
import { ticketsApi } from '../../../api/tickets';
import { ErrorMessage } from '../../../components/ErrorMessage';
import { Field } from '../../../components/Field';
import { Spinner } from '../../../components/Spinner';
import { useAsyncData } from '../../../hooks/useAsyncData';
import { formatDateTime } from '../../../lib/format';
import type { TicketDetail } from '../../../types/api';

export function EscalationPanel({ ticket, onUpdated }: {
  ticket: TicketDetail; onUpdated: (ticket: TicketDetail) => void;
}) {
  const load = useCallback(() => ticketsApi.getEscalations(ticket.id), [ticket.id]);
  const history = useAsyncData(load, 'Unable to load escalation history.');
  const [error, setError] = useState<string>();
  const [isSaving, setIsSaving] = useState(false);
  const canEscalate = ticket.priority !== 'Critical' && ticket.status !== 'Resolved' && ticket.status !== 'Closed';

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const reason = String(new FormData(form).get('reason') ?? '').trim();
    if (reason.length < 5 || reason.length > 500) {
      setError('Enter a reason between 5 and 500 characters.');
      return;
    }
    if (!canEscalate || isSaving) return;
    setError(undefined);
    setIsSaving(true);
    try {
      const result = await ticketsApi.escalateTicket(ticket.id, { reason });
      form.reset();
      onUpdated(result.ticket);
      await history.reload();
    } catch (failure) {
      // Includes 409 conflicts; keep the current ticket and entered reason on failure.
      setError(toErrorMessage(failure, 'Unable to escalate this ticket.'));
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <section className="card">
      <h2>Escalation</h2>
      {canEscalate ? (
        <form onSubmit={(event) => void submit(event)} noValidate>
          <p>Raise priority by one level and restart the SLA window. Your signed-in account is recorded.</p>
          <Field id="escalation-reason" label="Reason" hint="5 to 500 characters after trimming spaces.">
            {(props) => <textarea {...props} name="reason" rows={3} required disabled={isSaving} />}
          </Field>
          {error && <ErrorMessage message={error} />}
          <button className="button button--primary" type="submit" disabled={isSaving}>
            {isSaving ? 'Escalating…' : 'Escalate ticket'}
          </button>
        </form>
      ) : <p>Critical, resolved and closed tickets cannot be escalated.</p>}
      <h3>Escalation history</h3>
      {history.isLoading && <Spinner label="Loading escalation history" />}
      {history.error && <ErrorMessage message={history.error} onRetry={() => void history.reload()} />}
      {history.data?.length === 0 && <p>No escalations yet.</p>}
      {history.data?.map((row) => (
        <article key={row.id}>
          <h4>{row.fromPriority} → {row.toPriority}</h4>
          <p>{row.reason}</p>
          <p>Agent: {row.fromAgent?.fullName ?? 'Unassigned'} → {row.toAgent?.fullName ?? 'Unassigned'}</p>
          <p>Due: {formatDateTime(row.fromDueAtUtc)} → {formatDateTime(row.toDueAtUtc)}</p>
          <p>By {row.escalatedBy} at {formatDateTime(row.escalatedAtUtc)}</p>
        </article>
      ))}
    </section>
  );
}
