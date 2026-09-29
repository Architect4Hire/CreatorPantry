import { AiAllowanceState } from '../services/ai-usage.service';

/**
 * Whether a new AI request should be offered at all.
 *
 * <strong>Only a known, spent allowance stops anything.</strong> `unknown` covers a first read still in
 * flight and a read that failed, and `degraded` covers figures we are no longer sure of — blocking on either
 * would let a runtime-config blip or one dropped response lock a creator out of AI entirely. The server is
 * the authority and refuses properly on its own; disabling the button early is a courtesy that saves a
 * pointless round trip, not the thing that enforces the limit.
 *
 * One definition, used by every request screen, so six buttons cannot come to disagree about when they are
 * available.
 */
export function allowanceBlocksNewRequests(allowance: AiAllowanceState): boolean {
  return allowance.kind === 'exhausted' || allowance.kind === 'suspended';
}
