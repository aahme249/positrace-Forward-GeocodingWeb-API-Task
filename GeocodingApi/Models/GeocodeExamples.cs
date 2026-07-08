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
            reqMedia.Example = new OpenApiArray
            {
                new OpenApiString("Apt. 4 456 Yonge St, Toronto, ON M4Y 1X9"),
                new OpenApiString("123-12 Main St, Toronto, ON M5V 2T6"),
                new OpenApiString("Unit 201 789 Queen St W, Toronto, ON M6J 1G1"),
                new OpenApiString("Suite 300 1000 De La Gauchetière W, Montreal, QC H3B 4W5"),
                new OpenApiString("#5 100 Wellington St, Ottawa, ON K1A 0A9"),
                new OpenApiString("99999 Nowhere Blvd, Apt 12, Toronto, ON M5V 3A8"),
                new OpenApiString("Room 412 Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3"),
                new OpenApiString("Unit 5 99999 Nowhere Blvd, Faketown, ON"),
            };
        }

        // ── Response example ──────────────────────────────────────────────────
        if (!operation.Responses.TryGetValue("200", out var response)) return;
        if (!response.Content.TryGetValue("application/json", out var resMedia)) return;

        resMedia.Example = new OpenApiObject
        {
            ["results"] = new OpenApiArray
            {
                Result("Apt. 4 456 Yonge St, Toronto, ON M4Y 1X9",
                       "456 Yonge St, Toronto, ON M4Y 1X9",
                       43.6617504, -79.3834756,
                       "456, Yonge Street, Church-Wellesley, Toronto, Ontario, Canada",
                       "address"),

                Result("123-12 Main St, Toronto, ON M5V 2T6",
                       "123 Main St, Toronto, ON M5V 2T6",
                       strategy: "not_found"),

                Result("Unit 201 789 Queen St W, Toronto, ON M6J 1G1",
                       "789 Queen St W, Toronto, ON M6J 1G1",
                       43.6461764, -79.4084568,
                       "789, Queen Street West, West Queen West, Toronto, Ontario, Canada",
                       "address"),

                Result("Suite 300 1000 De La Gauchetière W, Montreal, QC H3B 4W5",
                       "1000 De La Gauchetière W, Montreal, QC H3B 4W5",
                       45.4983669, -73.566246,
                       "H3B 4W5, Ville-Marie, Montréal, Québec, Canada",
                       "postal_code"),

                Result("#5 100 Wellington St, Ottawa, ON K1A 0A9",
                       "100 Wellington St, Ottawa, ON K1A 0A9",
                       45.4230465, -75.6985103,
                       "100, Wellington Street, Centretown, Ottawa, Ontario, Canada",
                       "address"),

                Result("99999 Nowhere Blvd, Apt 12, Toronto, ON M5V 3A8",
                       "99999 Nowhere Blvd, Toronto, ON M5V 3A8",
                       43.6477776, -79.3951973,
                       "M5V 3A8, Toronto, Ontario, Canada",
                       "postal_code"),

                Result("Room 412 Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3",
                       "Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3",
                       43.6453185, -79.3805025,
                       "M5J 1E3, Toronto, Ontario, Canada",
                       "postal_code"),

                Result("Unit 5 99999 Nowhere Blvd, Faketown, ON",
                       "99999 Nowhere Blvd, Faketown, ON",
                       strategy: "not_found"),
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
