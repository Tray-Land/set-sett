namespace SetSett.Solar;

/// <summary>Sunrise and sunset for one date. Both are null on a polar day or night; <see cref="AlwaysUp"/> says which.</summary>
public readonly record struct SunTimes(DateTime? SunriseUtc, DateTime? SunsetUtc, bool AlwaysUp);

/// <summary>
/// Sunrise and sunset from the sunrise equation (the NOAA-derived form with refraction and the
/// sun's radius folded into -0.833°). Good to about a minute, which is plenty for switching a
/// theme. Works in UTC; no network, no time zone database.
/// </summary>
public static class SolarCalculator
{
    private const double J2000 = 2451545.0;
    private const double HorizonDegrees = -0.833;
    private const double ObliquityDegrees = 23.4397;

    private static readonly DateTime J2000Utc = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <param name="latitude">Degrees, north positive.</param>
    /// <param name="longitude">Degrees, east positive.</param>
    /// <param name="date">The local calendar date at that place.</param>
    public static SunTimes Calculate(double latitude, double longitude, DateOnly date)
    {
        // Days since J2000 at noon UTC of the date, then shifted to local mean solar noon.
        double n = date.DayNumber - new DateOnly(2000, 1, 1).DayNumber;
        double meanNoon = n - (longitude / 360.0);

        double m = Normalize(357.5291 + (0.98560028 * meanNoon));
        double mRad = Rad(m);
        double center = (1.9148 * Math.Sin(mRad)) + (0.0200 * Math.Sin(2 * mRad)) + (0.0003 * Math.Sin(3 * mRad));
        double lambda = Rad(Normalize(m + center + 180 + 102.9372));
        double transit = J2000 + meanNoon + (0.0053 * Math.Sin(mRad)) - (0.0069 * Math.Sin(2 * lambda));

        double sinDeclination = Math.Sin(lambda) * Math.Sin(Rad(ObliquityDegrees));
        double cosDeclination = Math.Cos(Math.Asin(sinDeclination));
        double phi = Rad(latitude);
        double cosHourAngle = (Math.Sin(Rad(HorizonDegrees)) - (Math.Sin(phi) * sinDeclination)) / (Math.Cos(phi) * cosDeclination);

        if (cosHourAngle > 1)
        {
            return new SunTimes(null, null, AlwaysUp: false); // polar night
        }

        if (cosHourAngle < -1)
        {
            return new SunTimes(null, null, AlwaysUp: true); // midnight sun
        }

        double hourAngleDays = Deg(Math.Acos(cosHourAngle)) / 360.0;
        return new SunTimes(FromJulian(transit - hourAngleDays), FromJulian(transit + hourAngleDays), AlwaysUp: false);
    }

    private static DateTime FromJulian(double julian) => J2000Utc.AddDays(julian - J2000);

    private static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;

    private static double Rad(double degrees) => degrees * Math.PI / 180;

    private static double Deg(double radians) => radians * 180 / Math.PI;
}
