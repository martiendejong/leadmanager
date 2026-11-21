import React, { useState, useRef } from 'react'
import { createLeadWithDupCheck, mergeLead, uploadLeadDocuments } from '../../api/leads'
import type { CreateLeadDto, DuplicateLead } from '../../api/leads'
import { useToast } from '../Toast'
import DuplicateModal from './DuplicateModal'
import OrganizationSelect from '../organizations/OrganizationSelect'
import { useOrganizations } from '../../contexts/OrganizationContext'
import { errorMessage } from '../../api/hunt'

interface CreateLeadFormProps {
  onSuccess: () => void
  onCancel: () => void
}

export default function CreateLeadForm({ onSuccess, onCancel }: CreateLeadFormProps) {
  const { showToast } = useToast()
  const { defaultOrgId, rememberOrg } = useOrganizations()
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [selectedFiles, setSelectedFiles] = useState<File[]>([])
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [duplicates, setDuplicates] = useState<DuplicateLead[] | null>(null)
  const pendingFormDataRef = useRef<CreateLeadDto | null>(null)

  const [formData, setFormData] = useState<CreateLeadDto>({
    name: '',
    website: '',
    sector: '',
    city: '',
    phone: '',
    companyEmail: '',
    source: 'Handmatig',
    manualInput: '',
    organizationId: defaultOrgId ?? '',
  })

  const [charCount, setCharCount] = useState(0)
  const [isDragging, setIsDragging] = useState(false)

  const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    const { name, value } = e.target
    setFormData((prev) => ({ ...prev, [name]: value }))

    if (name === 'manualInput') {
      setCharCount(value.length)
    }
  }

  const handleFileSelect = (files: FileList | null) => {
    if (!files) return

    const validTypes = ['.pdf', '.docx', '.txt', 'application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'text/plain']
    const maxSize = 10 * 1024 * 1024 // 10MB

    const validFiles = Array.from(files).filter((file) => {
      const isValidType = validTypes.some((type) =>
        file.type === type || file.name.toLowerCase().endsWith(type)
      )
      const isValidSize = file.size <= maxSize

      if (!isValidType) {
        showToast(`Invalid file type: ${file.name}. Only PDF, DOCX and TXT are allowed.`, 'error')
        return false
      }
      if (!isValidSize) {
        showToast(`File too large: ${file.name}. Maximum 10MB per file.`, 'error')
        return false
      }
      return true
    })

    setSelectedFiles((prev) => {
      const combined = [...prev, ...validFiles]
      if (combined.length > 5) {
        showToast('A maximum of 5 files is allowed.', 'error')
        return prev
      }
      return combined
    })
  }

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(true)
  }

  const handleDragLeave = () => {
    setIsDragging(false)
  }

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(false)
    handleFileSelect(e.dataTransfer.files)
  }

  const removeFile = (index: number) => {
    setSelectedFiles((prev) => prev.filter((_, i) => i !== index))
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()

    if (!formData.organizationId) {
      showToast('Choose the organisation this lead belongs to.', 'error')
      return
    }

    // Validate at least one input type
    const hasWebsite = !!formData.website?.trim()
    const hasManualInput = !!formData.manualInput?.trim()
    const hasDocuments = selectedFiles.length > 0

    if (!hasWebsite && !hasManualInput && !hasDocuments) {
      showToast('Fill in at least one field: website URL, free text or upload a document.', 'error')
      return
    }

    // Validate ManualInput length
    if (formData.manualInput && formData.manualInput.length > 5000) {
      showToast('Free text can contain at most 5000 characters.', 'error')
      return
    }

    setIsSubmitting(true)

    try {
      const result = await createLeadWithDupCheck(formData, false)

      if ('duplicates' in result) {
        // Show duplicate modal
        pendingFormDataRef.current = formData
        setDuplicates(result.duplicates)
        setIsSubmitting(false)
        return
      }

      const lead = result.lead

      // Upload documents if any
      if (selectedFiles.length > 0) {
        await uploadLeadDocuments(lead.id, selectedFiles)
      }

      if (formData.organizationId) rememberOrg(formData.organizationId)
      showToast('Lead created successfully!', 'success')
      onSuccess()
    } catch (err: any) {
      console.error('Failed to create lead:', err)
      showToast(errorMessage(err, 'Could not create lead.'), 'error')
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleMerge = async (existingLeadId: string) => {
    if (!pendingFormDataRef.current) return
    setIsSubmitting(true)
    setDuplicates(null)
    try {
      // Create the new lead with force, then merge it into the existing one
      const result = await createLeadWithDupCheck(pendingFormDataRef.current, true)
      if ('lead' in result) {
        await mergeLead(existingLeadId, result.lead.id)
        showToast('Leads merged!', 'success')
        onSuccess()
      }
    } catch (err: any) {
      showToast(errorMessage(err, 'Could not merge leads.'), 'error')
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleForce = async () => {
    if (!pendingFormDataRef.current) return
    setIsSubmitting(true)
    setDuplicates(null)
    try {
      const result = await createLeadWithDupCheck(pendingFormDataRef.current, true)
      if ('lead' in result) {
        if (selectedFiles.length > 0) {
          await uploadLeadDocuments(result.lead.id, selectedFiles)
        }
        showToast('Lead created successfully!', 'success')
        onSuccess()
      }
    } catch (err: any) {
      showToast(errorMessage(err, 'Could not create lead.'), 'error')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <>
    {duplicates && (
      <DuplicateModal
        duplicates={duplicates}
        onMerge={handleMerge}
        onForce={handleForce}
        onCancel={() => setDuplicates(null)}
      />
    )}
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full max-h-[90vh] overflow-y-auto">
        <div className="sticky top-0 bg-white border-b px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold">Create new lead</h2>
          <button
            onClick={onCancel}
            className="text-gray-400 hover:text-gray-600 text-2xl leading-none"
            type="button"
          >
            ×
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-6">
          {/* Basic Information */}
          <div>
            <h3 className="font-medium mb-3">Basic information</h3>
            <div className="grid grid-cols-2 gap-4">
              <OrganizationSelect
                className="col-span-2"
                id="create-lead-organization"
                value={formData.organizationId ?? ''}
                onChange={(id) => setFormData((prev) => ({ ...prev, organizationId: id }))}
              />
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Company name <span className="text-red-500">*</span>
                </label>
                <input
                  type="text"
                  name="name"
                  value={formData.name}
                  onChange={handleChange}
                  required
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                  placeholder="For example: ACME Ltd"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Sector</label>
                <input
                  type="text"
                  name="sector"
                  value={formData.sector}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                  placeholder="For example: IT / Marketing"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">City</label>
                <input
                  type="text"
                  name="city"
                  value={formData.city}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                  placeholder="Amsterdam"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Phone</label>
                <input
                  type="text"
                  name="phone"
                  value={formData.phone}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                  placeholder="06-12345678"
                />
              </div>

              <div className="col-span-2">
                <label className="block text-sm font-medium text-gray-700 mb-1">Company email</label>
                <input
                  type="email"
                  name="companyEmail"
                  value={formData.companyEmail}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                  placeholder="info@company.com"
                />
              </div>
            </div>
          </div>

          {/* Input Methods - Visually Grouped */}
          <div>
            <h3 className="font-medium mb-2">Input method</h3>
            <p className="text-sm text-gray-600 mb-4">
              Choose at least one method to add information about the company
            </p>

            <div className="space-y-4">
              {/* Website URL (Optional) */}
              <div className="border rounded-lg p-4 bg-gray-50">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  <span className="inline-block w-6 h-6 rounded-full bg-blue-100 text-blue-600 text-center mr-2">1</span>
                  Website URL (optional)
                </label>
                <input
                  type="url"
                  name="website"
                  value={formData.website ?? ''}
                  onChange={handleChange}
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 bg-white"
                  placeholder="https://www.company.com"
                />
              </div>

              {/* Free Text Input */}
              <div className="border rounded-lg p-4 bg-gray-50">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  <span className="inline-block w-6 h-6 rounded-full bg-green-100 text-green-600 text-center mr-2">2</span>
                  Free text input (optional)
                </label>
                <p className="text-xs text-gray-500 mb-2">
                  Paste a LinkedIn bio, business card or company description
                </p>
                <textarea
                  name="manualInput"
                  value={formData.manualInput ?? ''}
                  onChange={handleChange}
                  rows={5}
                  maxLength={5000}
                  className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-green-500 bg-white font-mono text-sm"
                  placeholder="Ramon Conijn, eigenaar, Amsterdam, 06-29809397..."
                />
                <div className="text-right text-xs text-gray-500 mt-1">
                  {charCount} / 5000 characters
                </div>
              </div>

              {/* Document Upload */}
              <div className="border rounded-lg p-4 bg-gray-50">
                <label className="block text-sm font-medium text-gray-700 mb-2">
                  <span className="inline-block w-6 h-6 rounded-full bg-purple-100 text-purple-600 text-center mr-2">3</span>
                  Upload documents (optional)
                </label>
                <p className="text-xs text-gray-500 mb-2">
                  PDF, DOCX or TXT - max 5 files, 10MB per file
                </p>

                <div
                  onDragOver={handleDragOver}
                  onDragLeave={handleDragLeave}
                  onDrop={handleDrop}
                  className={`border-2 border-dashed rounded-lg p-6 text-center cursor-pointer transition-colors ${
                    isDragging
                      ? 'border-purple-500 bg-purple-50'
                      : 'border-gray-300 bg-white hover:border-purple-400'
                  }`}
                  onClick={() => fileInputRef.current?.click()}
                >
                  <input
                    ref={fileInputRef}
                    type="file"
                    multiple
                    accept=".pdf,.docx,.txt"
                    onChange={(e) => handleFileSelect(e.target.files)}
                    className="hidden"
                  />
                  <div className="text-gray-600">
                    <svg
                      className="mx-auto h-12 w-12 text-gray-400"
                      stroke="currentColor"
                      fill="none"
                      viewBox="0 0 48 48"
                    >
                      <path
                        d="M28 8H12a4 4 0 00-4 4v20m32-12v8m0 0v8a4 4 0 01-4 4H12a4 4 0 01-4-4v-4m32-4l-3.172-3.172a4 4 0 00-5.656 0L28 28M8 32l9.172-9.172a4 4 0 015.656 0L28 28m0 0l4 4m4-24h8m-4-4v8m-12 4h.02"
                        strokeWidth={2}
                        strokeLinecap="round"
                        strokeLinejoin="round"
                      />
                    </svg>
                    <p className="mt-2">
                      <span className="font-medium text-purple-600">Click to upload</span> or
                      drag files here
                    </p>
                  </div>
                </div>

                {selectedFiles.length > 0 && (
                  <ul className="mt-3 space-y-2">
                    {selectedFiles.map((file, index) => (
                      <li
                        key={index}
                        className="flex items-center justify-between bg-white px-3 py-2 rounded border"
                      >
                        <div className="flex items-center space-x-2 text-sm">
                          <svg
                            className="h-5 w-5 text-purple-600"
                            fill="none"
                            stroke="currentColor"
                            viewBox="0 0 24 24"
                          >
                            <path
                              strokeLinecap="round"
                              strokeLinejoin="round"
                              strokeWidth={2}
                              d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                            />
                          </svg>
                          <span>{file.name}</span>
                          <span className="text-gray-500">
                            ({(file.size / 1024 / 1024).toFixed(2)} MB)
                          </span>
                        </div>
                        <button
                          type="button"
                          onClick={() => removeFile(index)}
                          className="text-red-600 hover:text-red-800"
                        >
                          <svg className="h-5 w-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path
                              strokeLinecap="round"
                              strokeLinejoin="round"
                              strokeWidth={2}
                              d="M6 18L18 6M6 6l12 12"
                            />
                          </svg>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </div>
          </div>

          {/* Action Buttons */}
          <div className="flex justify-end space-x-3 pt-4 border-t">
            <button
              type="button"
              onClick={onCancel}
              className="px-4 py-2 border rounded-md hover:bg-gray-50"
              disabled={isSubmitting}
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSubmitting}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
            >
              {isSubmitting ? 'Creating...' : 'Create lead'}
            </button>
          </div>
        </form>
      </div>
    </div>
    </>
  )
}
