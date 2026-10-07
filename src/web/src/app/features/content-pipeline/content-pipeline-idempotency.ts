/**
 * One idempotency key per *logical* ask, reused across retries of it.
 *
 * **Why this is not just `crypto.randomUUID()` at each click.** An ask that answered `unavailable` may have
 * reached the server anyway — the response is what was lost. Retrying with a fresh key then buys a second
 * generation and spends the allowance twice. Reusing the key makes the retry a replay, which is what the routes
 * offer it for.
 *
 * A *deliberate* new ask — "plan different looks", "write it again" — is a different question and takes a new
 * key, so {@link next} is called after {@link clear}.
 */
export class IdempotencyKey {
  private key: string | null = null;

  /** The key for this ask: the one a failed attempt already used, or a fresh one. */
  next(): string {
    this.key ??= crypto.randomUUID();

    return this.key;
  }

  /** Done with this ask — the server accepted it, or the creator is asking something else. */
  clear(): void {
    this.key = null;
  }
}
