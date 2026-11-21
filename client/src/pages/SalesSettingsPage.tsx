import { useEffect, useState } from 'react';
import type { SalesSettings } from '../api/salesSettings';
import { salesSettingsApi } from '../api/salesSettings';
import MessageTemplatesPanel from '../components/messages/MessageTemplatesPanel';
import { Link } from 'react-router-dom';

type Tab = 'company' | 'pricing' | 'quote' | 'script' | 'email' | 'messages' | 'rules' | 'fedha';

const TABS: { id: Tab; label: string }[] = [
  { id: 'company', label: 'Company details' },
  { id: 'pricing', label: 'Pricing' },
  { id: 'quote', label: 'Quote' },
  { id: 'script', label: 'Call script template' },
  { id: 'email', label: 'Email template' },
  { id: 'messages', label: 'Message templates' },
  { id: 'rules', label: 'Client rules' },
  { id: 'fedha', label: 'Fedha integration' },
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
    salesSettingsApi.get().then(setSettings).catch(() => setError('Could not load settings'));
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
      setError('Could not save');
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
        <h1 className="text-2xl font-bold text-gray-900">Settings</h1>
        <p className="text-gray-500 text-sm mt-1">Configure company details, pricing, templates and integrations.</p>
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
        <Link
          to="/settings/organizations"
          className="px-4 py-2 text-sm font-medium rounded-t-lg transition-colors text-gray-600 hover:text-indigo-600 hover:bg-indigo-50"
        >
          Organisations
        </Link>
      </div>

      {/* Panel */}
      <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-4">
        {activeTab === 'company' && (
          <>
            <Field label="Company name" value={settings.companyName} onChange={v => update({ companyName: v })} />
            <Field label="Address" value={settings.companyAddress ?? ''} onChange={v => update({ companyAddress: v })} />
            <div className="grid grid-cols-2 gap-4">
              <Field label="Postal code" value={settings.companyZipCode ?? ''} onChange={v => update({ companyZipCode: v })} />
              <Field label="City" value={settings.companyCity ?? ''} onChange={v => update({ companyCity: v })} />
            </div>
            <div className="grid grid-cols-2 gap-4">
              <Field label="Chamber of Commerce number" value={settings.companyKvk ?? ''} onChange={v => update({ companyKvk: v })} />
              <Field label="VAT number" value={settings.companyVat ?? ''} onChange={v => update({ companyVat: v })} />
            </div>
            <Field label="IBAN" value={settings.companyIban ?? ''} onChange={v => update({ companyIban: v })} />
            <div className="grid grid-cols-2 gap-4">
              <Field label="Email address" value={settings.companyEmail ?? ''} onChange={v => update({ companyEmail: v })} type="email" />
              <Field label="Phone number" value={settings.companyPhone ?? ''} onChange={v => update({ companyPhone: v })} />
            </div>
            <Field label="Website" value={settings.companyWebsite ?? ''} onChange={v => update({ companyWebsite: v })} />
          </>
        )}

        {activeTab === 'pricing' && (
          <>
            <p className="text-sm text-gray-500">Bundle prices used by default in quotes and intakes.</p>
            <div className="grid grid-cols-2 gap-4">
              <Field label="Starter hours/month" value={settings.starterHours} type="number" onChange={v => update({ starterHours: Number(v) })} />
              <Field label="Starter price/month" value={settings.starterMonthlyPrice} type="number" prefix="€" onChange={v => update({ starterMonthlyPrice: Number(v) })} />
            </div>
            <div className="grid grid-cols-2 gap-4">
              <Field label="Team hours/month" value={settings.teamHours} type="number" onChange={v => update({ teamHours: Number(v) })} />
              <Field label="Team price/month" value={settings.teamMonthlyPrice} type="number" prefix="€" onChange={v => update({ teamMonthlyPrice: Number(v) })} />
            </div>
            <div className="grid grid-cols-3 gap-4">
              <Field label="Bundle hourly rate" value={settings.bundleHourlyRate} type="number" prefix="€" onChange={v => update({ bundleHourlyRate: Number(v) })} />
              <Field label="Loose hourly rate" value={settings.looseHourlyRate} type="number" prefix="€" onChange={v => update({ looseHourlyRate: Number(v) })} />
              <Field label="Overage hourly rate" value={settings.overageHourlyRate} type="number" prefix="€" onChange={v => update({ overageHourlyRate: Number(v) })} />
            </div>
          </>
        )}

        {activeTab === 'quote' && (
          <>
            <div className="grid grid-cols-3 gap-4">
              <Field label="Quote number prefix" value={settings.quoteNumberPrefix} onChange={v => update({ quoteNumberPrefix: v })} />
              <Field label="Current number" value={settings.quoteNumberCurrent} type="number" onChange={v => update({ quoteNumberCurrent: Number(v) })} />
              <Field label="Validity (days)" value={settings.quoteValidityDays} type="number" onChange={v => update({ quoteValidityDays: Number(v) })} />
            </div>
            <Field label="Introduction text" value={settings.quoteIntroText ?? ''} onChange={v => update({ quoteIntroText: v })} rows={3} />
            <Field label="Payment terms" value={settings.quoteTerms ?? ''} onChange={v => update({ quoteTerms: v })} rows={3} />
            <Field label="Footer text" value={settings.quoteFooterText ?? ''} onChange={v => update({ quoteFooterText: v })} rows={2} />
          </>
        )}

        {activeTab === 'script' && (
          <>
            <p className="text-sm text-gray-500 mb-2">
              Call script sections as a JSON array. Each section has: <code>id</code>, <code>title</code>, <code>prompt</code>.
              The AI uses these prompts to write personalised text for each section.
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
            <Field label="Subject template" value={settings.emailSubjectTemplate ?? ''} onChange={v => update({ emailSubjectTemplate: v })} />
            <p className="text-xs text-gray-400">Use {'{{naam}}'}, {'{{bedrijf}}'}, {'{{offerte}}'} as variables.</p>
            <Field label="Message body template" value={settings.emailBodyTemplate ?? ''} onChange={v => update({ emailBodyTemplate: v })} rows={10} />
          </>
        )}

        {activeTab === 'rules' && (
          <>
            <p className="text-sm text-gray-500">
              Rules of engagement for clients and prospects: when we do (or do not) do free work, how often we follow up,
              and the order contract → work. Set a client to the <strong>Reactive</strong> category on the
              client page to show the related warning.
            </p>
            <Field
              label="Client rules"
              value={settings.clientEngagementRules ?? ''}
              onChange={v => update({ clientEngagementRules: v })}
              rows={10}
            />
          </>
        )}

        {activeTab === 'messages' && <MessageTemplatesPanel />}

        {activeTab === 'fedha' && (
          <>
            <p className="text-sm text-gray-500">Connect the Fedha invoicing system to create invoices directly from the app.</p>
            <Field label="Fedha base URL" value={settings.fedhaBaseUrl ?? ''} onChange={v => update({ fedhaBaseUrl: v })} />
            <Field label="API key" value={settings.fedhaApiKey ?? ''} onChange={v => update({ fedhaApiKey: v })} type="password" />
            <Field label="Default project ID" value={settings.fedhaDefaultProjectId ?? ''} onChange={v => update({ fedhaDefaultProjectId: v })} />
          </>
        )}
      </div>

      {/* Save bar (the message templates have their own save button) */}
      <div className={`mt-4 flex items-center gap-3 ${activeTab === 'messages' ? 'hidden' : ''}`}>
        <button
          onClick={save}
          disabled={saving}
          className="px-5 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium hover:bg-indigo-700 disabled:opacity-60 transition-colors"
        >
          {saving ? 'Saving...' : 'Save'}
        </button>
        {saved && <span className="text-green-600 text-sm">Saved!</span>}
        {error && <span className="text-red-600 text-sm">{error}</span>}
      </div>
    </div>
  );
}
