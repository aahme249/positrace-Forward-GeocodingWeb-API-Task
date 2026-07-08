using System.ComponentModel.DataAnnotations;

namespace GeocodingApi.Models;

public record GeocodeRequest(
    [Required, MinLength(1)] List<string> Addresses
);
