namespace ANGI.Application.Common.Models.Agent;

/// <summary>One public restaurant's location, used to recognize places from catalog addresses without a map service.</summary>
public sealed record AgentCatalogPlace(string Province, string? Address, decimal Latitude, decimal Longitude);

/// <summary>A map search result; it is only a candidate until its name is checked against what the user wrote.</summary>
public sealed record AgentPlaceCandidate(string Name, string? Address, string? Type, decimal Latitude, decimal Longitude);

/// <summary>A place the user named, located and sized so that enough restaurants fall inside the radius.</summary>
public sealed record AgentPlaceMatch(string Province, string Label, decimal Latitude, decimal Longitude, decimal RadiusKm, string? District = null);

/// <summary>A complaint that a day is in the wrong place: which days it names, and the right place when the message states one.</summary>
public sealed record AgentPlaceCorrection(short[] Days, string? Place);

/// <summary>A place named for one or more days of the trip.</summary>
public sealed record AgentItineraryStop(short[] Days, string Place, bool Explicit);

/// <summary>What one message says about where the trip goes, after each place was looked up.</summary>
/// <param name="Province">The province these places belong to, when known.</param>
/// <param name="DayAreas">Days bound to a place.</param>
/// <param name="TripArea">One area named for the whole trip, without any day.</param>
/// <param name="WholeProvinceDays">Days the user set to the whole province by naming it.</param>
/// <param name="Unresolved">Places that could not be located or have no restaurant nearby.</param>
/// <param name="LastDay">The last day the itinerary names, or zero when it does not extend the trip.</param>
public sealed record AgentTripPlaces(string? Province, AgentDayArea[] DayAreas, AgentPlaceMatch? TripArea, short[] WholeProvinceDays,
    string[] Unresolved, short LastDay);
