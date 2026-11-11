namespace ANGI.Application.Common.Models.Discovery;

/// <summary>
/// A circle around a point, shared by every "restaurants near me" filter (DISH-01, DISH-03, RM-06, RM-10): the database
/// narrows with <see cref="BoundingBox"/>, then <see cref="Contains"/> keeps the restaurants truly within the radius.
/// </summary>
public sealed record NearbyArea(decimal Latitude, decimal Longitude, decimal RadiusKm)
{
    private const double EarthRadiusKm = 6371.0;
    private const double KmPerDegreeLatitude = 111.32;

    /// <summary>Latitude and longitude limits that contain the whole circle (longitude unbounded near the poles).</summary>
    public (decimal MinLatitude, decimal MaxLatitude, decimal MinLongitude, decimal MaxLongitude) BoundingBox()
    {
        var deltaLatitude = (double)RadiusKm / KmPerDegreeLatitude;
        var cosLatitude = Math.Cos((double)Latitude * Math.PI / 180.0);
        var deltaLongitude = cosLatitude < 0.01 ? 180.0 : (double)RadiusKm / (KmPerDegreeLatitude * cosLatitude);
        return (
            Latitude - (decimal)deltaLatitude,
            Latitude + (decimal)deltaLatitude,
            Longitude - (decimal)deltaLongitude,
            Longitude + (decimal)deltaLongitude);
    }

    /// <summary>Whether a point lies within the radius (great-circle distance).</summary>
    public bool Contains(decimal latitude, decimal longitude) =>
        DistanceKm(Latitude, Longitude, latitude, longitude) <= (double)RadiusKm;

    /// <summary>Haversine distance in kilometres.</summary>
    public static double DistanceKm(decimal latitude1, decimal longitude1, decimal latitude2, decimal longitude2)
    {
        var dLatitude = (double)(latitude2 - latitude1) * Math.PI / 180.0;
        var dLongitude = (double)(longitude2 - longitude1) * Math.PI / 180.0;
        var a = Math.Pow(Math.Sin(dLatitude / 2), 2) +
                Math.Cos((double)latitude1 * Math.PI / 180.0) * Math.Cos((double)latitude2 * Math.PI / 180.0) *
                Math.Pow(Math.Sin(dLongitude / 2), 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }
}
