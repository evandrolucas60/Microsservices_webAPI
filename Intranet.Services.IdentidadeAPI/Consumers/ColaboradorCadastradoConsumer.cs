using BuildingBlocks.Contracts.Identidade;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Consumers;

/// <summary>
/// Conta nova no portal (evento do AuthAPI): cria ou completa o Member + Work + Profile do colaborador
/// com os dados do cadastro, para o card da página de Ramais já nascer preenchido.
/// Idempotente (upsert): o que não existe é criado; o que já existe só tem os campos VAZIOS preenchidos,
/// nunca sobrescrevendo o que o colaborador editou. Corrida com o GetProfileInfo (que cria os registros
/// vazios no primeiro acesso) é resolvida pelos índices únicos: em conflito, relê e tenta de novo.
/// </summary>
public class ColaboradorCadastradoConsumer : IConsumer<ColaboradorCadastrado>
{
    private const int MaxTentativas = 3;

    private readonly AppDbContext _db;
    private readonly ILogger<ColaboradorCadastradoConsumer> _logger;

    public ColaboradorCadastradoConsumer(AppDbContext db, ILogger<ColaboradorCadastradoConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ColaboradorCadastrado> context)
    {
        var msg = context.Message;
        if (string.IsNullOrWhiteSpace(msg.UserId)) return;

        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                await AplicarAsync(msg, context.CancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (tentativa < MaxTentativas)
            {
                // Índice único (Members.UserId, Works.MemberId, Profiles.MemberId): outra requisição
                // criou o registro primeiro. Descarta o que foi montado e recomeça a partir do banco.
                _logger.LogInformation(ex, "Cadastro de {UserId}: registro criado concorrentemente; tentando de novo ({Tentativa})", msg.UserId, tentativa);
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task AplicarAsync(ColaboradorCadastrado msg, CancellationToken ct)
    {
        var member = await _db.Members.FirstOrDefaultAsync(m => m.UserId == msg.UserId, ct);
        if (member is null)
        {
            member = new Member
            {
                MemberId = Guid.NewGuid(),
                UserId = msg.UserId,
                Email = Valor(msg.Email),
                Name = Valor(msg.Nome),
                Phone = Valor(msg.Telefone),
                dateOfMembership = DateTime.UtcNow,
            };
            _db.Members.Add(member);
        }
        else
        {
            // GetProfileInfo cria o membro com nome = e-mail (claim do token): conta como não preenchido
            if (Vazio(member.Name) || string.Equals(member.Name?.Trim(), member.Email?.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(member.Name?.Trim(), msg.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                member.Name = Valor(msg.Nome) ?? member.Name;
            if (Vazio(member.Email)) member.Email = Valor(msg.Email);
            if (Vazio(member.Phone)) member.Phone = Valor(msg.Telefone);
        }

        var work = await _db.Works.FirstOrDefaultAsync(w => w.MemberId == member.MemberId, ct);
        if (work is null)
        {
            work = new Work
            {
                Title = msg.Cargo.Trim(),
                Company = string.Empty,
                Location = msg.Loja.Trim(),
                Registration = string.Empty,
                Description = string.Empty,
                Department = msg.Setor.Trim(),
                Ramal = Valor(msg.Ramal),
                AdmissionDate = null, // preenchida pelo colaborador em Configurações > Trabalho
                MemberId = member.MemberId,
            };
            _db.Works.Add(work);
        }
        else
        {
            if (Vazio(work.Title)) work.Title = msg.Cargo.Trim();
            if (Vazio(work.Department)) work.Department = msg.Setor.Trim();
            if (Vazio(work.Location)) work.Location = msg.Loja.Trim();
            if (Vazio(work.Ramal)) work.Ramal = Valor(msg.Ramal);
        }

        // Profiles.WorkId é obrigatório: o EF insere o Work antes e preenche o WorkId
        if (!await _db.Profiles.AnyAsync(p => p.MemberId == member.MemberId, ct))
        {
            _db.Profiles.Add(new UserProfile
            {
                ProfileId = Guid.NewGuid(),
                MemberId = member.MemberId,
                Work = work,
            });
        }

        var alterados = _db.ChangeTracker.Entries().Count(e => e.State is EntityState.Added or EntityState.Modified);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Cadastro do colaborador {UserId} aplicado ({Alterados} registro(s) criados/completados)", msg.UserId, alterados);
    }

    private static bool Vazio(string? valor) => string.IsNullOrWhiteSpace(valor);

    private static string? Valor(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
