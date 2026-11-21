'use client';

import { useEffect, useMemo, useRef, useState } from 'react';

import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { Checkbox } from '@nanisoft/prism-ui/components/checkbox';
import { Input } from '@nanisoft/prism-ui/components/input';
import { NativeSelect } from '@nanisoft/prism-ui/components/native-select';

import { PageHeader } from '@/components/page-header';
import { ErrorCard, LoadingCard } from '@/components/reading-state';
import { api } from '@/lib/api';
import { messageOf } from '@/lib/format';
import type {
  Catalog,
  Flavour,
  ProfileSummary,
  ProfileVerifyResult,
  ProfilesView,
  ProbeResult,
  UtilityBinding,
} from '@/lib/types';
import { useApi, type Reading } from '@/lib/use-api';

/**
 * How the fleet's agents authenticate and which models they may run. A profile
 * is a flavour, an optional credential and a model palette; a phase pin must be
 * a palette member, and the two fleet bindings are the profiles triage,
 * planning, unpinned phases and dead pins fall back to.
 */
export default function ProfilesPage() {
  const view = useApi<ProfilesView>('/api/profiles');
  const catalog = useApi<Catalog>('/api/engine/catalog');
  const external = useApi<Catalog>('/api/engine/catalog/external');

  return (
    <div className="app-page app-page--wide">
      <PageHeader
        title="Profiles"
        description="How the fleet authenticates and which models it may run. A profile carries one flavour (opencode-go), an optional credential and the palette that bounds every phase pin."
        actions={
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => {
              catalog.refresh();
              external.refresh();
            }}
          >
            Refresh catalog
          </Button>
        }
      />
      {view.reading.phase === 'loading' ? (
        <LoadingCard label="Reading the agent profiles" />
      ) : view.reading.phase === 'error' ? (
        <ErrorCard
          title="The profiles could not be read"
          message={view.reading.message}
          onRetry={view.refresh}
        />
      ) : (
        <ProfilesBody view={view.reading.data} catalog={catalog} external={external} reload={view.refresh} />
      )}
    </div>
  );
}

function ProfilesBody({
  view,
  catalog,
  external,
  reload,
}: {
  view: ProfilesView;
  catalog: { reading: Reading<Catalog>; refresh: () => void };
  external: { reading: Reading<Catalog>; refresh: () => void };
  reload: () => void;
}) {
  const [profiles, setProfiles] = useState<ProfileSummary[] | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [utility, setUtility] = useState<UtilityBinding | null>(null);
  const [fallback, setFallback] = useState<UtilityBinding | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [clearing, setClearing] = useState(false);
  const [keyEntry, setKeyEntry] = useState('');
  const [showKey, setShowKey] = useState(false);
  const [clearKey, setClearKey] = useState(false);
  const [paletteFilter, setPaletteFilter] = useState('');
  const [probe, setProbe] = useState<ProbeResult | null>(null);
  const [verify, setVerify] = useState<ProfileVerifyResult | null>(null);
  const [verifyModel, setVerifyModel] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [flash, setFlash] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [bindingBusy, setBindingBusy] = useState(false);
  const [utilityFlash, setUtilityFlash] = useState<string | null>(null);
  const [fallbackFlash, setFallbackFlash] = useState<string | null>(null);
  const [bindingError, setBindingError] = useState<string | null>(null);

  const seededRef = useRef(false);

  useEffect(() => {
    setProfiles(view.profiles.map((profile) => ({ ...profile, models: [...profile.models] })));
    if (!seededRef.current) {
      // First load only: open the first profile so the editor is useful.
      // After that the selection is user-driven — deleting keeps it empty
      // until the user picks another profile or creates one.
      setSelectedId(view.profiles[0]?.id ?? null);
      seededRef.current = true;
    } else {
      setSelectedId((current) => {
        if (current === null || current === '') return current;
        return view.profiles.some((profile) => profile.id === current) ? current : null;
      });
    }
    setUtility(view.utility === null ? null : { ...view.utility });
    setFallback(view.fallback === null ? null : { ...view.fallback });
    setKeyEntry('');
    setShowKey(false);
    setClearKey(false);
    setProbe(null);
  }, [view]);

  // Auto-refresh the palette when a draft key is typed (debounced):
  // serve snapshot needs a restart to bake the key, but the public Go list
  // (GET /inference/go/v1/models) refreshes immediately so the list is live.
  const catalogRefresh = catalog.refresh;
  const externalRefresh = external.refresh;
  const keyTimerRef = useRef<number | null>(null);
  useEffect(() => {
    const trimmed = keyEntry.trim();
    if (trimmed.length === 0) return;
    if (keyTimerRef.current !== null) window.clearTimeout(keyTimerRef.current);
    keyTimerRef.current = window.setTimeout(() => {
      catalogRefresh();
      externalRefresh();
    }, 800);
    return () => {
      if (keyTimerRef.current !== null) window.clearTimeout(keyTimerRef.current);
    };
  }, [keyEntry, catalogRefresh, externalRefresh]);

  const selectedIndex = useMemo(() => {
    if (profiles === null || selectedId === null) return -1;
    return profiles.findIndex((profile) => profile.id === selectedId);
  }, [profiles, selectedId]);

  // The palette/verify picker list: serve snapshot + public Go models list merged,
  // so a new profile or a pasted key immediately shows every `opencode-go/…` model,
  // even before the serve bakes the credential. Docs: curl https://opencode.ai/inference/go/v1/models
  const selectedProfileForOptions = selectedIndex >= 0 && profiles !== null ? profiles[selectedIndex] : undefined;
  const modelOptions = useMemo(() => {
    const provider = 'opencode-go';
    const serveModels =
      catalog.reading.phase === 'ready'
        ? (catalog.reading.data.providers.find((entry) => entry.id === provider)?.models ?? []).map(
            (model: string) => `${provider}/${model}`,
          )
        : [];
    const externalModels =
      external.reading.phase === 'ready'
        ? (external.reading.data.providers.find((entry) => entry.id === provider)?.models ?? []).map(
            (model: string) => `${provider}/${model}`,
          )
        : [];
    const selected = selectedProfileForOptions?.models ?? [];
    // oxlint-disable-next-line unicorn/no-array-sort — the literal here is already a fresh copy
    const merged = [...new Set([...serveModels, ...externalModels, ...selected])].sort((a: string, b: string) => a.localeCompare(b));
    return merged;
  }, [catalog.reading, external.reading, selectedProfileForOptions?.models]);

  if (profiles === null) {
    return <LoadingCard label="Preparing the profile editor" />;
  }

  const profile = selectedProfileForOptions;

  const updateProfile = (updater: (next: ProfileSummary) => void) => {
    setProfiles((current) => {
      if (current === null) return current;
      const next = current.map((entry) => ({ ...entry, models: [...entry.models] }));
      const target = next[selectedIndex];
      if (target !== undefined) updater(target);
      return next;
    });
  };

  const select = (id: string) => {
    setSelectedId(id);
    setKeyEntry('');
    setShowKey(false);
    setClearKey(false);
    setPaletteFilter('');
    setProbe(null);
    setVerify(null);
    setVerifyModel('');
    setErrors([]);
    setFlash(null);
  };

  const newProfile = () => {
    // A new (unsaved) profile edits the local list only. The palette list comes
    // from the public Go models endpoint (external catalog) + the serve snapshot,
    // merged in Palette — so refresh both to make the list appear immediately,
    // even before a key is saved.
    catalog.refresh();
    external.refresh();
    const entry: ProfileSummary = {
      id: '',
      name: '',
      flavour: 'OpencodeGo',
      flavourLabel: 'opencode-go',
      models: [],
      hasKey: false,
    };
    setProfiles((current) => [...(current ?? []), entry]);
    setSelectedId('');
    setKeyEntry('');
    setShowKey(false);
    setClearKey(false);
    setPaletteFilter('');
    setProbe(null);
    setVerify(null);
    setVerifyModel('');
    setErrors([]);
    setFlash(null);
  };

  const saveProfile = async () => {
    if (profile === undefined) return;
    const validation: string[] = [];
    if (profile.name.trim().length === 0) {
      validation.push('Name is empty');
    }
    if (profile.models.length === 0) {
      validation.push(`Palette is empty — no phase could pin '${profile.name}'.`);
    }
    if (validation.length > 0) {
      setErrors(validation);
      setFlash(null);
      return;
    }

    setBusy(true);
    setErrors([]);
    setFlash(null);
    try {
      const result = await api.saveProfile({
        id: profile.id.length > 0 ? profile.id : null,
        name: profile.name,
        flavour: profile.flavour,
        apiKey: keyEntry.length > 0 ? keyEntry : undefined,
        clearKey,
        models: profile.models,
      });
      if (result.profile !== null) {
        setSelectedId(result.profile.id);
      }
      setKeyEntry('');
      setClearKey(false);
      setFlash('Saved');
      reload();
    } catch (failure) {
      setErrors([messageOf(failure)]);
    } finally {
      setBusy(false);
    }
  };

  const deleteProfile = async () => {
    if (profile === undefined) return;
    if (profile.id.length === 0) {
      setProfiles((current) => current?.filter((entry) => entry !== profile) ?? current);
      setSelectedId(null);
      setProbe(null);
      setVerify(null);
      setDeleteError(null);
      setFlash('Discarded — editor cleared');
      return;
    }
    setBusy(true);
    setErrors([]);
    setDeleteError(null);
    setFlash(null);
    try {
      await api.deleteProfile(profile.id);
      // Empty the centre/right editor: selection stays null across the reload
      // (the synced effect preserves null instead of re-selecting).
      setSelectedId(null);
      setProbe(null);
      setVerify(null);
      setFlash('Deleted — editor cleared');
      reload();
    } catch (failure) {
      setDeleteError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  };

  const clearBindingsAndDelete = async () => {
    if (profile === undefined) return;
    setClearing(true);
    setDeleteError(null);
    try {
      // Clear fleet bindings that reference this profile so delete can proceed.
      if (utility?.profileId === profile.id) {
        await api.setUtility(null, null);
        setUtility(null);
      }
      if (fallback?.profileId === profile.id) {
        await api.setFallback(null, null);
        setFallback(null);
      }
      // Unpin pipeline phases that reference this profile.
      const pipelineCatalog = await api.pipelines();
      for (const pipe of pipelineCatalog.pipelines) {
        if (!pipe.phases.some((phase) => phase.profileId === profile.id)) continue;
        const next = {
          ...pipe,
          phases: pipe.phases.map((phase) =>
            phase.profileId === profile.id ? { ...phase, profileId: null, modelId: null } : phase,
          ),
        };
        await api.savePipeline(next);
      }
      await api.deleteProfile(profile.id);
      setSelectedId(null);
      setProbe(null);
      setVerify(null);
      setFlash('Cleared pins/bindings and deleted — editor cleared');
      reload();
    } catch (failure) {
      setDeleteError(messageOf(failure));
    } finally {
      setClearing(false);
    }
  };

  const probeProfile = async () => {
    if (profile === undefined) return;
    setBusy(true);
    setProbe(null);
    try {
      const result = await api.probeProfile({
        id: profile.id.length > 0 ? profile.id : null,
        flavour: profile.flavour,
        apiKey: keyEntry.length > 0 ? keyEntry : undefined,
        models: profile.models,
      });
      setProbe(result);
      // Test is read-only — refresh the merged list so a pasted key is reflected.
      catalog.refresh();
      external.refresh();
    } catch (failure) {
      setProbe({ ok: false, message: messageOf(failure), keyRejected: false });
    } finally {
      setBusy(false);
    }
  };

  const verifyProfile = async () => {
    if (profile === undefined) return;
    setBusy(true);
    setVerify(null);
    try {
      const result = await api.verifyProfile({
        id: profile.id.length > 0 ? profile.id : null,
        flavour: profile.flavour,
        apiKey: keyEntry.length > 0 ? keyEntry : undefined,
        modelId: verifyModel.length > 0 ? verifyModel : undefined,
        models: profile.models,
      });
      setVerify(result);
      // Verify may bake the key (serve restart) — refresh both catalogs so the
      // snapshot reflects what the serve can now drive.
      catalog.refresh();
      external.refresh();
    } catch (failure) {
      setVerify({
        ok: false,
        message: messageOf(failure),
        modelReply: null,
        modelId: verifyModel || profile.models[0] || null,
        providerId: null,
        tokensIn: 0,
        tokensOut: 0,
        cost: 0,
        keyRejected: false,
      });
    } finally {
      setBusy(false);
    }
  };

  const restart = async () => {
    setBusy(true);
    setBindingError(null);
    try {
      await api.restartEngine();
      catalog.refresh();
      external.refresh();
      setFlash('Serve restarted — the catalog snapshot is renewed');
    } catch (failure) {
      setBindingError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  };

  const saveUtility = async () => {
    setBindingBusy(true);
    setBindingError(null);
    try {
      await api.setUtility(utility?.profileId ?? null, utility?.modelId ?? null);
      setUtilityFlash(
        utility?.profileId
          ? `Bound: ${profiles.find((entry) => entry.id === utility.profileId)?.name ?? utility.profileId} · ${utility.modelId ?? '(engine default model)'}`
          : 'Utility binding cleared — engine defaults',
      );
      reload();
    } catch (failure) {
      setUtilityFlash(null);
      setBindingError(messageOf(failure));
    } finally {
      setBindingBusy(false);
    }
  };

  const saveFallback = async () => {
    setBindingBusy(true);
    setBindingError(null);
    try {
      await api.setFallback(fallback?.profileId ?? null, fallback?.modelId ?? null);
      setFallbackFlash(
        fallback?.profileId
          ? `Bound: ${profiles.find((entry) => entry.id === fallback.profileId)?.name ?? fallback.profileId} · ${fallback.modelId ?? '(no model)'}`
          : 'Fallback binding cleared — engine defaults',
      );
      reload();
    } catch (failure) {
      setFallbackFlash(null);
      setBindingError(messageOf(failure));
    } finally {
      setBindingBusy(false);
    }
  };

  return (
    <div className="app-stack">
      <div className="app-bindings">
        <BindingCard
          title="Fleet utility sessions"
          note="Required. Run this for triage and parent planning, fleet-wide. Never a pipeline phase."
          mandatory
          profiles={profiles}
          binding={utility}
          modelRequired
          onChange={setUtility}
          onSave={saveUtility}
          flash={utilityFlash}
          busy={bindingBusy}
          onCreate={newProfile}
        />
        <BindingCard
          title="Fleet fallback model"
          note="Purpose: unpinned phases run here for a deterministic model (no engine-default roulette), and a dead pinned model lands here. Unset = engine defaults, dead pins fail the run."
          profiles={profiles}
          binding={fallback}
          modelRequired
          onChange={setFallback}
          onSave={saveFallback}
          flash={fallbackFlash}
          busy={bindingBusy}
        />
      </div>

      {bindingError === null ? null : (
        <Alert variant="destructive">
          <AlertTitle>The binding was rejected</AlertTitle>
          <AlertDescription>{bindingError}</AlertDescription>
        </Alert>
      )}

      {profiles.length === 0 ? (
        <Card>
          <CardContent>
            <div className="app-stack">
              <p className="app-muted">No profiles yet. A profile is what a phase pin or a binding points at.</p>
              <div>
                <Button type="button" size="sm" onClick={newProfile}>
                  + profile
                </Button>
              </div>
            </div>
          </CardContent>
        </Card>
      ) : profile === undefined ? (
        <div className="app-profile-layout">
          <Card>
            <CardHeader>
              <div className="app-editor-head">
                <CardTitle as="h2">Profiles</CardTitle>
                <Button type="button" size="sm" variant="outline" onClick={newProfile}>
                  + profile
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <div className="app-profile-list">
                {profiles.map((entry, index) => {
                  const referrers = view.referrers[entry.id] ?? [];
                  return (
                    <button
                      key={entry.id.length > 0 ? entry.id : `new-${index}`}
                      type="button"
                      className="app-profile"
                      onClick={() => select(entry.id)}
                    >
                      <span>
                        {entry.name.length > 0 ? entry.name : '(unnamed)'}{' '}
                        <Badge variant="outline">{entry.flavourLabel}</Badge>
                      </span>
                      <span className="app-profile__meta">
                        {entry.hasKey ? 'key set' : 'key blank'} · {entry.models.length} models
                        {referrers.length > 0 ? ` · pinned (${referrers.length})` : ''}
                      </span>
                    </button>
                  );
                })}
              </div>
              <p className="app-hint">Flavours are a closed set — a new one arrives as code, not config.</p>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle as="h2">No profile selected</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="app-stack">
                <p className="app-muted">
                  The editor is empty. Pick a profile from the list to edit it, or create a new one.
                </p>
                {flash === null ? null : <Badge variant="success">{flash}</Badge>}
              </div>
            </CardContent>
          </Card>
        </div>
      ) : (
        <div className="app-profile-layout">
          <Card>
            <CardHeader>
              <div className="app-editor-head">
                <CardTitle as="h2">Profiles</CardTitle>
                <Button type="button" size="sm" variant="outline" onClick={newProfile}>
                  + profile
                </Button>
              </div>
            </CardHeader>
            <CardContent>
              <div className="app-profile-list">
                {profiles.map((entry, index) => {
                  const referrers = view.referrers[entry.id] ?? [];
                  return (
                    <button
                      key={entry.id.length > 0 ? entry.id : `new-${index}`}
                      type="button"
                      className={
                        index === selectedIndex ? 'app-profile app-profile--active' : 'app-profile'
                      }
                      onClick={() => select(entry.id)}
                      aria-current={index === selectedIndex ? 'true' : undefined}
                    >
                      <span>
                        {entry.name.length > 0 ? entry.name : '(unnamed)'}{' '}
                        <Badge variant="outline">{entry.flavourLabel}</Badge>
                      </span>
                      <span className="app-profile__meta">
                        {entry.hasKey ? 'key set' : 'key blank'} · {entry.models.length} models
                        {referrers.length > 0 ? ` · pinned (${referrers.length})` : ''}
                      </span>
                    </button>
                  );
                })}
              </div>
              <p className="app-hint">Flavours are a closed set — a new one arrives as code, not config.</p>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle as="h2">
                Edit: {profile.name.length > 0 ? profile.name : '(unnamed)'}
              </CardTitle>
            </CardHeader>
            <CardContent>
              <div className="app-stack">
                <div className="app-field-row">
                  <div className="app-field">
                    <label className="app-label" htmlFor="profile-name">
                      Name
                    </label>
                    <Input
                      id="profile-name"
                      value={profile.name}
                      onChange={(event) => {
                        const value = event.target.value;
                        updateProfile((next) => {
                          next.name = value;
                        });
                      }}
                    />
                  </div>
                  <div className="app-field">
                    <label className="app-label" htmlFor="profile-flavour">
                      Flavour
                    </label>
                    <NativeSelect
                      id="profile-flavour"
                      size="sm"
                      value={profile.flavour}
                      onChange={(event) => {
                        const value = event.target.value;
                        updateProfile((next) => {
                          next.flavour = value;
                          next.flavourLabel =
                            view.flavours.find((entry) => entry.value === value)?.label ?? value;
                          next.models = [];
                        });
                        setKeyEntry('');
                        setClearKey(false);
                        setProbe(null);
                      }}
                    >
                      {view.flavours.map((entry) => (
                        <option key={entry.value} value={entry.value}>
                          {entry.label}
                        </option>
                      ))}
                    </NativeSelect>
                  </div>
                </div>

                <div className="app-field">
                  <span className="app-label">
                    API key{' '}
                    <span className="app-hint">{clearKey || !profile.hasKey ? 'key blank' : 'key set'}</span>
                  </span>
                  <div className="app-row">
                    <div className="app-enroll__input">
                      <Input
                        type={showKey ? 'text' : 'password'}
                        value={keyEntry}
                        placeholder={
                          profile.hasKey && !clearKey
                            ? 'stored — paste to replace; leave blank to keep'
                            : 'paste key — stored in the config store, masked here'
                        }
                        onChange={(event) => {
                          setKeyEntry(event.target.value);
                          if (event.target.value.length > 0) {
                            setClearKey(false);
                          }
                        }}
                        autoComplete="off"
                      />                    </div>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => setShowKey((current) => !current)}
                    >
                      {showKey ? 'Hide' : 'Show'}
                    </Button>
                    {profile.hasKey && !clearKey ? (
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => {
                          updateProfile((next) => {
                            next.hasKey = false;
                          });
                          setClearKey(true);
                          setKeyEntry('');
                        }}
                      >
                        Clear
                      </Button>
                    ) : null}
                  </div>
                  <p className="app-hint">
                    {flavourFor(view, profile)?.keyMode ?? ''} Palette auto-refreshes when you type — serve snapshot +
                    public models list.
                  </p>
                </div>

                <div className="app-field">
                  <span className="app-label">
                    Model palette (multi-select — bounds every phase pin) · {profile.models.length}{' '}
                    selected
                  </span>
                  <Palette
                    profile={profile}
                    flavour={flavourFor(view, profile)}
                    catalog={catalog.reading}
                    external={external.reading}
                    modelOptions={modelOptions}
                    filter={paletteFilter}
                    onFilter={setPaletteFilter}
                    onToggle={(model) => {
                      updateProfile((next) => {
                        next.models = next.models.includes(model)
                          ? next.models.filter((candidate) => candidate !== model)
                          : [...next.models, model];
                      });
                    }}
                    onRestart={() => void restart()}
                    busy={busy}
                  />
                </div>

                <div className="app-row">
                  <Button type="button" size="sm" onClick={() => void saveProfile()} disabled={busy}>
                    Save profile
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => void probeProfile()}
                    disabled={busy}
                  >
                    Test key &amp; catalog
                  </Button>
                  <span className="app-hint">
                    Save blocks on an empty name or palette. The probe is advisory and read-only.
                  </span>
                </div>

                <div className="app-field">
                  <label className="app-label" htmlFor="verify-model">
                    Verify model (same list as the palette — first palette model by default)
                  </label>
                  <div className="app-row">
                    <NativeSelect
                      id="verify-model"
                      size="sm"
                      value={verifyModel || profile.models[0] || modelOptions[0] || ''}
                      onChange={(event) => setVerifyModel(event.target.value)}
                      disabled={modelOptions.length === 0}
                    >
                      {modelOptions.length === 0 ? (
                        <option value="">— no models (public list loading…) —</option>
                      ) : (
                        modelOptions.map((model: string) => (
                          <option key={model} value={model}>
                            {model}
                          </option>
                        ))
                      )}
                    </NativeSelect>
                    <Button type="button" size="sm" onClick={() => void verifyProfile()} disabled={busy}>
                      Verify key
                    </Button>
                  </div>
                  <p className="app-hint">
                    Injects the key above (or the stored key) into the serve and runs
                    “Say hi in one short sentence. Who are you?” on the model — the reply is the proof.
                    A changed key costs one serve restart.
                  </p>
                </div>

                {probe === null ? null : (
                  <Alert variant={probe.ok ? undefined : 'destructive'}>
                    <AlertTitle>{probe.ok ? 'Probe passed' : 'Probe found a problem'}</AlertTitle>
                    <AlertDescription>{probe.message}</AlertDescription>
                  </Alert>
                )}

                {verify === null ? null : (
                  <Alert variant={verify.ok ? undefined : 'destructive'}>
                    <AlertTitle>{verify.ok ? 'Verify passed' : 'Verify failed'}</AlertTitle>
                    <AlertDescription>
                      <div>{verify.message}</div>
                      {verify.modelReply === null || verify.modelReply === '' ? null : (
                        <div className="app-card-note">
                          model reply: “{verify.modelReply}” · {verify.tokensIn}in/{verify.tokensOut}out · cost {verify.cost}
                        </div>
                      )}
                    </AlertDescription>
                  </Alert>
                )}

                {errors.length > 0 ? (
                  <Alert variant="destructive">
                    <AlertTitle>Save rejected</AlertTitle>
                    <AlertDescription>
                      <ul className="app-errors">
                        {errors.map((error) => (
                          <li key={error}>{error}</li>
                        ))}
                      </ul>
                    </AlertDescription>
                  </Alert>
                ) : null}

                {deleteError === null ? null : (
                  <Alert variant="destructive">
                    <AlertTitle>Delete rejected</AlertTitle>
                    <AlertDescription>
                      <p>
                        {deleteError}
                      </p>
                      <p className="app-card-note">
                        Clear the utility/fallback bindings and unpin the pipelines that reference this profile, or use
                        the shortcut below to clear them and delete in one step.
                      </p>
                      <p>
                        <Button
                          type="button"
                          size="sm"
                          variant="destructive"
                          onClick={() => void clearBindingsAndDelete()}
                          disabled={busy || clearing}
                        >
                          {clearing ? 'Clearing…' : 'Clear bindings/pins and delete'}
                        </Button>
                      </p>
                    </AlertDescription>
                  </Alert>
                )}

                {flash === null ? null : <Badge variant="success">{flash}</Badge>}
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle as="h2">Facts</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="app-facts">
                <div className="app-row">
                  <Badge variant="outline">{profile.flavourLabel}</Badge>
                </div>
                <div>
                  engine: <code className="app-code">{flavourFor(view, profile)?.engine ?? '—'}</code>
                </div>
                <p className="app-card-note">{flavourFor(view, profile)?.fact ?? ''}</p>
                <p className="app-card-note">{flavourFor(view, profile)?.catalogNote ?? ''}</p>
                <p className="app-card-note">
                  referenced by:{' '}
                  {(view.referrers[profile.id] ?? []).length === 0
                    ? 'nothing yet'
                    : (view.referrers[profile.id] ?? []).join(', ')}
                </p>
                <div>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => void deleteProfile()}
                    disabled={busy || clearing}
                  >
                    Delete profile…
                  </Button>
                </div>
                <p className="app-hint">
                  Deleting a pinned profile is refused with the referrer list. Clear the listed pins/bindings, or use
                  “Clear bindings/pins and delete” in the alert above.
                </p>
              </div>
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  );
}

function BindingCard({
  title,
  note,
  mandatory,
  profiles,
  binding,
  modelRequired,
  onChange,
  onSave,
  flash,
  busy,
  onCreate,
}: {
  title: string;
  note: string;
  mandatory?: boolean;
  profiles: readonly ProfileSummary[];
  binding: UtilityBinding | null;
  modelRequired: boolean;
  onChange: (binding: UtilityBinding | null) => void;
  onSave: () => void;
  flash: string | null;
  busy: boolean;
  onCreate?: () => void;
}) {
  const profile = profiles.find((entry) => entry.id === binding?.profileId);
  const models = profile?.models ?? [];
  const unset = binding === null || !binding.profileId;
  return (
    <Card>
      <CardHeader>
        <CardTitle as="h2">
          <span className="app-title-row">
            {title}
            {mandatory ? <Badge variant="secondary">Required</Badge> : null}
          </span>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="app-binding">
          <p className="app-card-note">{note}</p>
          {mandatory && unset ? (
            <Alert variant="destructive">
              <AlertTitle>Required — not set</AlertTitle>
              <AlertDescription>
                <p>
                  Utility sessions have no default. Create a profile first, then select it below — engine defaults are not
                  used.
                </p>
                {onCreate === undefined ? null : (
                  <p>
                    <Button type="button" size="sm" variant="outline" onClick={onCreate}>
                      + create profile
                    </Button>
                  </p>
                )}
              </AlertDescription>
            </Alert>
          ) : null}
          <div className="app-field-row">
            <div className="app-field">
              <span className="app-label">Profile</span>
              <NativeSelect
                size="sm"
                aria-label="Binding profile"
                value={binding?.profileId ?? ''}
                onChange={(event) => {
                  const value = event.target.value;
                  onChange(value.length > 0 ? { profileId: value, modelId: null } : null);
                }}
              >
                <option value="">
                  {mandatory
                    ? '(required — select a profile)'
                    : modelRequired
                      ? '(unset — engine defaults, no pin fallback)'
                      : '(engine default — triage/planning unchanged)'}
                </option>
                {profiles
                  .filter((entry) => entry.id.length > 0)
                  .map((entry) => (
                    <option key={entry.id} value={entry.id}>
                      {entry.name}
                    </option>
                  ))}
              </NativeSelect>
            </div>
            <div className="app-field">
              <span className="app-label">
                Model {modelRequired ? '(required — the binding is atomic)' : ''}
              </span>
              <NativeSelect
                size="sm"
                aria-label="Binding model"
                value={binding?.modelId ?? ''}
                disabled={profile === undefined}
                onChange={(event) => {
                  const value = event.target.value;
                  onChange({
                    profileId: binding?.profileId ?? null,
                    modelId: value.length > 0 ? value : null,
                  });
                }}
              >
                {profile === undefined ? (
                  <option value="">—</option>
                ) : (
                  <>
                    {modelRequired ? null : <option value="">(engine default model)</option>}
                    {models.map((model) => (
                      <option key={model} value={model}>
                        {model}
                      </option>
                    ))}
                  </>
                )}
              </NativeSelect>
            </div>
          </div>
          <div className="app-row">
            <Button
              type="button"
              size="sm"
              onClick={onSave}
              disabled={busy || (mandatory === true && unset)}
            >
              Save binding
            </Button>
            {flash === null ? null : <Badge variant="success">{flash}</Badge>}
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

function Palette({
  profile,
  flavour,
  catalog,
  external,
  modelOptions,
  filter,
  onFilter,
  onToggle,
  onRestart,
  busy,
}: {
  profile: ProfileSummary;
  flavour: Flavour | undefined;
  catalog: Reading<Catalog>;
  external: Reading<Catalog>;
  modelOptions: string[];
  filter: string;
  onFilter: (value: string) => void;
  onToggle: (model: string) => void;
  onRestart: () => void;
  busy: boolean;
}) {
  const provider = providerId(profile.flavour);
  const live = provider !== null;
  const loading = (catalog.phase === 'loading' || external.phase === 'loading') && modelOptions.length === 0;
  const rows = modelOptions
    .filter((model) => filter.trim().length === 0 || model.toLowerCase().includes(filter.toLowerCase()))
    .map((model) => ({ id: model, outOfCatalog: !profile.models.includes(model) && false }));

  return (
    <div className="app-stack app-stack--sm">
      {loading ? (
        <p className="app-hint">Loading the Go models list (serve snapshot + public models endpoint)…</p>
      ) : (
        <>
          {live ? (
            <Input
              placeholder={`filter ${modelOptions.length} opencode-go models…`}
              value={filter}
              onChange={(event) => onFilter(event.target.value)}
            />
          ) : null}
          <div className="app-palette">
            {rows.length === 0 ? (
              <p className="app-hint">
                No models in this snapshot yet — if you just saved a key, hit “Refresh catalog” or
                “restart serve to refresh” to bake it in.
              </p>
            ) : (
              rows.map((row) => (
                <label className="app-palette__row" key={row.id}>
                  <Checkbox
                    checked={profile.models.includes(row.id)}
                    onCheckedChange={() => onToggle(row.id)}
                    aria-label={`Select ${row.id}`}
                  />
                  <span className={row.id.includes('-free') ? 'app-palette__free' : undefined}>{row.id}</span>
                </label>
              ))
            )}
          </div>
        </>
      )}
      <p className="app-hint">
        source: {flavour?.catalogNote ?? '—'} · {flavour?.keylessNote ?? '—'}{' '}
        {live ? (
          <Button type="button" size="sm" variant="ghost" onClick={onRestart} disabled={busy}>
            restart serve to refresh
          </Button>
        ) : null}
      </p>
    </div>
  );
}

function providerId(flavour: string): string | null {
  switch (flavour) {
    case 'OpencodeGo':
      return 'opencode-go';
    default:
      return null;
  }
}

function flavourFor(view: ProfilesView, profile: ProfileSummary): Flavour | undefined {
  return view.flavours.find((entry) => entry.value === profile.flavour);
}
