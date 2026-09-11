// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Input validation for <see cref="MigrationAssessmentRequest"/>. Pure and offline.</summary>
public static class RequestValidator
{
    public const int MaxEvidenceItems = 500;

    public static RequestValidationResult Validate(MigrationAssessmentRequest? request)
    {
        if (request is null)
        {
            return new RequestValidationResult(false, ["Request is null."]);
        }

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(request.EngagementId))
        {
            errors.Add("EngagementId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationName))
        {
            errors.Add("ApplicationName is required.");
        }

        if (FleetGuardrails.ContainsPotentialSecret(request.EngagementId) ||
            FleetGuardrails.ContainsPotentialSecret(request.ApplicationName) ||
            FleetGuardrails.ContainsPotentialSecret(request.OracleFormsVersion))
        {
            errors.Add("Request identifiers appear to contain credential material and were rejected.");
        }

        if (request.Evidence is null)
        {
            errors.Add("Evidence must be an array.");
        }
        else if (request.Evidence.Count > MaxEvidenceItems)
        {
            errors.Add($"Evidence exceeds the maximum of {MaxEvidenceItems} items.");
        }

        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (EvidenceItem? item in request.Evidence ?? [])
        {
            if (item is null)
            {
                errors.Add("Evidence items cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Id))
            {
                errors.Add("Every evidence item requires an Id.");
            }
            else if (!seenIds.Add(item.Id))
            {
                errors.Add($"Duplicate evidence id '{item.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(item.Source))
            {
                errors.Add($"Evidence '{item.Id}' requires a Source.");
            }

            if (!Enum.IsDefined(item.Kind))
            {
                errors.Add($"Evidence '{item.Id}' has an unsupported Kind value.");
            }

            if (item.Signals is null)
            {
                errors.Add($"Evidence '{item.Id}' Signals must be an array.");
            }
            else
            {
                foreach (WorkloadSignal signal in item.Signals.Where(signal => !Enum.IsDefined(signal)))
                {
                    errors.Add($"Evidence '{item.Id}' has an unsupported signal value '{(int)signal}'.");
                }
            }

            if (FleetGuardrails.ContainsPotentialSecret(item.Id) ||
                FleetGuardrails.ContainsPotentialSecret(item.Source) ||
                FleetGuardrails.ContainsPotentialSecret(item.Summary))
            {
                errors.Add($"Evidence '{item.Id}' appears to contain credential material and was rejected.");
            }
        }

        if (request.BusinessConstraints is null)
        {
            errors.Add("BusinessConstraints must be an array.");
        }
        else if (request.BusinessConstraints.Any(FleetGuardrails.ContainsPotentialSecret))
        {
            errors.Add("BusinessConstraints appear to contain credential material and were rejected.");
        }

        if (request.Approval is null)
        {
            errors.Add("Approval is required.");
        }
        else if (!Enum.IsDefined(request.Approval.Decision))
        {
            errors.Add("Approval has an unsupported Decision value.");
        }
        else if (request.Approval.Decision == ApprovalDecision.Approved &&
                 string.IsNullOrWhiteSpace(request.Approval.ApproverId))
        {
            errors.Add("An Approved decision requires an ApproverId.");
        }

        if (request.Approval is not null &&
            (FleetGuardrails.ContainsPotentialSecret(request.Approval.ApproverId) ||
             FleetGuardrails.ContainsPotentialSecret(request.Approval.Notes)))
        {
            errors.Add("Approval details appear to contain credential material and were rejected.");
        }

        return new RequestValidationResult(errors.Count == 0, errors);
    }
}
