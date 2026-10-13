import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../../api/client';
import { ticketsApi } from '../../../api/tickets';
import { ticketDetail } from '../../../test/fixtures';
import type { TicketDetail, TicketEscalation } from '../../../types/api';
import { EscalationPanel } from './EscalationPanel';

const ticket = ticketDetail({ priority: 'Medium' });
const updated = ticketDetail({ priority: 'High' });
const escalation: TicketEscalation = {
  id: 1, ticketId: ticket.id, fromPriority: 'Medium', toPriority: 'High',
  fromAgent: ticket.assignedAgent, toAgent: updated.assignedAgent,
  fromDueAtUtc: ticket.dueAtUtc, toDueAtUtc: updated.dueAtUtc,
  reason: 'Customer needs urgent help', escalatedBy: 'Alex Turner (agent #1)',
  escalatedAtUtc: '2026-10-13T10:00:00Z',
};

describe('EscalationPanel', () => {
  beforeEach(() => {
    vi.spyOn(ticketsApi, 'getEscalations').mockResolvedValue([]);
    vi.spyOn(ticketsApi, 'escalateTicket').mockResolvedValue({ ticket: updated, escalation });
  });
  afterEach(() => vi.restoreAllMocks());

  async function setup(value: TicketDetail = ticket) {
    const onUpdated = vi.fn();
    render(<EscalationPanel ticket={value} onUpdated={onUpdated} />);
    await screen.findByText('No escalations yet.');
    return { user: userEvent.setup(), onUpdated };
  }

  it.each(['  abc  ', 'x'.repeat(501)])('blocks invalid trimmed reasons before any API call', async (reason) => {
    const { user, onUpdated } = await setup();
    await user.type(screen.getByLabelText('Reason'), reason);
    await user.click(screen.getByRole('button', { name: 'Escalate ticket' }));
    expect(screen.getByText('Enter a reason between 5 and 500 characters.')).toBeInTheDocument();
    expect(ticketsApi.escalateTicket).not.toHaveBeenCalled();
    expect(onUpdated).not.toHaveBeenCalled();
  });

  it('shows a 409 and preserves the ticket and entered reason', async () => {
    vi.mocked(ticketsApi.escalateTicket).mockRejectedValue(new ApiError(409, 'Critical tickets cannot be escalated.'));
    const { user, onUpdated } = await setup();
    await user.type(screen.getByLabelText('Reason'), escalation.reason);
    await user.click(screen.getByRole('button', { name: 'Escalate ticket' }));
    expect(await screen.findByText('Critical tickets cannot be escalated.')).toBeInTheDocument();
    expect(screen.getByLabelText('Reason')).toHaveValue(escalation.reason);
    expect(onUpdated).not.toHaveBeenCalled();
    expect(ticketsApi.getEscalations).toHaveBeenCalledTimes(1);
  });

  it('sends a trimmed reason, updates the ticket and reloads history', async () => {
    const { user, onUpdated } = await setup();
    vi.mocked(ticketsApi.getEscalations).mockResolvedValue([escalation]);
    await user.type(screen.getByLabelText('Reason'), `  ${escalation.reason}  `);
    await user.click(screen.getByRole('button', { name: 'Escalate ticket' }));
    expect(await screen.findByText('Medium → High')).toBeInTheDocument();
    expect(ticketsApi.escalateTicket).toHaveBeenCalledExactlyOnceWith(ticket.id, { reason: escalation.reason });
    expect(onUpdated).toHaveBeenCalledExactlyOnceWith(updated);
    expect(ticketsApi.getEscalations).toHaveBeenCalledTimes(2);
    expect(screen.getByText(/Alex Turner \(agent #1\)/)).toBeInTheDocument();
    expect(screen.getByText(/Sara Lindqvist → Sara Lindqvist/)).toBeInTheDocument();
    expect(screen.getByLabelText('Reason')).toHaveValue('');
  });

  it('disables the form while the request is pending', async () => {
    let finish!: (value: { ticket: TicketDetail; escalation: TicketEscalation }) => void;
    vi.mocked(ticketsApi.escalateTicket).mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const { user } = await setup();
    await user.type(screen.getByLabelText('Reason'), escalation.reason);
    await user.click(screen.getByRole('button', { name: 'Escalate ticket' }));
    expect(screen.getByRole('button', { name: 'Escalating…' })).toBeDisabled();
    expect(screen.getByLabelText('Reason')).toBeDisabled();
    await act(async () => finish({ ticket: updated, escalation }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Escalate ticket' })).toBeEnabled());
  });

  it.each<Partial<TicketDetail>>([
    { priority: 'Critical' }, { status: 'Resolved' }, { status: 'Closed' },
  ])('hides the form for an ineligible ticket but retains history', async (patch) => {
    await setup(ticketDetail(patch));
    expect(screen.queryByRole('button', { name: 'Escalate ticket' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Escalation history' })).toBeInTheDocument();
  });
});
