using ANGI.Application.Common.Models.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Discovery;

/// <summary>Verifies an area of several circles, used by the Agent for a zone such as "nội đô", while one circle behaves as before.</summary>
public sealed class NearbyAreaTests
{
    private static readonly NearbyArea Zone = new(21.03m, 105.85m, 2m) { Also = [new NearbyArea(21.03m, 105.79m, 2m)] };

    // TEST-01: A point inside any circle is inside the area; a point between two circles is not, and the box holds both circles.
    /// <summary>The zone is its places, not one big circle around them.</summary>
    [Fact]
    public void Contains_PointInAnyCircle_IsInside()
    {
        Zone.Contains(21.03m, 105.851m).Should().BeTrue();
        Zone.Contains(21.03m, 105.791m).Should().BeTrue();
        Zone.Contains(21.03m, 105.82m).Should().BeFalse();
        var box = Zone.BoundingBox();
        box.MinLongitude.Should().BeLessThan(105.79m);
        box.MaxLongitude.Should().BeGreaterThan(105.85m);
    }

    // TEST-02: Two areas holding the same circles are equal even as different instances, so an area read back from storage is not taken as moved.
    /// <summary>Record equality would compare the circle arrays by reference.</summary>
    [Fact]
    public void Equals_SameCirclesInOtherInstances_AreEqual()
    {
        var copy = new NearbyArea(21.03m, 105.85m, 2m) { Also = [new NearbyArea(21.03m, 105.79m, 2m)] };

        copy.Should().Be(Zone);
        (copy == Zone).Should().BeTrue();
        (Zone == new NearbyArea(21.03m, 105.85m, 2m)).Should().BeFalse("the single circle is a different area");
    }

    // TEST-03: Widening sets every circle; tightening narrows only circles wider than the limit; the distance is to the nearest centre.
    /// <summary>A meal searched further out widens the whole zone, and a radius the user set caps every place of it.</summary>
    [Fact]
    public void WithRadiusAndTightened_ChangeEveryCircle()
    {
        Zone.WithRadius(8m).Circles().Should().OnlyContain(circle => circle.RadiusKm == 8m);
        var mixed = new NearbyArea(21.03m, 105.85m, 5m) { Also = [new NearbyArea(21.03m, 105.79m, 1m)] };
        mixed.Tightened(3m).Circles().Select(circle => circle.RadiusKm).Should().Equal(3m, 1m);
        Zone.DistanceTo(21.03m, 105.79m).Should().BeLessThan(0.01);
    }
}
