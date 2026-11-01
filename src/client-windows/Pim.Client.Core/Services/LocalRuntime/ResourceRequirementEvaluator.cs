using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Fail-closed resource gate. A requirement that is declared but cannot be
/// measured is a failure; a requirement that is not declared (including GPU on a
/// CPU-only model) is simply not checked.
/// </summary>
public static class ResourceRequirementEvaluator
{
    public static ResourceDecision Evaluate(
        LocalResourceRequirements requirements,
        ResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(snapshot);

        var reasons = new List<string>();

        if (requirements.CpuCores is { } requiredCores)
        {
            if (snapshot.LogicalProcessorCount is null)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceCpuUnknown);
            }
            else if (snapshot.LogicalProcessorCount < requiredCores)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceCpuInsufficient);
            }
        }

        if (requirements.MemoryBytes is { } requiredMemory)
        {
            if (snapshot.AvailableMemoryBytes is null)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceMemoryUnknown);
            }
            else if (snapshot.AvailableMemoryBytes < requiredMemory)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceMemoryInsufficient);
            }
        }

        if (requirements.GpuRequired)
        {
            if (snapshot.VramAvailableBytes is null)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceVramUnknown);
            }
            else if (requirements.VramBytes is { } requiredVram
                     && snapshot.VramAvailableBytes < requiredVram)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceVramInsufficient);
            }
        }

        if (requirements.RequireAcPower)
        {
            if (snapshot.IsOnAcPower is null)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourcePowerUnknown);
            }
            else if (snapshot.IsOnAcPower == false)
            {
                reasons.Add(LocalRuntimeReasonCodes.ResourceOnBattery);
            }
        }

        return reasons.Count == 0
            ? ResourceDecision.Ok
            : new ResourceDecision(false, reasons);
    }
}
