using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace GeocodingApi.Models;

public class GeocodeExampleFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.Name != "Geocode") return;

        // ── Request example ───────────────────────────────────────────────────
        if (operation.RequestBody?.Content.TryGetValue("application/json", out var reqMedia) == true)
        {
            reqMedia.Example = new OpenApiObject
            {
                ["addresses"] = new OpenApiArray
                {
                    new OpenApiString("123-12 Main St, Toronto, ON M5V 2T6"),
                    new OpenApiString("Apt. 4 456 Yonge St, Toronto, ON M4Y 1X9"),
                    new OpenApiString("Unit 201 789 Queen St W, Toronto, ON M6J 1G1"),
                    new OpenApiString("Suite 300 1000 De La Gauchetière W, Montreal, QC H3B 4W5"),
                    new OpenApiString("#5 100 Wellington St, Ottawa, ON K1A 0A9"),
                    new OpenApiString("Room 412 Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3"),
                    new OpenApiString("99999 Nowhere Blvd, Apt 12, Toronto, ON M5V 3A8"),
                    new OpenApiString("Unit 5 99999 Nowhere Blvd, Faketown, ON"),
                    new OpenApiString("CN Tower, 290 Bremner Blvd, Toronto, ON M5V 3L9"),
                    new OpenApiString("Union Station, 65 Front St W, Toronto, ON M5J 1E6"),
                    new OpenApiString("1 Hospital Rd, Iqaluit, NU X0A 0H0"),
                    new OpenApiString("Apt 3 2568 Granville St, Vancouver, BC V6H 3G8"),
                    new OpenApiString("100 Queen St W, Toronto, ON M5H 2N2"),
                    new OpenApiString("100 Queen St W, Toronto, ON M5H 2N2"),
                }
            };
        }

        // ── Response example ──────────────────────────────────────────────────
        if (!operation.Responses.TryGetValue("200", out var response)) return;
        if (!response.Content.TryGetValue("application/json", out var resMedia)) return;

        resMedia.Example = new OpenApiObject
        {
            ["results"] = new OpenApiArray
            {
                // 1. dash-unit strip (unit 123, civic 12 per Canada Post convention) → not_found (fake street number)
                Result("123-12 Main St, Toronto, ON M5V 2T6",
                       "12 Main St, Toronto, ON M5V 2T6",
                       strategy: "not_found"),

                // 2. Apt. qualifier → address
                Result("Apt. 4 456 Yonge St, Toronto, ON M4Y 1X9",
                       "456 Yonge St, Toronto, ON M4Y 1X9",
                       43.6617504, -79.3834756,
                       "456, Yonge Street, Church-Wellesley, Toronto, Ontario, Canada",
                       "address"),

                // 3. Unit qualifier → address
                Result("Unit 201 789 Queen St W, Toronto, ON M6J 1G1",
                       "789 Queen St W, Toronto, ON M6J 1G1",
                       43.6461764, -79.4084568,
                       "789, Queen Street West, West Queen West, Toronto, Ontario, Canada",
                       "address"),

                // 4. Suite qualifier → postal_code fallback
                Result("Suite 300 1000 De La Gauchetière W, Montreal, QC H3B 4W5",
                       "1000 De La Gauchetière W, Montreal, QC H3B 4W5",
                       45.4983669, -73.566246,
                       "H3B 4W5, Ville-Marie, Montréal, Québec, Canada",
                       "postal_code"),

                // 5. # qualifier → address
                Result("#5 100 Wellington St, Ottawa, ON K1A 0A9",
                       "100 Wellington St, Ottawa, ON K1A 0A9",
                       45.4230465, -75.6985103,
                       "100, Wellington Street, Centretown, Ottawa, Ontario, Canada",
                       "address"),

                // 6. Room qualifier → postal_code fallback
                Result("Room 412 Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3",
                       "Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3",
                       43.6453185, -79.3805025,
                       "M5J 1E3, Toronto, Ontario, Canada",
                       "postal_code"),

                // 7. fake street, real postal code → postal_code fallback
                Result("99999 Nowhere Blvd, Apt 12, Toronto, ON M5V 3A8",
                       "99999 Nowhere Blvd, Toronto, ON M5V 3A8",
                       43.6477776, -79.3951973,
                       "M5V 3A8, Toronto, Ontario, Canada",
                       "postal_code"),

                // 8. fake street, no postal code → not_found
                Result("Unit 5 99999 Nowhere Blvd, Faketown, ON",
                       "99999 Nowhere Blvd, Faketown, ON",
                       strategy: "not_found"),

                // 9. landmark → address
                Result("CN Tower, 290 Bremner Blvd, Toronto, ON M5V 3L9",
                       "CN Tower, 290 Bremner Blvd, Toronto, ON M5V 3L9",
                       43.6425662, -79.3870568,
                       "CN Tower, 290, Bremner Boulevard, CityPlace, Toronto, Ontario, Canada",
                       "address"),

                // 10. transit hub → address
                Result("Union Station, 65 Front St W, Toronto, ON M5J 1E6",
                       "Union Station, 65 Front St W, Toronto, ON M5J 1E6",
                       43.6452578, -79.3806535,
                       "Union Station, 65, Front Street West, Toronto, Ontario, Canada",
                       "address"),

                // 11. remote place (Iqaluit) → postal_code fallback
                Result("1 Hospital Rd, Iqaluit, NU X0A 0H0",
                       "1 Hospital Rd, Iqaluit, NU X0A 0H0",
                       63.7467269, -68.5169943,
                       "X0A 0H0, Iqaluit, Nunavut, Canada",
                       "postal_code"),

                // 12. Vancouver address with Apt → address
                Result("Apt 3 2568 Granville St, Vancouver, BC V6H 3G8",
                       "2568 Granville St, Vancouver, BC V6H 3G8",
                       49.2630996, -123.1402742,
                       "2568, Granville Street, South Granville, Vancouver, British Columbia, Canada",
                       "address"),

                // 13. duplicate #1 → address (Nominatim called once)
                Result("100 Queen St W, Toronto, ON M5H 2N2",
                       "100 Queen St W, Toronto, ON M5H 2N2",
                       43.6536032, -79.3840055,
                       "Toronto City Hall, 100, Queen Street West, Toronto, Ontario, Canada",
                       "address"),

                // 14. duplicate #2 → address (served from dedup / cache, no extra Nominatim call)
                Result("100 Queen St W, Toronto, ON M5H 2N2",
                       "100 Queen St W, Toronto, ON M5H 2N2",
                       43.6536032, -79.3840055,
                       "Toronto City Hall, 100, Queen Street West, Toronto, Ontario, Canada",
                       "address"),
            }
        };
    }

    private static OpenApiObject Result(
        string original, string normalized,
        double? lat = null, double? lon = null,
        string? displayName = null,
        string strategy = "not_found") =>
        new()
        {
            ["originalAddress"]   = new OpenApiString(original),
            ["normalizedAddress"] = new OpenApiString(normalized),
            ["latitude"]          = lat.HasValue ? new OpenApiDouble(lat.Value) : new OpenApiNull(),
            ["longitude"]         = lon.HasValue ? new OpenApiDouble(lon.Value) : new OpenApiNull(),
            ["displayName"]       = displayName != null ? new OpenApiString(displayName) : new OpenApiNull(),
            ["strategy"]          = new OpenApiString(strategy),
            ["found"]             = new OpenApiBoolean(lat.HasValue),
            ["error"]             = new OpenApiNull(),
        };
}
