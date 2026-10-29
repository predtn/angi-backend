using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Agent;

/// <summary>Shares ownership-safe resource and lease checks between Agent use cases.</summary>
public static class AgentConversationRules
{
    /// <summary>Extracts the authenticated identity without accepting a user ID from tools or requests.</summary>
    public static int UserId(ICurrentUserService user) => user.UserId ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập.");

    /// <summary>Rejects invalid numeric resource IDs through the standard validation envelope.</summary>
    public static void ValidateId(long id)
    {
        if (id <= 0) throw new ValidationException([new ValidationFailure("id", "ID phải lớn hơn 0.")]);
    }

    /// <summary>Hides missing and other-user conversations behind the same error.</summary>
    public static NotFoundException Missing() => new("CONVERSATION_NOT_FOUND", "Không tìm thấy hội thoại.");

    /// <summary>Enforces idle expiry at request time independently of the cleanup worker.</summary>
    public static void RequireActive(AgentConversation conversation, DateTime now)
    {
        if (conversation.Status != AgentConversationStatus.Active || conversation.ExpiresAt <= now)
            throw new GoneException("CONVERSATION_EXPIRED", "Hội thoại đã hết hạn. Vui lòng mở hội thoại mới.");
    }

    /// <summary>Determines whether an unexpired worker currently owns conversation mutation.</summary>
    public static bool Busy(AgentConversation conversation, DateTime now) => conversation.ProcessingAttemptId.HasValue && conversation.ProcessingUntil > now;

    /// <summary>Clears all fencing fields together after a completed, failed or revoked turn.</summary>
    public static void ClearLease(AgentConversation conversation)
    {
        conversation.ProcessingTurnId = null;
        conversation.ProcessingAttemptId = null;
        conversation.ProcessingUntil = null;
    }

    /// <summary>Compares hard trip values while ignoring navigation focus and a resolved clarification question.</summary>
    public static bool SameConstraints(AgentState left, AgentState right) =>
        AgentJson.Write(CanonicalState(left) with { FocusDay = null, FocusMeal = null, PendingQuestion = null }) ==
        AgentJson.Write(CanonicalState(right) with { FocusDay = null, FocusMeal = null, PendingQuestion = null });

    /// <summary>Normalizes equivalent set, province and numeric representations for retry hashing and constraint comparison.</summary>
    public static AgentState CanonicalState(AgentState state) => state with
    {
        Province = state.Province?.Trim().ToUpperInvariant(), Title = state.Title.Trim(),
        TagIds = state.TagIds.Order().ToArray(), ExcludeTagIds = state.ExcludeTagIds.Order().ToArray(),
        BudgetAmount = Number(state.BudgetAmount), MaxPricePerMeal = Number(state.MaxPricePerMeal),
        OriginLatitude = Number(state.OriginLatitude), OriginLongitude = Number(state.OriginLongitude), RadiusKm = Number(state.RadiusKm),
        PendingStateChange = null, SourceRoadmapVersion = null, HistoryStartSequence = null
    };

    /// <summary>Removes insignificant decimal scale while preserving the exact numeric value.</summary>
    private static decimal? Number(decimal? value) => value.HasValue ? decimal.Parse(value.Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : null;
}
