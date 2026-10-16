'use client';

import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';

import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { Input } from '@nanisoft/prism-ui/components/input';
import { Item } from '@nanisoft/prism-ui/components/item';
import { Label } from '@nanisoft/prism-ui/components/label';
import { NativeSelect } from '@nanisoft/prism-ui/components/native-select';
import { PasswordField } from '@nanisoft/prism-ui/components/password-field';
import { Status } from '@nanisoft/prism-ui/components/status';

import {
  fetchCredentials,
  removeCredential,
  setCredential,
  type CredentialView,
  type LlmProvider,
} from '@/lib/credentials';

/**
 * What the page has read so far. Three states rather than a nullable list and a
 * boolean: a fetch that has not answered, one that answered, and one that failed are
 * three different things a reader is owed.
 */
type Reading =
  | { phase: 'loading' }
  | { phase: 'ready'; credentials: CredentialView[]; llmProviders: LlmProvider[] }
  | { phase: 'error'; message: string };

/**
 * The words a provider option reads. The id is the wire format — the prefix of a
 * project's model reference — and these are this app's words for it, the way the
 * decision buttons are named here and not by the factory.
 */
const PROVIDER_LABELS: Record<string, string> = {
  anthropic: 'Anthropic',
  openai: 'OpenAI',
  google: 'Google',
  opencode: 'OpenCode Zen',
  'opencode-go': 'OpenCode Go',
};

/**
 * The Credentials surface.
 *
 * The factory's key names, each with whether it has a value, and one write-only form
 * that sets one. It is a composed surface and not a Prism item, because Prism refuses
 * any item whose whole job is holding a secret: what is here is a list of names and a
 * `PasswordField`, and the value the reader types is posted and never rendered back.
 * Nothing on this page ever reads a value — neither the API it calls nor this code can.
 *
 * The form opens with the factory's own provider table: a provider is one half of a
 * model reference (`anthropic/claude-sonnet-4-5`), and choosing one fills the name with
 * the environment variable that provider's tooling reads — the name a project has to
 * declare as its LLM credential for the factory to route the value into the round. The
 * name stays editable, because a project may name a credential anything; the dropdown
 * is a fact about OpenCode's contract, not a restriction on the file.
 */
export default function CredentialsPage() {
  const [reading, setReading] = useState<Reading>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback((signal?: AbortSignal) => {
    fetchCredentials(signal)
      .then((view) =>
        setReading({
          phase: 'ready',
          credentials: view.credentials,
          llmProviders: view.llmProviders,
        }))
      .catch((error: unknown) => {
        if (signal?.aborted) return;
        setReading({
          phase: 'error',
          message: error instanceof Error ? error.message : 'the factory could not be reached',
        });
      });
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load, attempt]);

  const retry = useCallback(() => {
    setReading({ phase: 'loading' });
    setAttempt((count) => count + 1);
  }, []);

  const write = useCallback(
    async (event: FormEvent<HTMLFormElement>) => {
      event.preventDefault();
      if (name.trim() === '' || value.trim() === '') {
        setNotice('A credential needs a name and a value.');
        return;
      }

      setBusy(true);
      try {
        await setCredential(name.trim(), value);
        // The value is cleared the moment it is written and is never rendered back.
        // The notice names the credential, never what was entered against it.
        setValue('');
        setNotice(`Wrote ${name.trim()}.`);
        load();
      } catch (error) {
        setNotice(error instanceof Error ? error.message : 'the value could not be written');
      } finally {
        setBusy(false);
      }
    },
    [load, name, value],
  );

  const remove = useCallback(
    async (target: string) => {
      setBusy(true);
      try {
        await removeCredential(target);
        setNotice(`Removed ${target}.`);
        load();
      } catch (error) {
        setNotice(error instanceof Error ? error.message : 'the credential could not be removed');
      } finally {
        setBusy(false);
      }
    },
    [load],
  );

  return (
    <main className="credentials">
      <header className="credentials__header">
        <h1 className="credentials__title">Credentials</h1>
        <p className="credentials__lede">
          The keys the factory can use, by name. A credential is set here and never read back: the
          surface shows whether a name has a value, never what it is.
        </p>
      </header>

      {reading.phase === 'loading' ? (
        <Status tone="info" label="Reading the credentials" />
      ) : reading.phase === 'error' ? (
        <Card>
          <CardHeader>
            <CardTitle>Could not read the credentials</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="credentials__error">
              <Status tone="destructive" label={reading.message} />
              <Button variant="outline" onClick={retry}>
                Try again
              </Button>
            </div>
          </CardContent>
        </Card>
      ) : (
        <div className="credentials__panels">
          <Card>
            <CardHeader>
              <CardTitle>Known credentials</CardTitle>
            </CardHeader>
            <CardContent>
              {reading.credentials.length === 0 ? (
                <p className="credentials__hint">No credential is declared yet.</p>
              ) : (
                <ul className="credentials__list">
                  {reading.credentials.map((credential) => (
                    <li key={credential.name}>
                      <Item
                        entry={{
                          id: credential.name,
                          title: credential.name,
                          meta: (
                            <Status
                              tone={credential.set ? 'success' : 'neutral'}
                              label={credential.set ? 'set' : 'not set'}
                            />
                          ),
                          actions: (
                            <Button
                              variant="outline"
                              size="sm"
                              disabled={busy}
                              onClick={() => void remove(credential.name)}
                            >
                              Remove
                            </Button>
                          ),
                        }}
                      />
                    </li>
                  ))}
                </ul>
              )}
              {notice ? <p className="credentials__notice">{notice}</p> : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Set a credential</CardTitle>
            </CardHeader>
            <CardContent>
              <form className="credentials__form" onSubmit={(event) => void write(event)}>
                <div className="credentials__field">
                  <Label htmlFor="credential-provider">Provider</Label>
                  <NativeSelect
                    id="credential-provider"
                    autoComplete="off"
                    value={
                      reading.llmProviders.find(
                        (candidate) => candidate.canonicalEnvironmentVariable === name.trim(),
                      )?.id ?? ''
                    }
                    onChange={(event) => {
                      const chosen = reading.llmProviders.find(
                        (candidate) => candidate.id === event.target.value,
                      );
                      if (chosen) {
                        setName(chosen.canonicalEnvironmentVariable);
                      }
                    }}
                  >
                    <option value="">Choose a provider</option>
                    {reading.llmProviders.map((candidate) => (
                      <option key={candidate.id} value={candidate.id}>
                        {`${PROVIDER_LABELS[candidate.id] ?? candidate.id} — ${candidate.canonicalEnvironmentVariable}`}
                      </option>
                    ))}
                  </NativeSelect>
                  <p className="credentials__hint">
                    Optional. A provider is the OpenCode CLI&apos;s half of a model reference:
                    choosing one fills the name with the environment variable its tooling reads —
                    the name a project then declares as its LLM credential. A name of your own
                    works too.
                  </p>
                </div>
                <div className="credentials__field">
                  <Label htmlFor="credential-name">Name</Label>
                  <Input
                    id="credential-name"
                    name="name"
                    autoComplete="off"
                    value={name}
                    onChange={(event) => setName(event.target.value)}
                  />
                </div>
                <PasswordField
                  label="Value"
                  value={value}
                  onValueChange={setValue}
                  revealLabel="Show value"
                  hideLabel="Hide value"
                  autoComplete="new-password"
                />
                <Button type="submit" disabled={busy}>
                  Set credential
                </Button>
              </form>
            </CardContent>
          </Card>
        </div>
      )}
    </main>
  );
}
