namespace Intranet.Services.IdentidadeAPI.Models;

public class Address
{
    public int AddressId { get; set; }
    public string? streetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zipcode { get; set; }
    public string? Country { get; set; }
    public int MemberId { get; set; }
}
