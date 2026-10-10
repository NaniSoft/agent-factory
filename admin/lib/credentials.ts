/**
 * The Credentials surface's view model and the three calls that drive it.
 *
 * The shape mirrors `AgentFactory.Api.CredentialsView`: a name and whether it has a
 * value, camel-cased by name on both sides and pinned there rather than left to a
 * serialiser's naming policy. There is deliberately no value on the read — the surface
 * is write-only, and the factory never sends one back — and no codegen between the two.
 */
export type CredentialView = {
  name: string;
  set: boolean;
};

export type CredentialsView = {
  credentials: CredentialView[];
};

/**
 * Read the credential names the factory can use.
 *
 * The API is the factory's own process and lives off this app's base path, so the
 * request is absolute from the origin. `cache: 'no-store'` is the point: every read
 * reflects the secrets directory now, and a cached presence would be a claim about a
 * moment that has passed.
 */
export async function fetchCredentials(signal?: AbortSignal): Promise<CredentialsView> {
  const response = await fetch('/api/credentials', { cache: 'no-store', signal });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as CredentialsView;
}

/**
 * Set a credential's value. The value travels in the request body and is never read
 * back: the answer is the name and its new presence, and nothing else.
 */
export async function setCredential(name: string, value: string): Promise<void> {
  const response = await fetch(`/api/credentials/${encodeURIComponent(name)}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ value }),
  });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }
}

/** Remove a credential's file, by name. */
export async function removeCredential(name: string): Promise<void> {
  const response = await fetch(`/api/credentials/${encodeURIComponent(name)}`, {
    method: 'DELETE',
  });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }
}
