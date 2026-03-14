import apiClient from './client'

export interface CompanyProfile {
  id: string
  websiteUrl: string
  companyName: string
  description: string
  whatTheyDo: string
  idealCustomerProfile: string
  toneOfVoice: string
  targetSectors: string[]
  targetRegions: string[]
  keywords: string[]
  usps: string[]
  crawledAt: string | null
  profileVersion: number
  updatedAt: string
}

export interface QualifiedLead {
  name: string
  website: string
  city: string
  sector: string
  phone: string
  email: string
  source: string
  confidenceScore: number
  qualificationReason: string
}

export async function getProfile(): Promise<CompanyProfile | null> {
  try {
    const res = await apiClient.get<CompanyProfile>('/api/profile')
    return res.data
  } catch (err: any) {
    if (err.response?.status === 404) return null
    throw err
  }
}

export async function generateProfile(websiteUrl: string): Promise<CompanyProfile> {
  const res = await apiClient.post<CompanyProfile>('/api/profile/generate', { websiteUrl })
  return res.data
}

export async function updateProfile(profile: Omit<CompanyProfile, 'id' | 'crawledAt' | 'profileVersion' | 'updatedAt' | 'websiteUrl'>): Promise<CompanyProfile> {
  const res = await apiClient.put<CompanyProfile>('/api/profile', {
    companyName: profile.companyName,
    description: profile.description,
    whatTheyDo: profile.whatTheyDo,
    idealCustomerProfile: profile.idealCustomerProfile,
    toneOfVoice: profile.toneOfVoice,
    targetSectors: profile.targetSectors,
    targetRegions: profile.targetRegions,
    keywords: profile.keywords,
    usps: profile.usps,
  })
  return res.data
}

export async function searchLeadsWithProfile(): Promise<QualifiedLead[]> {
  const res = await apiClient.post<QualifiedLead[]>('/api/profile/search')
  return res.data
}
