import apiClient from './client';

export interface SalesSettings {
  id: string;
  companyName: string;
  companyAddress?: string;
  companyCity?: string;
  companyZipCode?: string;
  companyKvk?: string;
  companyVat?: string;
  companyIban?: string;
  companyEmail?: string;
  companyPhone?: string;
  companyWebsite?: string;
  starterHours: number;
  starterMonthlyPrice: number;
  teamHours: number;
  teamMonthlyPrice: number;
  bundleHourlyRate: number;
  looseHourlyRate: number;
  overageHourlyRate: number;
  quoteNumberPrefix: string;
  quoteNumberCurrent: number;
  quoteValidityDays: number;
  quoteIntroText?: string;
  quoteTerms?: string;
  quoteFooterText?: string;
  callScriptSectionsJson?: string;
  emailSubjectTemplate?: string;
  emailBodyTemplate?: string;
  clientEngagementRules?: string;
  fedhaBaseUrl?: string;
  fedhaApiKey?: string;
  fedhaDefaultProjectId?: string;
}

export interface GenerateScriptRequest {
  leadId: string;
  extraContext?: string;
}

export interface GenerateScriptResponse {
  script: string;
  leadName: string;
}

export interface GenerateQuoteRequest {
  leadId: string;
  productType: string;
  bundleType: string;
  customDescription?: string;
  customHours?: number;
  customPrice?: number;
}

export const salesSettingsApi = {
  get: async (): Promise<SalesSettings> => {
    const res = await apiClient.get('/sales-settings');
    return res.data;
  },

  update: async (data: Partial<SalesSettings>): Promise<SalesSettings> => {
    const res = await apiClient.put('/sales-settings', data);
    return res.data;
  },

  generateScript: async (req: GenerateScriptRequest): Promise<GenerateScriptResponse> => {
    const res = await apiClient.post('/sales-settings/generate-script', req);
    return res.data;
  },

  generateQuote: async (req: GenerateQuoteRequest): Promise<Blob> => {
    const res = await apiClient.post('/offerte/generate', req, { responseType: 'blob' });
    return res.data;
  },
};
