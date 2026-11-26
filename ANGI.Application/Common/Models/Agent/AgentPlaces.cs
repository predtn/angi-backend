namespace ANGI.Application.Common.Models.Agent;

/// <summary>One public restaurant's location, used to recognize places from catalog addresses and wards without a map service.</summary>
/// <param name="Ward">The restaurant's ward, a field of its own: production addresses hold only the street ("Phố Hoàng Cầu").</param>
public sealed record AgentCatalogPlace(string Province, string? Address, decimal Latitude, decimal Longitude, string? Ward = null);

/// <summary>A map search result; it is only a candidate until its name is checked against what the user wrote.</summary>
public sealed record AgentPlaceCandidate(string Name, string? Address, string? Type, decimal Latitude, decimal Longitude);

/// <summary>A place the user named, located and sized so that enough restaurants fall inside the radius.</summary>
/// <param name="Zone">For a zone the model read as several places ("nội đô" = Hoàn Kiếm, Ba Đình…): every located circle, the first one equal to this match; null for one place.</param>
/// <param name="Missed">For a zone: the places the model listed that could not be located.</param>
public sealed record AgentPlaceMatch(string Province, string Label, decimal Latitude, decimal Longitude, decimal RadiusKm, string? District = null,
    AgentZoneCircle[]? Zone = null, string[]? Missed = null);

/// <summary>A place named for one or more days of the trip, with the kind and parts the model read for it.</summary>
public sealed record AgentItineraryStop(short[] Days, string Place, bool Explicit, string? Kind = null, string[]? Parts = null);

/// <summary>What the backend found for a zone the model read as several places; the model words it for the user.</summary>
/// <param name="Label">The zone as the user named it.</param>
/// <param name="Days">The days it applies to; empty for the whole trip.</param>
/// <param name="Located">The places of the zone the search now covers.</param>
/// <param name="Missed">The places of the zone that could not be located.</param>
/// <param name="SearchScope">"zone" when at least one place was located; with none, "province" for a trip (its days use the whole province) and "none" for a suggestion (nothing is searched).</param>
public sealed record AgentZoneReport(string Label, short[] Days, string[] Located, string[] Missed, string SearchScope);

/// <summary>What one message says about where the trip goes, after each place was looked up.</summary>
/// <param name="Province">The province these places belong to, when known.</param>
/// <param name="DayAreas">Days bound to a place.</param>
/// <param name="TripArea">One area named for the whole trip, without any day.</param>
/// <param name="WholeProvinceDays">Days the user set to the whole province by naming it.</param>
/// <param name="Unresolved">Single places that could not be located or have no restaurant nearby.</param>
/// <param name="LastDay">The last day the itinerary names, or zero when it does not extend the trip.</param>
/// <param name="Zones">Zones read as several places, with what was located; the model tells the user, so they are not in the backend note.</param>
/// <param name="MissedDays">Days whose named place (or zone) was not found: they use the whole province, so an earlier place of theirs is dropped.</param>
/// <param name="TripAreaMissed">The area named for the whole trip was not found: the trip uses the whole province instead of an earlier named area.</param>
public sealed record AgentTripPlaces(string? Province, AgentDayArea[] DayAreas, AgentPlaceMatch? TripArea, short[] WholeProvinceDays,
    string[] Unresolved, short LastDay, AgentZoneReport[]? Zones = null, short[]? MissedDays = null, bool TripAreaMissed = false);
