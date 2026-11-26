namespace ANGI.Application.Common.Models.Discovery;

/// <summary>
/// A circle around a point, shared by every "restaurants near me" filter (DISH-01, DISH-03, RM-06, RM-10): the database
/// narrows with <see cref="BoundingBox"/>, then <see cref="Contains"/> keeps the restaurants truly within the radius.
/// An Agent zone ("nội đô") adds more circles in <see cref="Also"/>: a restaurant inside any of them is inside the area.
/// </summary>
public sealed record NearbyArea(decimal Latitude, decimal Longitude, decimal RadiusKm)
{
    private const double EarthRadiusKm = 6371.0;
    private const double KmPerDegreeLatitude = 111.32;

    /// <summary>More circles of the same area, for a zone made of several places; null for a single circle.</summary>
    public NearbyArea[]? Also { get; init; }

    /// <summary>Every circle of the area, this one first.</summary>
    public IEnumerable<NearbyArea> Circles() => Also is { Length: > 0 } more ? [this with { Also = null }, .. more] : [this];

    /// <summary>Latitude and longitude limits that contain every circle (longitude unbounded near the poles).</summary>
    public (decimal MinLatitude, decimal MaxLatitude, decimal MinLongitude, decimal MaxLongitude) BoundingBox()
    {
        var boxes = Circles().Select(circle => circle.OwnBox()).ToArray();
        return (boxes.Min(box => box.MinLatitude), boxes.Max(box => box.MaxLatitude), boxes.Min(box => box.MinLongitude), boxes.Max(box => box.MaxLongitude));
    }

    private (decimal MinLatitude, decimal MaxLatitude, decimal MinLongitude, decimal MaxLongitude) OwnBox()
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

    /// <summary>Whether a point lies within the radius of any circle (great-circle distance).</summary>
    public bool Contains(decimal latitude, decimal longitude) =>
        Circles().Any(circle => DistanceKm(circle.Latitude, circle.Longitude, latitude, longitude) <= (double)circle.RadiusKm);

    /// <summary>Distance from a point to the nearest centre of the area.</summary>
    public double DistanceTo(decimal latitude, decimal longitude) => Circles().Min(circle => DistanceKm(circle.Latitude, circle.Longitude, latitude, longitude));

    /// <summary>The same area with every circle set to this radius, used when a meal searches further out.</summary>
    public NearbyArea WithRadius(decimal radiusKm) => this with { RadiusKm = radiusKm, Also = Also?.Select(circle => circle with { RadiusKm = radiusKm }).ToArray() };

    /// <summary>The same area with no circle wider than this radius; a narrower circle keeps its own.</summary>
    public NearbyArea Tightened(decimal radiusKm) => this with
    {
        RadiusKm = Math.Min(RadiusKm, radiusKm), Also = Also?.Select(circle => circle with { RadiusKm = Math.Min(circle.RadiusKm, radiusKm) }).ToArray()
    };

    /// <summary>Two areas are equal when they hold the same circles in the same order.</summary>
    public bool Equals(NearbyArea? other) => other is not null && Latitude == other.Latitude && Longitude == other.Longitude && RadiusKm == other.RadiusKm &&
        (Also ?? []).SequenceEqual(other.Also ?? []);

    public override int GetHashCode() => HashCode.Combine(Latitude, Longitude, RadiusKm, Also?.Length ?? 0);

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
