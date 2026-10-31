using System.Globalization;
using System.Text;
using SetSett.Model;

namespace SetSett.Places;

public sealed record City(string Name, string Province, string Country, double Latitude, double Longitude, int Population)
{
    public string Display => string.IsNullOrWhiteSpace(Province) || Province == Name
        ? $"{Name}, {Country}"
        : $"{Name}, {Province}, {Country}";

    public SavedPlace ToPlace() => new(Display, Latitude, Longitude);
}

/// <summary>
/// The offline city list behind the location picker: no permission, no network. Reads the
/// world_cities.csv layout (city, city_ascii, lat, lng, pop, country, iso2, iso3, province), found
/// by header name so extra or reordered columns don't matter.
/// </summary>
public static class CityCatalog
{
    public static IReadOnlyList<City> Parse(TextReader reader)
    {
        List<City> cities = [];
        if (reader.ReadLine() is not { } header)
        {
            return cities;
        }

        List<string> columns = SplitCsvLine(header);
        int Column(string name) => columns.FindIndex(c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));
        int city = Column("city"), ascii = Column("city_ascii"), lat = Column("lat"), lng = Column("lng");
        int pop = Column("pop"), country = Column("country"), province = Column("province");
        if (lat < 0 || lng < 0 || (city < 0 && ascii < 0))
        {
            return cities;
        }

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            List<string> cells = SplitCsvLine(line);
            string Cell(int index) => index >= 0 && index < cells.Count ? cells[index].Trim() : "";

            string name = Cell(city);
            if (string.IsNullOrWhiteSpace(name) || name.Contains('?'))
            {
                name = Cell(ascii); // The source lost some accented names to "?".
            }

            if (string.IsNullOrWhiteSpace(name) ||
                !double.TryParse(Cell(lat), NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
                !double.TryParse(Cell(lng), NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
            {
                continue;
            }

            double.TryParse(Cell(pop), NumberStyles.Float, CultureInfo.InvariantCulture, out double population);
            cities.Add(new City(name, Cell(province), Cell(country), latitude, longitude, (int)Math.Min(population, int.MaxValue)));
        }

        return cities;
    }

    /// <summary>Cities whose name starts with the query first, then ones containing it; bigger cities first within each.</summary>
    public static IReadOnlyList<City> Search(IReadOnlyList<City> cities, string query, int max = 8)
    {
        string q = Fold(query.Trim());
        if (q.Length == 0)
        {
            return [];
        }

        return cities
            .Select(c => (City: c, Rank: Rank(c, q)))
            .Where(r => r.Rank >= 0)
            .OrderBy(r => r.Rank)
            .ThenByDescending(r => r.City.Population)
            .Take(max)
            .Select(r => r.City)
            .ToList();
    }

    /// <summary>The closest city to a location fix, to name it ("Near Milwaukee").</summary>
    public static City? Nearest(IReadOnlyList<City> cities, GeoPoint point) =>
        cities.MinBy(c => PlaceCondition.DistanceMeters(point, new GeoPoint(c.Latitude, c.Longitude)));

    private static int Rank(City city, string q)
    {
        string name = Fold(city.Name);
        if (name.StartsWith(q, StringComparison.Ordinal))
        {
            return 0;
        }

        if (name.Contains(q, StringComparison.Ordinal))
        {
            return 1;
        }

        return Fold(city.Display).Contains(q, StringComparison.Ordinal) ? 2 : -1;
    }

    // Case- and accent-insensitive: "zurich" finds "Zürich".
    private static string Fold(string text)
    {
        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static List<string> SplitCsvLine(string line)
    {
        List<string> result = [];
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }
}
