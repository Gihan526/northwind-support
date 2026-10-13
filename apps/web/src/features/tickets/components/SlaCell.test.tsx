import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { SlaStatus } from '../../../types/api';
import { SlaCell } from './SlaCell';

describe('SlaCell', () => {
  it.each<[SlaStatus, string, string]>([
    ['WithinSla', 'Within SLA', 'info'],
    ['AtRisk', 'At risk', 'warning'],
    ['Breached', 'Breached', 'danger'],
    ['Met', 'Met', 'success'],
    ['NotApplicable', 'No SLA', 'neutral'],
  ])('identifies %s with text and colour', (status, label, tone) => {
    render(<SlaCell dueAtUtc={null} slaStatus={status} />);
    expect(screen.getByText(label)).toHaveClass(`badge--${tone}`);
  });
});
