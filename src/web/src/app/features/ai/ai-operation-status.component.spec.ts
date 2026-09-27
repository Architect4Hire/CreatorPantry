import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AiFailureCategory, AiOperationStatus } from '../../models/ai-proposal.models';
import { AiOperationStatusComponent, AiStatusConnection, isRetryableAiOutcome } from './ai-operation-status.component';

describe('AiOperationStatusComponent', () => {
  let fixture: ComponentFixture<AiOperationStatusComponent>;

  async function render(options: {
    status: AiOperationStatus;
    failureCategory?: AiFailureCategory | null;
    connection?: AiStatusConnection;
  }): Promise<HTMLElement> {
    fixture = TestBed.createComponent(AiOperationStatusComponent);
    fixture.componentRef.setInput('status', options.status);
    fixture.componentRef.setInput('failureCategory', options.failureCategory ?? null);
    fixture.componentRef.setInput('requestedAt', '2026-03-12T14:02:00Z');
    fixture.componentRef.setInput('statusChangedAt', '2026-03-12T14:05:00Z');
    fixture.componentRef.setInput('connection', options.connection ?? 'live');
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AiOperationStatusComponent] });
  });

  it('names every state in words rather than leaving it to a colour', async () => {
    const expected: Record<AiOperationStatus, string> = {
      Requested: 'Queued',
      Running: 'Working',
      Proposed: 'Ready for review',
      Accepted: 'Accepted',
      PartiallyAccepted: 'Some changes accepted',
      Rejected: 'Declined',
      Failed: "Didn't finish",
      Expired: 'Timed out',
    };

    for (const [status, label] of Object.entries(expected)) {
      const element = await render({ status: status as AiOperationStatus });
      expect(element.querySelector('cp-status-pill')?.textContent).toContain(label);
    }
  });

  // The fact the whole panel rests on. A creator must be able to read it, not infer it.
  it('says nothing has been saved while a proposal is waiting', async () => {
    const element = await render({ status: 'Proposed' });

    expect(element.querySelector('.status-detail')?.textContent).toContain('Nothing has been saved');
  });

  it('explains a failure in plain words, with no provider or template jargon', async () => {
    const element = await render({ status: 'Failed', failureCategory: 'Timeout' });
    const failure = element.querySelector('.status-failure')?.textContent ?? '';

    expect(failure).toContain('took too long');
    expect(failure).not.toContain('provider');
    expect(failure).not.toContain('template');
  });

  it('has a line for every failure category, so no explanation is silently dropped', async () => {
    const categories: AiFailureCategory[] = [
      'Unspecified',
      'Validation',
      'Quota',
      'TemplateUnavailable',
      'Provider',
      'RateLimited',
      'Timeout',
      'OutputSchemaInvalid',
      'DomainInvalid',
      'SafetyBlocked',
      'Cancelled',
      'LeaseAbandoned',
    ];

    for (const category of categories) {
      const element = await render({ status: 'Failed', failureCategory: category });
      expect(element.querySelector('.status-failure')?.textContent?.trim().length).toBeGreaterThan(0);
    }
  });

  it('shows no failure line when nothing failed', async () => {
    const element = await render({ status: 'Running' });

    expect(element.querySelector('.status-failure')).toBeNull();
  });

  it('announces a failure assertively and everything else politely', async () => {
    const running = await render({ status: 'Running' });
    expect(running.querySelector('[role="status"]')?.textContent).toContain('Working');
    expect(running.querySelector('[role="alert"]')?.textContent?.trim()).toBe('');

    const failed = await render({ status: 'Failed', failureCategory: 'Provider' });
    expect(failed.querySelector('[role="alert"]')?.textContent).toContain("Didn't finish");
    expect(failed.querySelector('[role="status"]')?.textContent?.trim()).toBe('');
  });

  it('says outright when what it is showing may be out of date', async () => {
    const element = await render({ status: 'Running', connection: 'degraded' });

    expect(element.textContent).toContain('Not up to date');
    expect(element.textContent).toContain("couldn't check just now");
    expect(element.querySelector('[role="status"]')?.textContent).toContain('last update');
  });

  describe('isRetryableAiOutcome', () => {
    it('offers another attempt for the failures that resolve themselves', () => {
      for (const category of ['Provider', 'RateLimited', 'Timeout', 'Cancelled', 'LeaseAbandoned'] as const) {
        expect(isRetryableAiOutcome('Failed', category)).toBeTrue();
      }
    });

    // ai.md: a safety block is never retried, and the rest arrive at the same answer while spending the budget.
    it('does not offer another attempt for a refusal that would repeat itself', () => {
      for (const category of [
        'SafetyBlocked',
        'OutputSchemaInvalid',
        'DomainInvalid',
        'Validation',
        'Quota',
        'TemplateUnavailable',
      ] as const) {
        expect(isRetryableAiOutcome('Failed', category)).toBeFalse();
      }
    });

    it('offers another attempt after an expiry, where nobody acted in time', () => {
      expect(isRetryableAiOutcome('Expired', null)).toBeTrue();
    });

    it('offers nothing while a request is still alive or already decided', () => {
      expect(isRetryableAiOutcome('Running', null)).toBeFalse();
      expect(isRetryableAiOutcome('Proposed', null)).toBeFalse();
      expect(isRetryableAiOutcome('Accepted', null)).toBeFalse();
    });
  });
});
