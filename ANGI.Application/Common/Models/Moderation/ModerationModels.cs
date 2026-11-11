using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Moderation;

/// <summary>Contains only the restaurant fields needed to build a moderator list card.</summary>
public sealed record ModRestaurantListItem(
    Domain.Entities.Restaurant Restaurant, int PendingReportCount);

/// <summary>Identifies the immutable report target before acquiring transaction locks.</summary>
public sealed record ReportTarget(ReportTargetType Type, long Id);

/// <summary>Collects report display data separately from its tracked state.</summary>
public sealed record ReportDisplayData(
    string TargetName,
    IReadOnlyDictionary<int, User> Users,
    IReadOnlyDictionary<long, MediaFile> Evidence,
    int OpenReportCount);
