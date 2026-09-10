namespace LeadManager.Api.DTOs;

public record SalesSettingsDto(
    Guid Id,
    string CompanyName,
    string? CompanyAddress,
    string? CompanyCity,
    string? CompanyZipCode,
    string? CompanyKvk,
    string? CompanyVat,
    string? CompanyIban,
    string? CompanyEmail,
    string? CompanyPhone,
    string? CompanyWebsite,
    decimal StarterHours,
    decimal StarterMonthlyPrice,
    decimal TeamHours,
    decimal TeamMonthlyPrice,
    decimal BundleHourlyRate,
    decimal LooseHourlyRate,
    decimal OverageHourlyRate,
    string QuoteNumberPrefix,
    int QuoteNumberCurrent,
    int QuoteValidityDays,
    string? QuoteIntroText,
    string? QuoteTerms,
    string? QuoteFooterText,
    string? CallScriptSectionsJson,
    string? EmailSubjectTemplate,
    string? EmailBodyTemplate,
    string? FedhaBaseUrl,
    string? FedhaApiKey,
    string? FedhaDefaultProjectId
);

public record UpdateSalesSettingsDto(
    string CompanyName,
    string? CompanyAddress,
    string? CompanyCity,
    string? CompanyZipCode,
    string? CompanyKvk,
    string? CompanyVat,
    string? CompanyIban,
    string? CompanyEmail,
    string? CompanyPhone,
    string? CompanyWebsite,
    decimal StarterHours,
    decimal StarterMonthlyPrice,
    decimal TeamHours,
    decimal TeamMonthlyPrice,
    decimal BundleHourlyRate,
    decimal LooseHourlyRate,
    decimal OverageHourlyRate,
    string QuoteNumberPrefix,
    int QuoteNumberCurrent,
    int QuoteValidityDays,
    string? QuoteIntroText,
    string? QuoteTerms,
    string? QuoteFooterText,
    string? CallScriptSectionsJson,
    string? EmailSubjectTemplate,
    string? EmailBodyTemplate,
    string? FedhaBaseUrl,
    string? FedhaApiKey,
    string? FedhaDefaultProjectId
);

public record GenerateScriptRequest(Guid LeadId, string? ExtraContext);
public record GenerateScriptResponse(string Script, string LeadName);

public record GenerateQuoteRequest(
    Guid LeadId,
    string ProductType,
    string BundleType,
    string? CustomDescription,
    decimal? CustomHours,
    decimal? CustomPrice
);
