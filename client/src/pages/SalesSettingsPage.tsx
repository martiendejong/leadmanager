import { useEffect, useState } from 'react';
import type { SalesSettings } from '../api/salesSettings';
import { salesSettingsApi } from '../api/salesSettings';

type Tab = 'company' | 'pricing' | 'quote' | 'script' | 'email' | 'fedha';

const TABS: { id: Tab; label: string }[] = [
  { id: 'company', label: 'Bedrijfsgegevens' },
  { id: 'pricing', label: 'Prijzen' },
  { id: 'quote', label: 'Offerte' },
  { id: 'script', label: 'Belscript template' },
  { id: 'email', label: 'E-mail template' },
  { id: 'fedha', label: 'Fedha koppeling' },
];

function Field({
  label, value, onChange, type = 'text', prefix, suffix, rows,
}: {
  label: string;
  value: string | number;
  onChange: (v: string) => void;
  type?: string;
  prefix?: string;
  suffix?: string;
  rows?: number;
}) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      <div className="flex items-center gap-1">
        {prefix && <span className="text-gray-500 text-sm">{prefix}</span>}
        {rows ? (
          <textarea
            className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
            value={value as string}
            onChange={e => onChange(e.target.value)}
            rows={rows}
          />
        ) : (
          <input
            type={type}
            className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
            value={value}
            onChange={e => onChange(e.target.value)}
          />
        )}
        {suffix && <span className="text-gray-500 text-sm">{suffix}</span>}
      </div>
    </div>
  );
}

export default function SalesSettingsPage() {
  const [settings, setSettings] = useState<SalesSettings | null>(null);
  const [activeTab, setActiveTab] = useState<Tab>('company');
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    salesSettingsApi.get().then(setSettings).catch(() => setError('Instellingen laden mislukt'));
  }, []);

  const update = (patch: Partial<SalesSettings>) =>
    setSettings(s => s ? { ...s, ...patch } : s);

  const save = async () => {
    if (!settings) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await salesSettingsApi.update(settings);
      setSettings(updated);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch {
      setError('Opslaan mislukt');
    } finally {
      setSaving(false);
    }
  };

  if (!settings) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
      </div>
    );
  }

  return (
    <div className="max-w-3xl mx-auto px-4 py-8">
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Instellingen</h1>
        <p className="text-gray-500 text-sm mt-1">Configureer bedrijfsgegevens, prijzen, templates en koppelingen.</p>
      </div>

      {/* Tabs */}
      <div className="flex gap-1 mb-6 flex-wrap border-b border-gray-200">
        {TABS.map(tab => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(tab.id)}
            className={`px-4 py-2 text-sm font-medium rounded-t-lg transition-colors ${
              activeTab === tab.id
                ? 'bg-indigo-600 text-white'
                : 'text-gray-600 hover:text-indigo-600 hover:bg-indigo-50'
            }`}
          >
            {tab.label}
          </button>
        ))}
      </div>

      {/* Panel */}
      <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-4">
        {activeTab === 'company' && (
          <>
            <Field label="Bedrijfsnaam" value={settings.companyName} onChange={v => update({ companyName: v })} />
            <Field label="Adres" value={settings.companyAddress ?? ''} onChange={v => update({ companyAddress: v })} />
            <div className="grid grid-cols-2 gap-4">
              <Field label="Postcode" value={settings.companyZipCode ?? ''} onChange={v => update({ companyZipCode: v })} />
              <Field label="Stad" value={settings.companyCity ?? ''} onChange={v => update({ companyCity: v })} />
            </div>
            <div className="grid grid-cols-2 gap-4">
              <Field label="KvK-nummer" value={settings.companyKvk ?? ''} onChange={v => update({ companyKvk: v })} />
              <Field label="BTW-nummer" value={settings.companyVat ?? ''} onChange={v => update({ companyVat: v })} />
            </div>
            <Field label="IBAN" value={settings.companyIban ?? ''} onChange={v => update({ companyIban: v })} />
            <div className="grid grid-cols-2 gap-4">
              <Field label="E-mailadres" value={settings.companyEmail ?? ''} onChange={v => update({ companyEmail: v })} type="email" />
              <Field label="Telefoonnummer" value={settings.companyPhone ?? ''} onChange={v => update({ companyPhone: v })} />
            </div>
            <Field label="Website" value={settings.companyWebsite ?? ''} onChange={v => update({ companyWebsite: v })} />
          </>
        )}

        {activeTab === 'pricing' && (
          <>
            <p className="text-sm text-gray-500">Bundelprijzen die standaard worden gebruikt in offertes en intakes.</p>
            <div className="grid grid-cols-2 gap-4">
              <Field label="Starter uren/maand" value={settings.starterHours} type="number" onChange={v => update({ starterHours: Number(v) })} />
              <Field label="Starter prijs/maand" value={settings.starterMonthlyPrice} type="number" prefix="€" onChange={v => update({ starterMonthlyPrice: Number(v) })} />
            </div>
            <div className="grid grid-cols-2 gap-4">
              <Field label="Team uren/maand" value={settings.teamHours} type="number" onChange={v => update({ teamHours: Number(v) })} />
              <Field label="Team prijs/maand" value={settings.teamMonthlyPrice} type="number" prefix="€" onChange={v => update({ teamMonthlyPrice: Number(v) })} />
            </div>
            <div className="grid grid-cols-3 gap-4">
              <Field label="Bundel uurtarief" value={settings.bundleHourlyRate} type="number" prefix="€" onChange={v => update({ bundleHourlyRate: Number(v) })} />
              <Field label="Los uurtarief" value={settings.looseHourlyRate} type="number" prefix="€" onChange={v => update({ looseHourlyRate: Number(v) })} />
              <Field label="Overage uurtarief" value={settings.overageHourlyRate} type="number" prefix="€" onChange={v => update({ overageHourlyRate: Number(v) })} />
            </div>
          </>
        )}

        {activeTab === 'quote' && (
          <>
            <div className="grid grid-cols-3 gap-4">
              <Field label="Offertenummer prefix" value={settings.quoteNumberPrefix} onChange={v => update({ quoteNumberPrefix: v })} />
              <Field label="Huidig nummer" value={settings.quoteNumberCurrent} type="number" onChange={v => update({ quoteNumberCurrent: Number(v) })} />
              <Field label="Geldigheid (dagen)" value={settings.quoteValidityDays} type="number" onChange={v => update({ quoteValidityDays: Number(v) })} />
            </div>
            <Field label="Introductietekst" value={settings.quoteIntroText ?? ''} onChange={v => update({ quoteIntroText: v })} rows={3} />
            <Field label="Betalingsvoorwaarden" value={settings.quoteTerms ?? ''} onChange={v => update({ quoteTerms: v })} rows={3} />
            <Field label="Voettekst" value={settings.quoteFooterText ?? ''} onChange={v => update({ quoteFooterText: v })} rows={2} />
          </>
        )}

        {activeTab === 'script' && (
          <>
            <p className="text-sm text-gray-500 mb-2">
              Belscript-secties als JSON-array. Elke sectie heeft: <code>id</code>, <code>title</code>, <code>prompt</code>.
              De AI gebruikt deze prompts om per sectie een gepersonaliseerde tekst te schrijven.
            </p>
            <textarea
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm font-mono focus:outline-none focus:ring-2 focus:ring-indigo-500"
              value={settings.callScriptSectionsJson ?? ''}
              onChange={e => update({ callScriptSectionsJson: e.target.value })}
              rows={14}
              placeholder='[{"id":"opening","title":"Opening","prompt":"..."}]'
            />
          </>
        )}

        {activeTab === 'email' && (
          <>
            <Field label="Onderwerp template" value={settings.emailSubjectTemplate ?? ''} onChange={v => update({ emailSubjectTemplate: v })} />
            <p className="text-xs text-gray-400">Gebruik {'{{naam}}'}, {'{{bedrijf}}'}, {'{{offerte}}'} als variabelen.</p>
            <Field label="Berichttekst template" value={settings.emailBodyTemplate ?? ''} onChange={v => update({ emailBodyTemplate: v })} rows={10} />
          </>
        )}

        {activeTab === 'fedha' && (
          <>
            <p className="text-sm text-gray-500">Koppel het Fedha facturatiesysteem om direct facturen aan te maken vanuit de app.</p>
            <Field label="Fedha base URL" value={settings.fedhaBaseUrl ?? ''} onChange={v => update({ fedhaBaseUrl: v })} />
            <Field label="API sleutel" value={settings.fedhaApiKey ?? ''} onChange={v => update({ fedhaApiKey: v })} type="password" />
            <Field label="Standaard project-ID" value={settings.fedhaDefaultProjectId ?? ''} onChange={v => update({ fedhaDefaultProjectId: v })} />
          </>
        )}
      </div>

      {/* Save bar */}
      <div className="mt-4 flex items-center gap-3">
        <button
          onClick={save}
          disabled={saving}
          className="px-5 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium hover:bg-indigo-700 disabled:opacity-60 transition-colors"
        >
          {saving ? 'Opslaan...' : 'Opslaan'}
        </button>
        {saved && <span className="text-green-600 text-sm">Opgeslagen!</span>}
        {error && <span className="text-red-600 text-sm">{error}</span>}
      </div>
    </div>
  );
}
