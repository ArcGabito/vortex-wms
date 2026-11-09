using VortexWms.Core.Domain;
using VortexWms.Core.Integration;
using VortexWms.Core.Services;

namespace VortexWms.Core.Tests;

/// <summary>Fase 5a: conteos cíclicos, ciegos, con recuento y aprobación; ubicaciones congeladas; ABC y exactitud.</summary>
public sealed class CountsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new("AAAA");
    private readonly TestWorkspace _b = new("BBBB");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _b.InitializeAsync();
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(_t.Coffee, 40, 6m), new DocumentLine(_t.Sugar, 10, 2m)]);
    }

    public async Task DisposeAsync()
    {
        await _t.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static async Task ReplicateAsync(TestWorkspace from, TestWorkspace to)
    {
        foreach (var e in (await from.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
            await to.Wms.SyncHandlers.HandleRecordUpsertedAsync(new VortexApi.Client.VortexEvent(e.Id, 1, e.EventType!, "WMS", 1, e.SubjectType,
                e.SubjectId, e.CreatedAt, e.CreatedAt, null, e.Payload!.Value));
    }

    [Fact]
    public async Task Conteo_ciego_con_recuento_que_ajusta_solo_al_aprobar_y_respeta_lo_que_se_movio_mientras_tanto()
    {
        var id = await _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Items, ItemIds: [_t.Coffee, _t.Sugar], RecountPercent: 5));
        _t.Context.Role = WmsPermissions.Operator;
        var view = (await _t.Wms.Counts.GetAsync(id))!;
        Assert.All(view.Lines, l => Assert.Null(l.Expected));   // ciego: el operario no ve lo esperado

        var coffee = view.Lines.Single(l => l.Line.ItemId == _t.Coffee).Line.LineNo;
        var sugar = view.Lines.Single(l => l.Line.ItemId == _t.Sugar).Line.LineNo;
        await _t.Wms.Counts.RecordAsync(id, coffee, 36);   // 10 % de diferencia: pide recontar
        await _t.Wms.Counts.RecordAsync(id, sugar, 10);
        Assert.Equal(CountLineStatuses.Recount, (await _t.Wms.Counts.GetAsync(id))!.Lines.Single(l => l.Line.LineNo == coffee).Status);
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Counts.SubmitAsync(id));
        await _t.Wms.Counts.RecordAsync(id, coffee, 37);   // el recuento vale

        // Mientras se cuenta salen 5 cafés: no se pierden al ajustar.
        _t.Context.Role = WmsPermissions.Supervisor;
        await _t.Wms.Inventory.AdjustAsync(_t.Central, _t.Coffee, null, -5, AdjustmentReasons.Damage, null);
        _t.Context.Role = WmsPermissions.Operator;
        await _t.Wms.Counts.SubmitAsync(id);
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Counts.ApproveAsync(id));

        _t.Context.Role = WmsPermissions.Supervisor;
        var review = (await _t.Wms.Counts.GetAsync(id))!;
        Assert.Equal((CountStatuses.Review, -3m), (review.Count.Status, review.Lines.Single(l => l.Line.LineNo == coffee).Difference!.Value));
        Assert.Equal(1, await _t.Wms.Counts.ApproveAsync(id));
        Assert.Equal((32m, 10m), (await _t.OnHandAsync(_t.Central, _t.Coffee), await _t.OnHandAsync(_t.Central, _t.Sugar)));

        var accuracy = await _t.Wms.Counts.GetAccuracyAsync(_t.Central);
        Assert.Equal((2, 1, 50m, -18m), (accuracy.Lines, accuracy.Exact, accuracy.Percent, accuracy.AdjustedValue));   // 3 cafés a 6
    }

    [Fact]
    public async Task Las_ubicaciones_contadas_se_congelan_y_un_articulo_no_entra_en_dos_conteos()
    {
        var a = await _t.Wms.Locations.SaveAsync(null, new LocationDraft(_t.Central, "A-01", LocationKinds.Storage));
        var b = await _t.Wms.Locations.SaveAsync(null, new LocationDraft(_t.Central, "B-01", LocationKinds.Storage));
        await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, null, a, 20);

        var id = await _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Locations, LocationIds: [a],
            AssigneeId: Guid.Parse("00000000-0000-0000-0000-000000000001"), AssigneeName: "Luis"));
        var count = (await _t.Wms.Counts.GetAsync(id))!;
        var line = Assert.Single(count.Lines);
        Assert.Equal(_t.Coffee, line.Line.ItemId);
        Assert.Equal(["A-01"], line.Line.Locations);
        var ex = await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, a, b, 5));
        Assert.Contains(count.Count.Number, ex.Message);
        await Assert.ThrowsAsync<WmsValidationException>(() =>
            _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Items, ItemIds: [_t.Coffee])));
        Assert.Single(await _t.Wms.Tasks.GetTasksAsync(false), t => t.Kind == TaskKinds.Count);

        await _t.Wms.Counts.CancelAsync(id);
        await _t.Wms.Locations.RelocateAsync(_t.Central, _t.Coffee, a, b, 5);
        Assert.DoesNotContain(await _t.Wms.Tasks.GetTasksAsync(false), t => t.Kind == TaskKinds.Count);
    }

    [Fact]
    public async Task Dos_equipos_cuentan_lineas_distintas_sin_conexion_y_se_suman()
    {
        var id = await _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Full, Blind: false, RecountPercent: null));
        await ReplicateAsync(_t, _b);
        var lines = (await _b.Wms.Counts.GetAsync(id))!.Lines;
        Assert.Equal(2, lines.Count);

        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        _b.Clock.Advance(TimeSpan.FromMinutes(2));
        await _t.Wms.Counts.RecordAsync(id, lines[0].Line.LineNo, lines[0].Expected!.Value);
        await _b.Wms.Counts.RecordAsync(id, lines[1].Line.LineNo, lines[1].Expected!.Value + 1);
        await ReplicateAsync(_b, _t);

        var merged = (await _t.Wms.Counts.GetAsync(id))!;
        Assert.All(merged.Lines, l => Assert.Equal(CountLineStatuses.Done, l.Status));
        Assert.Equal(1, merged.Count.Differences);
    }

    [Fact]
    public async Task La_clase_ABC_sale_del_valor_despachado_y_marca_lo_que_toca_contar()
    {
        var tea = Guid.NewGuid();
        await _t.Wms.Master.ApplyItemAsync(TestWorkspace.Item(tea, "TE-01", "Té verde"));
        await _t.Wms.Receipts.ReceiveDirectAsync(_t.Central, null, [new DocumentLine(tea, 5, 1m)]);
        await _t.Wms.Inventory.TransferAsync(_t.Central, _t.Store, [new DocumentLine(_t.Coffee, 30), new DocumentLine(_t.Sugar, 5),
            new DocumentLine(tea, 1)], null);   // 180, 10 y 1 de valor
        var abc = await _t.Wms.Counts.GetAbcAsync(_t.Central);
        Assert.Equal([("A", true), ("B", true), ("C", true)], abc.Select(a => (a.Class, a.Due)));
        Assert.Equal(_t.Coffee, abc[0].ItemId);

        var id = await _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Abc, Abc: "A"));
        var line = Assert.Single((await _t.Wms.Counts.GetAsync(id))!.Lines);
        await _t.Wms.Counts.RecordAsync(id, line.Line.LineNo, 10);
        await _t.Wms.Counts.SubmitAsync(id);
        await _t.Wms.Counts.ApproveAsync(id);
        Assert.False((await _t.Wms.Counts.GetAbcAsync(_t.Central))[0].Due);   // contado hoy: el próximo, en 30 días
        await Assert.ThrowsAsync<WmsValidationException>(() => _t.Wms.Counts.CreateAsync(new CountDraft(_t.Central, CountScopes.Abc, Abc: "A")));
    }
}
