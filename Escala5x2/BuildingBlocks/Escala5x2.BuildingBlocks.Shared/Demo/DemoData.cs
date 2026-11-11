namespace Escala5x2.BuildingBlocks.Shared.Demo;

/// <summary>
/// Identificadores fixos da loja de demonstração, para que Store e Employee criem dados
/// coerentes sem consultar o banco um do outro. Ativado por Seed:Demo=true.
/// </summary>
public static class DemoData
{
    public static readonly Guid StoreId = Guid.Parse("8f0c5a3e-1d2b-4c6a-9e7f-0a1b2c3d4e01");
    public static readonly Guid SectorCaixa = Guid.Parse("8f0c5a3e-1d2b-4c6a-9e7f-0a1b2c3d4e11");
    public static readonly Guid ShiftManha = Guid.Parse("8f0c5a3e-1d2b-4c6a-9e7f-0a1b2c3d4e21");
    public static readonly Guid ShiftTarde = Guid.Parse("8f0c5a3e-1d2b-4c6a-9e7f-0a1b2c3d4e22");

    /// <summary>Sete pessoas do exemplo do Documento de Alinhamento; Ana e Gabi com regra quinzenal.</summary>
    public static readonly (string Registration, string Name, bool Biweekly)[] Employees =
    [
        ("D001", "Ana", true),
        ("D002", "Bruno", false),
        ("D003", "Carla", false),
        ("D004", "Diego", false),
        ("D005", "Elisa", false),
        ("D006", "Fábio", false),
        ("D007", "Gabi", true)
    ];
}
