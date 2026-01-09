namespace Intranet.Services.IdentidadeAPI.Models;

public abstract class Person
{
    public string? Name { get; set; }
    public Address? Address  { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}
